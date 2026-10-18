# Everything search backend (card a7cea6e4)

Branch `feat/everything-search`, worktree `.claude/worktrees/everything-search`.

## Context

Harald wants voidtools Everything as the search backend: Windows Search queries plus the Win32 walk fallback are slow on unindexed trees, while Everything answers from its NTFS index in milliseconds. Upstream has no search-provider abstraction. All search goes through one class, `src/Files.App/Utils/Storage/Search/FolderSearch.cs`, and every caller builds a `FolderSearch` and calls `SearchAsync(results, token)`:

- results page: `Views/Layouts/BaseLayoutPage.cs:608` → `ShellViewModel.SearchAsync(FolderSearch)` (`ViewModels/ShellViewModel.cs:3446`), which streams `SearchTick` batches into the page and then sorts the final list;
- refresh of a results page: `Views/Shells/BaseShellPage.cs:597`;
- Omnibar search-mode suggestions: `ViewModels/UserControls/NavigationToolbarViewModel.cs:1216` (`MaxItemCount = 10`).

`FolderSearch.SearchAsync` (`:91`) dispatches to libraries (each library folder), `Home` (each non-network drive) or one folder, and all three end up in `AddItemsAsync(string folder, …)` (`:491`). That routes tag queries to the file-tag DB, `$…` and `…:…` queries to the Windows Search indexer as AQS, and plain queries to the indexer when the folder is fully indexed, plus a Win32 walk (`SearchWithWin32`, `:550`) otherwise. **`AddItemsAsync` is therefore the single choke point. One hook there gives Everything to every caller (results page, refresh, suggestions, libraries, Home)** without touching the callers.

Machine facts (verified): Everything **1.4.1.1024** is installed and running. `Everything.exe` runs as a service in session 0 and as a user process in session 1, and the IPC window lives in the user process. `es.exe` is in PATH, but the app must not depend on it.

## Key decision: how to talk to Everything

