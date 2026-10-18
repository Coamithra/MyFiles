# Live search suggestions in the address bar (card b162248f)

Branch `feat/omnibox-suggestions`, worktree `.claude/worktrees/omnibox-suggestions`, base main 590323296.

## Context

PR #14 made the Omnibar's path mode double as search: Enter on non-path text runs the rules in `NavigationToolbarViewModel.Omnibox.cs` and ends in a search of the current folder. While typing, though, path mode only knows folder path completion (`PopulateOmnibarSuggestionsForPathMode`, `NavigationToolbarViewModel.cs:1004-1116`), so non-path text like `navig` shows a single "No results" item (its `GetFolderWithPathFromPathAsync("navig")` fails). The card asks for the top Everything matches for the current folder and its subfolders as suggestions while typing such text. Everything answers scoped queries in about 20 ms (`EverythingSearch.QueryAsync`, PR #7).

How path-mode suggestions work today (all of it stays):

- `NavigationToolbar.xaml:243-291`: `OmnibarPathMode` binds `ItemsSource` to `PathModeSuggestionItems` (`ObservableCollection<OmnibarPathModeSuggestionModel>`, a `record (string Path, string DisplayName)`), `TextMemberPath="Path"`, item template = one `TextBlock` of `DisplayName`.
- `NavigationToolbar.xaml.cs:326-346` `Omnibar_TextChanged` (UserInput only) and the focus/mode handlers call `PopulateOmnibarSuggestionsForPathMode` through the dispatcher.
- Highlighting an item with the arrow keys puts its `Path` in the text box (reason `SuggestionChosen`, so no re-populate); Enter with a highlighted item, or a click, raises `QuerySubmitted` whose `Text` is that full path, and `SubmitPathModeQueryAsync` runs it through the normal rules. Enter with nothing highlighted submits the typed text (Omnibar `SelectedIndex` is -1).
- The populate method's own diffing (`:1055-1090`) avoids flicker: replace in place when no item is shared, otherwise remove/insert around shared items. An empty result shows the "No results" item (`AddNoResultsItem`, `:1103-1115`).

## Design

### Behaviour

- **When search suggestions apply** (everything else keeps today's behaviour exactly):
  - `UseEverythingForSearch` is on, and the page isn't Settings.
  - The text is not empty, not `Home`/`ReleaseNotes`/`Settings` (the existing history case), does not start with `>`, and is not only dots and slashes (`.`, `..`, `.\`).
  - `?x` suggests matches for `x` (empty `x`: no suggestions). Other text must not be path-shaped by the same test Enter uses: `NormalizePathInput` + `StorageFileExtensions.GetResolvedPath` + `IsPathShapedInput` (rooted, FTP, `shell:`). So `C:\Win`, `\\server`, `shell:Downloads` keep folder completion untouched.
  - Not a registered URL scheme prefix (`UriSchemePrefixRegex` + `IsRegisteredUrlProtocol`, both already in Omnibox.cs): `https://…` and `ms-settings:` get none, while Everything syntax such as `ext:pdf` does.
  - The query is one Everything would serve on Enter: not `$…`, no `tag:`, no `System.*:` term (the same rules as `FolderSearch.CanSearchWithEverything`), and no Everything content function (`content:`, `ansicontent:`, `utf8content:`, `utf16content:`, `utf16becontent:`, which read file contents and are too slow per keystroke).
  - The scope can be served. On a folder, it is the working directory, which must be fully qualified and not MTP, FTP, a zip or a drive that needs a find timeout (`FolderSearch.RequiresFindTimeout`: network, non-NTFS/ReFS, reparse-point root). On Home it is unscoped, matching the Enter search, which covers every local drive. On a library it is each library folder in turn, with the same checks.
- **Debounce 150 ms**, then query. Every call of the populate method cancels the previous pending query (CTS), and a result is dropped unless its token is live and `PathText` still equals the text it was made for. Focus loss cancels too.
- **Everything only.** The query never touches `FolderSearch`/Windows Search. If Everything isn't running, has no database or the IPC call fails, the helper returns null and the existing flow runs, which for non-path text shows "No results" as today.
- **What is queried** (on one `EverythingIpcClient`, on a `LongRunning` thread as `EverythingSearch.QueryAsync` does), for each scope folder:
  1. Direct children first: `parent:"<folder>" <text>`, up to 16 results. I verified with `es.exe` that the quoted form works, including the drive root `parent:"C:\"`.
  2. When the query is a plain ranking term (see Ranking), prefix matches in the subtree: `EverythingSearch.BuildQuery("startwith:\"<term>\"", folder)`, up to 48. Everything sorts every page by name, so with many matches the obvious hit can fall outside a page: 5087 matches for `local` in `C:\Users\coami` leave out `AppData\Local`, and `HEAD` in the repo misses `.git\HEAD` behind the `*Header*` files. The overseer approved this step during implementation.
  3. Then the subtree: `EverythingSearch.BuildQuery(text, folder)` (`"<folder>\" <text>`), up to 48 results.

   That is at most three round trips per scope folder per keystroke, all on the same client and thread with the client's timeouts. If the token fires between them, the query stops and nothing is applied. The unscoped Home case skips step 1. Libraries loop over their folders instead of using Everything's `<a|b>` OR syntax, which I could not verify. Results are de-duplicated by path, and `IsRoot` items are skipped.
- **Ranking:**
  - Direct children come first, then the startwith hits, then the general subtree hits.
  - Within each group: name equal to the term (with or without extension) first, then name starting with it, then the rest; subtree hits then go by depth below the scope. Ties keep Everything's name order.
  - The "term" is the trimmed query only when it has no whitespace and none of `: * ? " < > | \ /`; otherwise only depth and Everything's order count, and step 2 is skipped.
  - Capped at **8** (`MaxSearchSuggestions`), so a few path-completion slots are left unused (`MaxSuggestionsCount` is 10). The two Everything page sizes exist so that 8 survive the visibility filter.
- **Visibility** (Everything doesn't index attributes; attribute indexing is off on this machine): each candidate is stat'ed with `FolderSearch.StatPath` (FindFirstFileEx basic), which also drops items deleted since indexing.
  - The predicate is the one `FolderSearch.Everything.cs:162` uses (`ShowHiddenItems`, `ShowProtectedSystemFiles`, `ShowDotFiles`).
  - Beyond the results page, ancestors between the scope and the item must be visible too. A `.`-prefixed segment is checked by string; hidden/system ancestor folders by stat, with a per-query cache. So `.git\…` and `AppData\…` content doesn't leak in when those are hidden.
  - Candidates on a drive that needs a find timeout are skipped (cached per drive root; this only matters for unscoped Home).
  - Stat'ing stops once 8 items are kept.
- **Display:**
  - Each hit is an ordinary `OmnibarPathModeSuggestionModel(Path, DisplayName)`, so the model, the item template and the XAML don't change.
  - `DisplayName` is the path relative to the scope folder: `NavigationToolbar.xaml` for a direct child, `src\Files.App\UserControls\NavigationToolbar.xaml` deeper, the full path when unscoped (Home). This matches the existing `first\child` style of subfolder completion.
  - The list goes through the existing diff code, so it doesn't flicker. During the debounce the previous list stays up rather than flashing "No results".
- **Picking a hit:** highlighting it puts its full path in the box; Enter or a click submits that path through the unchanged rules.
  - A folder navigates in Files (and lands in path history).
  - A file opens with its default app (`HandleItemNavigationAsync` → `DangerousGetFileWithPathFromPathAsync` → `Win32Helper.InvokeWin32ComponentAsync`), exactly as typing its full path does.
  - **Proposal: open.** It is Explorer-like (Explorer's address bar and search suggestions both open the file), it matches Files' own search-mode suggestions (`NavigationHelpers.OpenPath`), and it needs no new code path.
  - The alternative, "reveal" (navigate to the parent and select the file), would need a `QuerySubmitted` branch on `args.Item` plus a select-after-navigate. Say if you prefer it.
- **No new setting.** The feature follows `UseEverythingForSearch` (turning Everything off for search turns these off too). A dedicated toggle would add a settings key, Advanced-page UI and resw strings. I'm proposing it only if you want one.

### Files

1. **New `src/Files.App/Utils/Storage/Search/EverythingSuggestions.cs`** (fork code):
   - `internal static class EverythingSuggestions` with `Task<IReadOnlyList<EverythingSuggestion>?> QueryAsync(string query, IReadOnlyList<string>? scopeFolders, int maxCount, CancellationToken token)`. It returns null when Everything is unavailable or a query fails; an empty list is a real "no matches".
   - `internal readonly record struct EverythingSuggestion(string Path, bool IsFolder, string? ScopeFolder)`.
   - It contains the two-step query, de-duplication, ranking, stat + ancestor visibility filter and drive-timeout skip.
   - `internal static bool IsSuggestableQuery(string query)`: the `$` / `tag:` / `System.*` / content-function exclusions.
   - Settings are read through `Ioc.Default.GetRequiredService<IUserSettingsService>()` before the thread starts.
   - Constants: `DirectChildrenPageSize = 16` and `SubtreePageSize = 48`.
2. **`src/Files.App/Utils/Storage/Search/FolderSearch.Everything.cs`**:
   - `StatPath(string, out WIN32_FIND_DATAW)` (`:196`) goes from `private` to `internal` for reuse.
   - The `System.*` regex (`:30-31`) and the `$`/`tag:`/regex test in `CanSearchWithEverything` (`:70-73`) move into an `internal static bool IsEverythingQuery(string query)`, which `CanSearchWithEverything` then calls. This is behaviour-preserving: `IsTagQuery(AQSQuery)` is true exactly when `Query` contains `tag:`, because a query without `:` turns into `System.FileName:…`, which never contains `tag:`. `EverythingSuggestions.IsSuggestableQuery` reuses it, so suggestions and Enter agree on what Everything serves.
3. **`src/Files.App/Utils/Storage/Search/FolderSearch.cs:653`**: `RequiresFindTimeout` goes from `private static` to `internal static` (a one-word upstream diff).
4. **`src/Files.App/ViewModels/UserControls/NavigationToolbarViewModel.Omnibox.cs`** (fork partial):
   - Constants `PathModeSearchSuggestionsDelay = 150 ms` and `MaxSearchSuggestions = 8`.
   - A field `CancellationTokenSource _pathModeSearchCts`.
   - `internal string PathModeSuggestionsSource` (`"path"` | `"everything"`, for automation).
   - `private async Task<List<OmnibarPathModeSuggestionModel>?> GetPathModeSearchSuggestionsAsync()` holds the eligibility rules above, the scope (`ContentPageContext.ShellPage`: `CurrentPageType == typeof(HomePage)` → unscoped; `App.LibraryManager.TryGetLibrary(WorkingDirectory, out var library)` → `library.Folders`; otherwise WorkingDirectory), the debounce, the query and the `DisplayName` mapping. It returns null when search suggestions don't apply or Everything is unavailable, and throws `OperationCanceledException` when stale.
   - `CancelPathModeSearchSuggestions()`.
5. **`src/Files.App/ViewModels/UserControls/NavigationToolbarViewModel.cs`** (upstream file, minimal hook):
   - In `PopulateOmnibarSuggestionsForPathMode` (`:1004`), before the `IgnoreExceptions` call, about 5 lines: `try { searchSuggestions = await GetPathModeSearchSuggestionsAsync(); } catch (OperationCanceledException) { return; }` and setting `PathModeSuggestionsSource`. The stale case returns before upstream's lambda, because that lambda would turn an exception into the "No results" flash.
   - Inside the lambda, the `if` at `:1012` becomes `if (searchSuggestions is not null) newSuggestions.AddRange(searchSuggestions); else if (…existing…)`. An empty hit list then reaches upstream's `Count is 0 → No results` path.
   - `CancelSuggestionSearch()` (`:1309`) also cancels the path-mode CTS.
   - `Dispose()` (`:1317`) cancels and disposes it.
6. **`src/Files.App/UserControls/NavigationToolbar.xaml.cs:523-526`**: on focus loss, call `ViewModel?.CancelSuggestionSearch()` regardless of mode (drop the `if` around it), so a pending query can't update the list after focus has gone.
7. **Automation: `src/Files.App/Automation/AutomationHandlers.Omnibar.cs`** gets `SuggestOmnibarAsync(JsonObject p)`; `AutomationHandlers.cs` gets the `Methods` entry after `omnibar.submit` (`:41`) and the case after `:86`.
   - Method `omnibar.suggest`, params `text` and `timeoutMs` (default 10000).
   - It sets `viewModel.PathText = text` (as typing does through the TwoWay binding) and awaits `viewModel.PopulateOmnibarSuggestionsForPathMode()`, the same method `Omnibar_TextChanged` calls, so the debounce and query run for real.
   - Returns `{ source, elapsedMs, items: [{ path, displayName }], omnibar: OmnibarState() }`, with items in UI order and built with System.Text.Json.Nodes.
8. **`.claude/CLAUDE.md`**:
   - Add a sentence to the address-bar gotcha (`:78`): non-path text suggests up to 8 Everything matches (direct children first, then the subtree), 150 ms debounce, Everything only, gated by `UseEverythingForSearch`, picking one submits its full path.
   - Add `omnibar.suggest` to the method list in "Driving the app".

No Omnibar control change, no XAML change, no Resources.resw strings, no settings. Shared resources touched: NavigationToolbarViewModel(.Omnibox), NavigationToolbar.xaml.cs (a one-line change), AutomationHandlers.cs plus .Omnibar.cs, `.claude/CLAUDE.md`, and FolderSearch(.Everything).cs.

## Constraints that shaped the design

- Path completion and the Enter rules must not change, so search suggestions only claim text that Enter would treat as non-path. The existing list and diffing code are reused, rather than a new model or template, to keep upstream diffs minimal and the no-flicker behaviour.
- Everything only, and nothing per keystroke may block: the queries run off the UI thread and the stats are cheap local FindFirstFileEx calls. Drives that need timeouts are skipped instead of waited on.
- Suggestions follow the same Everything eligibility as the results page, so what's suggested is what Enter would find.

## Verification

Build: `msbuild -restore src/Files.App/Files.App.csproj -p:Configuration=Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly`, error-free and with no new IL2xxx/IL3xxx warnings.

App (I run it myself, per the override): `dev-deploy.ps1 -Hidden`, then `ping`, then:

1. `navigate path=C:\Programming\MyFiles` then `omnibar.suggest text=navigationtoolbar`: source `everything`, at most 8 items, the `src\Files.App\…\NavigationToolbar.xaml(.cs)` / `NavigationToolbarViewModel*.cs` entries with relative display names, and nothing under `.claude\worktrees` ahead of shallower hits.
2. `omnibar.suggest text=agents`: `AGENTS.md` (a direct child) is first with display name `AGENTS.md`.
3. `omnibar.suggest 'text=?claude'`: suggestions for `claude`. `text=>Copy`: source `path`.
4. `text=C:\Prog`: source `path` (folder completion, `C:\Program Files` and so on). `text=` (empty): history. `text=Settings`: history. `text=https://example.com`: source `path`.
5. Prefix pass and ancestor filter: in `C:\Users\coami`, `omnibar.suggest text=Local` suggests `AppData\Local` only when hidden items are shown. In the repo root, `text=HEAD` suggests `.git\HEAD` only when hidden items are shown, because `.git` is a hidden folder.
6. Visibility, with `ShowDotFiles` off (`settings.get` first, restore afterwards): `omnibar.suggest text=HEAD` in `C:\Programming\MyFiles` returns no `.git\…` item. With `settings.set ShowDotFiles=true` it may. Also check `ShowHiddenItems` with a hidden folder (for example navigate to `C:\Users\coami` with `text=Local` and expect no `AppData\Local` while hidden items are off).
7. `settings.set UseEverythingForSearch=false`, then `omnibar.suggest text=agents`: source `path` with the "No results" item. Then restore the setting.
8. Home: `navigate path=Home` and `omnibar.suggest text=notepad.exe`: source `everything`, full-path display names, nothing under hidden `AppData` while hidden items are off.
9. The Enter rules are unchanged: `omnibar.submit text=agents` still lands on a search results page (`search.backends` = Everything), and `omnibar.submit text=src` still navigates to the child folder.
10. Timing: `elapsedMs` for checks 1–2 is about 150 ms plus a few tens of ms.

Not provable headless: popup rendering and flicker while typing, and keyboard selection. I will check those with `window.show` and a screenshot if feasible, and otherwise flag them for Harald.

## Out of scope

- A separate on/off setting (see above).
- Icons or a file/folder glyph in path-mode items (that would mean a template change).
- Reveal-in-folder on pick (see the proposal).
- Applying the ancestor-visibility filter to the Everything results page (`FolderSearch.Everything.cs`). I'm proposing that as a follow-up, since the results page currently lists items under hidden/dot folders.