| Option | Verdict |
|---|---|
| Everything SDK DLL (`Everything64.dll`) | Rejected. We would ship a third-party native binary per architecture (x64 + arm64) in the package, and calling it needs `LibraryImport` (CsWin32 can't generate it, since there is no metadata) plus a `DirectPInvoke`/optional-module decision. The DLL is itself only a thin wrapper over the IPC below. |
| Everything 1.5 named pipe (`\\.\pipe\Everything IPC`) | Rejected for now. Only Everything 1.5 (still alpha) serves it, and Harald runs 1.4.1. 1.5 also still serves the 1.4 IPC below. |
| **1.4 WM_COPYDATA `QUERY2` IPC, spoken directly from C#** | **Chosen.** It is what the SDK DLL does internally and works with 1.4 and 1.5. It needs only user32 APIs that CsWin32 generates (no DllImport/LibraryImport, no shipped binary) and is AOT-clean with a static `[UnmanagedCallersOnly]` window procedure. There is precedent in the repo: `Services/PreviewPopupProviders/SeerProProvider.cs` (WM_COPYDATA to another app) and `Files.App.Storage/Windows/Managers/WindowsFolderChangeWatcher.cs` (message-only window). |

I prototyped the protocol in Python ctypes against the running Everything (scratchpad `es_ipc_probe.py`, not committed). It works as specified:

- `FindWindow("EVERYTHING_TASKBAR_NOTIFICATION")` finds the IPC window. `SendMessage(hwnd, WM_USER, 401 /*IS_DB_LOADED*/, 0)` returns 1. Commands 0–3 give the version (1, 4, 1, 1024).
- The query is `WM_COPYDATA` with `dwData = 18` (`EVERYTHING_IPC_COPYDATA_QUERY2W`). The payload is a packed `EVERYTHING_IPC_QUERY2 { DWORD reply_hwnd, reply_copydata_message, search_flags, offset, max_results, request_flags, sort_type }` followed by the null-terminated UTF-16 search.
- The reply arrives **after** `SendMessage` returns, as a `WM_COPYDATA` sent to our window, so the calling thread must pump messages until it arrives. Its `dwData` equals our `reply_copydata_message`. The payload is `EVERYTHING_IPC_LIST2 { DWORD totitems, numitems, offset, request_flags, sort_type }` + `numitems × { DWORD flags (1 = folder, 2 = root), DWORD data_offset }`. Each item's data sits at `data_offset` from the start of the list: for `REQUEST_FULL_PATH_AND_NAME (0x4)` it is `DWORD len` + `len` UTF-16 chars + a null.
- Timings: a scoped query returning 5000 paths takes 17 ms (1.1 MB reply). Unscoped `e` returns 1.8 M total, and the first 100 000 paths take 63 ms. Size and date-modified are indexed and cheap (20 ms per 5000). **Attributes and date-created are not indexed: Everything reads them from disk, about 1.1 s per 5000–10 000 items.**
- Scoping works by prefixing a quoted folder with a trailing backslash: `"C:\Programming\MyFiles\src\" FolderSearch` returns only that subtree. A term containing `\` is matched against the full path, and the trailing `\` excludes the folder itself and sibling folders with the same prefix. A path Everything doesn't index (`"Z:\nonexistent\"`) returns `totitems = 0`.

Consequence for the design: ask Everything **only for full paths** (fast), then stat each result ourselves with `FindFirstFileEx`. The stat costs about what Everything's own disk read would. It gives the attributes we need for Files' hidden/system/dot-file filters, confirms the item still exists (the index can lag by a moment), and lets us reuse FolderSearch's existing `WIN32_FIND_DATAW → ListedItem/ShortcutItem` builders unchanged.

## Design

### Which searches go to Everything

`TrySearchWithEverythingAsync(folder)` handles a folder when **all** of these hold. Otherwise it returns false and the existing code runs unchanged:

1. Setting `UseEverythingForSearch` is on (default **on**).
2. The query is not a tag query (`IsTagQuery(AQSQuery)`: Everything knows nothing about Files' tag DB), does not start with `$` (`$` stays the explicit "Windows Search AQS" escape hatch), and contains no Windows property term (`System.` followed by a property name and `:`, case-insensitive, e.g. `System.FileName:foo*`, `System.Kind:music`).

**Audit of Files-generated query strings (overseer guard):** I grepped every producer of `FolderSearch.Query` / `NavigationArguments.SearchQuery` / `SubmitSearch`:

- Omnibar submit (`UserControls/NavigationToolbar.xaml.cs:262`), the previous-queries list and Omnibar suggestions (`NavigationToolbarViewModel.cs:1216`) all carry raw user text, or a result item's name.
- Tag navigation carries Files-generated `tag:` queries, all from `FolderSearch.FormatTagQuery`: `DetailsLayoutPage.xaml.cs:1188`, `InfoPane.xaml.cs:99`, `WidgetFileTagsContainerItem.cs:112`, `FileTagsManager.cs:51`, `SidebarViewModel.cs:891`, `ModernShellPage.xaml.cs:141`, `NavigationHelpers.cs:502`.
- A results-page refresh re-sends the stored `CurrentSearchQuery` (`BaseShellPage.cs:588`).
- Files has no search chips, date/size/kind filter helpers or saved-search (`.search-ms`) handling that feeds `FolderSearch`.
- The only other generated AQS is `System.FileName:` in two places, neither of which feeds `FolderSearch.Query`: `FolderSearch.AQSQuery` derives it internally from a plain query for the indexer path, and `StorageFileExtensions.cs:333` builds an `ApplicationSearchFilter` for a different API.

The rule is therefore: tag queries, `$…` and any `System.<Prop>:` term go to the existing path, and every other query (raw user text, including `name:value` like `ext:pdf`) goes to Everything. The `System.` guard also future-proofs against generators that might appear upstream, and covers AQS pasted by hand.
3. The folder is a plain fully-qualified file-system path: `Path.IsPathFullyQualified`, and not MTP (`DriveHelpers.IsMtpPath`), FTP (`FtpHelpers.IsFtpPath`), zip (`ZipStorageFolder.IsZipPath`), the Recycle Bin (`Constants.UserEnvironmentPaths.RecycleBinPath` prefix) or a shell/`::{` path.
4. Everything is reachable: its IPC window exists and `IS_DB_LOADED` returns non-zero.
5. Everything covers the folder. When the real query returns 0 items, a probe query `"<folder>\"` with `max_results = 1` must return `totitems > 0`. A non-indexed drive (exFAT USB stick, network share, cloud virtual drive, a folder in Everything's exclude list) therefore falls back to the existing path instead of reporting a false "no results".

**Queries containing `:` that don't start with `$`** (today treated as AQS, e.g. `kind:music`) **go to Everything**, so Everything syntax works (`ext:pdf`, `size:>1mb`, `dm:today`, wildcards, `|`, `!`). AQS-only keywords then become `$kind:music`. *This is a behaviour change, approved by the overseer with the Files-generated-query guard above.* The user's query is passed to Everything verbatim. Default Everything semantics (case-insensitive substring on the name, no path match) match Files' existing Win32 semantics (`*query*` on the name) closely.

Home and libraries need no special code. `AddItemsForHomeAsync` and `AddItemsForLibraryAsync` already call `AddItemsAsync` once per drive or library folder, so each drive or library folder gets its own scoped Everything query with its own coverage check and per-folder fallback.

### Paging, streaming and failure semantics

On a long-running worker thread (same `Task.Factory.StartNew(…, LongRunning).WaitAsync(token)` shape as `SearchWithWin32Async`), the search:

- creates one `EverythingIpcClient` (message-only window) for the whole search;
- pages through results sorted by name ascending (`sort_type = 1`, stable for paging): the first page has 500 items for a fast first paint, later pages 5000, and each page is capped by `UsedMaxItemCount - results.Count` (so suggestions ask for 10);
- stats each path with `FindFirstFileEx` (`FindExInfoBasic`, `FIND_FIRST_EX_LARGE_FETCH`). Like the Win32 walk, it runs the stat behind the existing 5 s timeout pattern when `RequiresFindTimeout(folder)` (network/non-NTFS/reparse roots). It skips items flagged root, items that no longer exist and items the hidden/system/dot-file settings hide (same predicate as `SearchWithWin32` with `hiddenOnly = false`). Items are built with the existing `GetShortcutItem` / `GetListedItemAsync(path, findData)` and added through `AddResult`, so `SearchTick` streaming and shortcut-target resolution work as before;
- calls `RaiseSearchTickIfDue` after each page and stops when a page comes back empty, `offset >= totitems`, the max count is reached or the token is cancelled.

There is **no extra result cap** beyond `MaxItemCount`, matching today's unlimited Win32 walk (see open question 2).

Failure handling: an IPC failure (window gone, send rejected, no reply within 5 s, malformed reply) **before any result was added** returns false, so the existing path runs (logged at Warning). A failure **after** results were added stops with the partial results and logs a Warning, because falling back would duplicate the items already shown. Cancellation behaves like the Win32 path. An unexpected exception from the worker is logged and handled by the same rule: fallback only if no result was added. The `using` on the client disposes the message-only window on every exit path (cancel, timeout, exception), and the window class is registered once per process.

### Files

**New: `src/Files.App/Utils/Storage/Search/EverythingIpcClient.cs`**: `internal sealed unsafe class EverythingIpcClient : IDisposable`, the thread-affine low-level IPC (create and use on one thread):

- `static HWND FindEverythingWindow()`: `PInvoke.FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null)`, then the Everything 1.5 alpha default instance class `"EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"` (untested here, since 1.5 isn't installed).
- `static EverythingStatus GetStatus()`: window found, version (`WM_USER` cmds 0–3), db loaded (cmd 401). Every message uses `SendMessageTimeout(SMTO_ABORTIFHUNG, 1000 ms)`, so a hung Everything never blocks the search.
- `static EverythingIpcClient? TryCreate()`: returns null when Everything isn't reachable or its DB isn't loaded. Otherwise it registers (once per process; `ERROR_CLASS_ALREADY_EXISTS` tolerated) a window class whose `lpfnWndProc = &WndProc`, and creates a message-only window (`HWND_MESSAGE` parent).
- `bool TryQuery(string search, uint offset, uint maxResults, CancellationToken token, out EverythingQueryResult result)`. It builds the QUERY2 buffer (`reply_copydata_message` = per-query incrementing id, so a late reply from a timed-out query is ignored; `request_flags = 0x4`; `sort_type = 1`; `search_flags = 0`) and sends it with `SendMessageTimeout(WM_COPYDATA)`. It then pumps `PeekMessage(PM_REMOVE)`/`DispatchMessage` with `MsgWaitForMultipleObjects(QS_ALLINPUT)` in ≤100 ms slices until the reply arrives, the token is cancelled or 5 s pass, and parses the reply.
- `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static LRESULT WndProc(...)`: on `WM_COPYDATA` whose `wParam` is the Everything window and whose `dwData` equals the pending id, it copies `cbData` bytes into a `[ThreadStatic]` buffer and returns 1. Everything else goes to `DefWindowProc`. The ThreadStatic works because the window and its pump are on the same thread.
- The parser treats the reply as untrusted: every `data_offset`/length is bounds-checked against `cbData`, and any violation fails the query.
- `Dispose` → `DestroyWindow`.
- Records: `readonly record struct EverythingItem(string Path, bool IsFolder, bool IsRoot)`, `sealed record EverythingQueryResult(uint TotalCount, IReadOnlyList<EverythingItem> Items)`, `readonly record struct EverythingStatus(bool IsRunning, bool IsDatabaseLoaded, string? Version)`.

**New: `src/Files.App/Utils/Storage/Search/EverythingSearch.cs`**: `public static class EverythingSearch`, the provider seam for other features (notably batch-2 card 49c16ee3, "address bar doubles as search"):

- `static EverythingStatus GetStatus()`.
- `static string BuildQuery(string userQuery, string? scopeFolder)` → `"<folder with trailing \>" <userQuery>`, or just the query when there is no scope.
- `static Task<EverythingQueryResult?> QueryAsync(string search, uint maxResults, CancellationToken token, uint offset = 0)` runs one query on a dedicated thread (LongRunning) and returns null when Everything is unavailable or fails. Omnibar path-mode text can call it directly for raw path completions. Anything that wants `ListedItem`s keeps using `new FolderSearch { Query, Folder, MaxItemCount }.SearchAsync(list, token)`, which now uses Everything automatically.

**New: `src/Files.App/Utils/Storage/Search/FolderSearch.Everything.cs`**: `public sealed partial class FolderSearch`:

- `public FolderSearchBackends UsedBackends { get; private set; }`, plus `internal static FolderSearchBackends LastResultsPageBackends`, set when a search with `MaxItemCount == 0` (a results-page search) finishes. It is a diagnostic for the automation `search ui=true` check only.
- `private async Task<bool> TrySearchWithEverythingAsync(string folder, IList<ListedItem> results, CancellationToken token)`: the eligibility checks, paging, stat and fallback rules above. It ORs `Everything` into `UsedBackends` when it serves the folder; otherwise it ORs `FileTags` (tag query) or `WindowsSearch` and returns false.
- `private static FindCloseSafeHandle? StatPath(string path, bool useTimeout, out WIN32_FIND_DATAW data, CancellationToken token)`: a single-item twin of the existing `FindFirstFile(folder, useTimeout, …)` (`FolderSearch.cs:618`), without the `\*` suffix.

**New: `src/Files.App/Data/Enums/FolderSearchBackends.cs`**: `[Flags] public enum FolderSearchBackends { None = 0, Everything = 1, WindowsSearch = 2, FileTags = 4 }`. It is a flags enum because a Home or library search can mix backends per drive or folder.

**Edit (upstream hot file, 4-line diff): `src/Files.App/Utils/Storage/Search/FolderSearch.cs`**

- `:20` `public sealed class FolderSearch` → `public sealed partial class FolderSearch`.
- `:491` `AddItemsAsync`: first statement `if (await TrySearchWithEverythingAsync(folder, results, token)) return;`. Nothing else in the upstream file changes. The partial reuses its private members (`AddResult`, `RaiseSearchTickIfDue`, `GetShortcutItem`, `GetListedItemAsync(string, WIN32_FIND_DATAW)`, `RequiresFindTimeout`, `UsedMaxItemCount`, `IsTagQuery`, `AQSQuery`, `UserSettingsService`).

**Edit: `src/Files.App.CsWin32/NativeMethods.txt`** (shared): add `SendMessageTimeout`, `PeekMessage`, `DispatchMessage`, `MsgWaitForMultipleObjects`, `HWND_MESSAGE` right after `SendMessage` (`:136`). `WM_*` (`:32`), `FindWindow`, `RegisterClassEx`, `CreateWindowEx`, `DestroyWindow`, `DefWindowProc`, `GetModuleHandle`, `COPYDATASTRUCT`, `WNDCLASSEXW`, `FindFirstFileEx` are already listed. If CsWin32 can't emit `HWND_MESSAGE` as a constant, use `new HWND(-3)` with a one-line comment instead. No DllImport/LibraryImport, and no new native module (user32 is already direct-bound).

**Setting (shared resources: settings service, Advanced page, Resources.resw)**

- `Data/Contracts/IGeneralSettingsService.cs`, after `ShowSystemTrayIcon` (`:291`): `bool UseEverythingForSearch { get; set; }` with a doc comment.
- `Services/Settings/GeneralSettingsService.cs`, after `ShowSystemTrayIcon` (`:354`): `get => Get(true); set => Set(value);`. It is a `bool`, so nothing is added to `UserSettingsJsonSerializationContext`.
- `ViewModels/Settings/AdvancedViewModel.cs`, after `ShowSystemTrayIcon` (`:339`): a pass-through property, same pattern.
- `Views/Settings/AdvancedPage.xaml`, after the System Tray Icon card (`:109–114`), before "Experimental Settings": a `SettingsCard` with header `UseEverythingForSearch`, description `UseEverythingForSearchDescription`, a search glyph `&#xE721;` and a `ToggleSwitch` bound TwoWay. The Settings search index picks it up automatically (it walks the page's SettingsCards).
- `Strings/en-US/Resources.resw`, right after `ShowSystemTrayIcon` (`:4353`): `UseEverythingForSearch` = "Search with Everything", `UseEverythingForSearchDescription` = "Use voidtools Everything for search when it's running. Folders Everything doesn't index, tag searches and queries starting with $ still use Windows Search."

Why Advanced: it already holds the other integration and app-behaviour switches (system tray, Open/Save dialog replacement). The toggle defaults on because Harald asked for Everything as the backend. The toggle is the "way back" his guiding principle wants.

**Automation (shared): `src/Files.App/Automation/AutomationHandlers.cs`**: a `Methods` entry plus a `"search" => SearchAsync(p)` case:

- `search`. Params: `query` (required), `path` (default: the active pane's working directory; `Home` allowed), `max` (FolderSearch `MaxItemCount`, default 100; a non-zero max makes FolderSearch also load suggestion icons for every result, so a large default would inflate `elapsedMs`), `limit` (paths returned, default 20), `ui` (default false).
  - Headless (`ui=false`) runs a standalone `FolderSearch` and returns `{ backends: "Everything" | "WindowsSearch" | "Everything, WindowsSearch" | …, count, elapsedMs, everything: { running, databaseLoaded, version }, paths: [first `limit`] }`.
  - `ui=true` calls the active pane's `SubmitSearch(query)` (after navigating to `path` if given), waits until the pane's view model reports `IsSearchResults` and `!IsLoadingItems` (`WaitUntilAsync`, 30 s), and returns `{ backends: FolderSearch.LastResultsPageBackends, count: FilesAndFolders.Count, paths }`.
- Results are built with `System.Text.Json.Nodes`, with `(JsonNode)` casts into `JsonArray`.

### Constraints that shaped the design

- Interop and AOT rules: CsWin32 only, no DllImport/ComImport/LibraryImport, no reflection, no shipped third-party binaries. The window procedure is a static `[UnmanagedCallersOnly]` function pointer, not a marshalled delegate.
- Smallest upstream diff: `FolderSearch.cs` changes by two lines. All fork logic lives in new files, so upstream merges stay cheap.
- Semantics preserved: folder plus subfolders, the same hidden/system/dot-file filters, the same `ListedItem`/`ShortcutItem` shapes and streaming, and fallback per folder whenever Everything is off, unreachable, still loading its DB, hung, or not indexing that folder.
- Never a false "no results": an empty Everything answer is trusted only after the coverage probe.
- No dependency on `es.exe` or the SDK DLL. A hung Everything costs at most about 1 s (status) plus 5 s (reply) before the fallback.

## Verification

**Mine (build):** `msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly` from the worktree must exit 0 with no new IL2xxx/IL3xxx warnings in the new files. The cold build of the untouched branch already passes (EXIT=0).

**Overseer (automation pipe, on merged main, Everything 1.4 running):**

1. `python .claude/scripts/files_auto.py search query=FolderSearch path=C:\Programming\MyFiles\src`: `backends` = `Everything`, `count` ≥ 2, and `paths` contains `...\Utils\Storage\Search\FolderSearch.cs` and `FolderSearch.Everything.cs`. `elapsedMs` should be small (under about 300 ms).
2. Scope: `search query=FolderSearch path=C:\Programming\MyFiles\src\Files.App\Views`: `backends` = `Everything`, `count` = 0 (the coverage probe succeeds, so there is no fallback).
3. Fallback by setting: `settings.set 'values={"UseEverythingForSearch": false}'`, then repeat check 1. Expect `backends` = `WindowsSearch` and FolderSearch.cs still found. Then `settings.set 'values={"UseEverythingForSearch": true}'`.
4. Escape hatch: `search 'query=$System.FileName:FolderSearch*' path=C:\Programming\MyFiles\src` gives `WindowsSearch`. The tag query `search query=tag:Red path=C:\` gives `FileTags`.
5. Everything syntax: `search query=ext:resw path=C:\Programming\MyFiles\src` gives `Everything`, and every path ends in `.resw`.
6. Hidden filter: with `settings.set 'values={"ShowHiddenItems": false}'`, `search query=desktop.ini path=C:\Users\coami` gives `count` 0. With `ShowHiddenItems` and `ShowProtectedSystemFiles` true, `count` > 0. Restore the previous values afterwards (read them first with `settings.get`).
7. Home: `search query=FolderSearch.cs path=Home` gives `Everything` (plus `WindowsSearch` if any non-NTFS drive is attached), and the path is found.
8. Suggestions-sized: `search query=e path=C:\ max=10`: `count` = 10, `backends` = `Everything`, fast.
9. Results page: `navigate path=C:\Programming\MyFiles\src`, then `search query=FolderSearch ui=true`. Expect `backends` = `Everything` and `count` ≥ 2, then `state` shows pageType SearchResults and `items` lists FolderSearch.cs.

**Not verifiable without disturbing Harald's machine:** fallback when Everything is not running (it needs the Everything tray process quit; the service alone doesn't host the IPC window), and Everything 1.5's window class. The setting-off path (check 3) exercises the same fallback code, but not the detection.

## Overseer rulings

1. **`:` queries** (non-`$`) go to Everything. Approved, with the guard that Files-generated AQS stays on Windows Search (see the audit in Design).
2. **No result cap** beyond `MaxItemCount`. Approved.
3. **Toggle on the Advanced page, default on.** Approved.

## Out of scope

- Everything 1.5 named-pipe IPC, the ETP/network server and the SDK DLL.
- Using Everything's relevance or highlighting, or showing in the UI which backend served a search.
- Omnibar path-mode search (card 49c16ee3). This card only provides the `EverythingSearch` / `FolderSearch` seam it will call.
- Starting Everything when it isn't running, or prompting to install it.
