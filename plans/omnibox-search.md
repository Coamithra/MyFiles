# Address bar doubles as search (Chrome-style omnibox)

Card `49c16ee3` · branch `feat/omnibox-search` · worktree `.claude/worktrees/omnibox-search`

## Context

Harald wants one box: the address bar navigates when the text is a path and searches when it is not, the way Chrome's omnibox does. Upstream Files already has an Omnibar (`src/Files.App/UserControls/NavigationToolbar.xaml:230`) with three modes, each with a mode button: path (`OmnibarPathMode`), command palette (`OmnibarCommandPaletteMode`) and search (`OmnibarSearchMode`, :349). There is no separate search box; search is the third mode. Search in the app means the search-results page, fed by `FolderSearch`, which since batch 1 goes through Everything first (`src/Files.App/Utils/Storage/Search/FolderSearch.Everything.cs`). This card reuses that path (`IShellPage.SubmitSearch`) and adds no second search path.

### What path mode does today (verified in code)

Enter in path mode runs `NavigationToolbar.Omnibar_QuerySubmitted` (`NavigationToolbar.xaml.cs:186-198`), which calls `NavigationToolbarViewModel.HandleItemNavigationAsync` (`NavigationToolbarViewModel.cs:543-670`). The resolution order is:

1. Empty text, or the current folder: nothing.
2. `Home`, `ReleaseNotes` or `Settings` (or the localized names): open that page.
3. `GetResolvedPath` (expands `~`, `%vars%` and `shell:` places), then an existing folder (`DangerousGetFolderWithPathFromPathAsync` or `CheckFolderAccessWithWin32`) is navigated to, an FTP path is navigated to, and an existing file is opened.
4. Otherwise `LaunchApplicationFromPath` (:986): the first token is started as a program with the rest as arguments, through `LaunchHelper.LaunchAppAsync`. That tries `Process.Start` without the shell (PATH lookup), then `ShellExecute` (PATH, App Paths, relative to the current folder; `ErrorDialog` is false, so it shows no error UI), then a third `Process.Start`. This is why `notepad`, `cmd` and `chrome` launch.
5. Otherwise `Launcher.LaunchUriAsync(new Uri(text))`.
6. Otherwise the `InvalidItemDialog` (:654-659), and the text stays in the box.

Corrections to the card and the overseer notes:

- **`>command` does not work in path mode today.** The `>` handling cited for `CheckPathInputAsync` (:846) lives in an `[Obsolete("Superseded by Omnibar.")]` method that the Omnibar never calls. Its only caller is `BaseShellPage.NavigationToolbar_QuerySubmitted`, raised from the obsolete `PathBoxQuerySubmitted` event. `HandleItemNavigationAsync` has no `>` branch, so `>Refresh` currently ends at the invalid-item dialog. This plan adds `>` handling. Without it, `>Refresh` would be searched for.
- **Step 3 probes relative text against the process's working directory, not the current folder.** `CheckFolderAccessWithWin32("Tasks")` calls `FindFirstFileEx` on a relative path. For a packaged app that directory is normally `C:\Windows\System32`, so words such as `tasks`, `drivers`, `config`, `speech`, `setup` and `recovery` "resolve" and then navigate to the meaningless relative path `Tasks`. Once unresolved text becomes a search, this matters, so relative text skips these probes.
- **URI-shaped text launches unregistered schemes.** Everything query syntax such as `ext:pdf`, `dm:today` or `size:>1mb`, and `tag:x` (which routes to Windows Search), parses as an absolute URI with an unknown scheme. Both `ShellExecute` (step 4) and `LaunchUriAsync` (step 5) can then raise Windows' "you'll need a new app to open this link" prompt instead of searching.

## Design

### Which inputs go to search (exact rule)

After this change, path-mode Enter resolves in this order. The first match wins:

| # | Input | Result |
|---|-------|--------|
| 0 | Starts with `?` | Forced search for the rest (Chrome's escape hatch: `?notepad` searches for "notepad"). A lone `?` does nothing and keeps the text. |
| 1 | Starts with `>` | Command (new; see below). Never searched. |
| 2 | Empty, or the current folder | Nothing (unchanged). |
| 3 | `Home` / `ReleaseNotes` / `Settings` (and localized) | That page (unchanged). These three words are reserved. |
| 4 | **Path-shaped** after `GetResolvedPath`: FTP, `Path.IsPathRooted` (`C:`, `C:\…`, `\\server\…`, `\foo`), or `ShellStorageFolder.IsShellPath` (`shell:`, `::{`) | Today's chain unchanged: folder, FTP, file, app with arguments (for example `C:\tools\x.exe -v`), URI, and otherwise the **invalid-item dialog stays**. A mistyped path is better reported than searched. |
| 5 | Not path-shaped and **URI-shaped** (regex `^[A-Za-z][A-Za-z0-9+.\-]+:`, so a scheme of at least two characters; `C:` is excluded) | Launched only if the scheme has a registered handler: `Launcher.QueryUriSupportAsync(uri, LaunchQuerySupportType.Uri)` is `Available`, or `HKCR\<scheme>` has a `URL Protocol` value (covers classic Win32 protocols). `http:`, `https:`, `mailto:` and `ms-settings:` launch. **Otherwise searched**, which covers `ext:pdf`, `dm:today`, `tag:x` and the like. The app-launch step is skipped for this text, so `ShellExecute` never sees an unregistered protocol. |
| 6 | Not path-shaped, names an **existing child folder of the current folder** (`Path.GetFullPath(Path.Combine(workingDir, text))`; `workingDir` as today, so the user profile on Home) | Navigate there in Files (new). Today `ShellExecute` opens such a folder in Explorer. This also makes `..` go up. |
| 7 | Not path-shaped, and the first token **resolves to an existing file without launching anything** (resolution order below) | That file is opened or started with the remaining tokens as arguments. Examples: `notepad`, `cmd`, `notepad foo.txt`, `chrome`, any program on PATH or in App Paths, and a file name in the current folder such as `report.docx`. |
| 8 | Everything else | **Search** of the current folder and its subfolders, exactly as search mode submits it: `shellPage.SubmitSearch(text)` plus `SaveSearchQueryToList(text)`. On the Settings page it filters settings instead (`SettingsPage.ApplySearchQuery`), as search mode does there. |

So `notepad` and `cmd` keep launching. A single word that names a program on PATH also launches rather than searching, for example `code`, `git`, `python`, `help`, `tree`, `sort`, `find` or `control`; `?word` is the way to search for those. Multi-word text launches only when its first word is a program (`notepad foo` opens Notepad), and `budget report` is searched.

### Launch resolution (row 7): resolve first, launch only what resolved

Overseer requirement: deciding whether text is launchable must never raise Windows UI before the fallback to search. That rules out "Windows cannot find 'xyz'", "How do you want to open this?" and the get-an-app prompt. Fork text therefore never goes through upstream's try-to-launch chain (`LaunchApplicationFromPath`); rooted text still does, unchanged. The first token comes from `CommandLineParser.SplitArguments` with quotes trimmed, and the rest is the argument string. `Resolve(name)` runs this order, and the first match wins:

1. **Current folder:** `Path.GetFullPath(Path.Combine(workingDir, name))` is an existing file. If `name` has no extension, also try `name` plus each `PATHEXT` extension there. This only applies when `workingDir` is a real folder; on Home it is the user profile, as in today's code.
2. **PATH:** only when `name` contains no directory separator. The directories are the process `PATH` followed by the Machine and User `PATH` from the environment (so programs installed after the app started are found), de-duplicated and with variables expanded. In each directory, if `name` already has a `PATHEXT` extension, try it as-is; otherwise try `name` plus each `PATHEXT` extension in order (`PATHEXT` falls back to `.COM;.EXE;.BAT;.CMD` when unset). A plain document on PATH (`readme.txt`) does not count. This is managed code, so no new interop is needed.
3. **App Paths:** `HKCU`, then `HKLM`, `SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\<name>` (with `.exe` appended when `name` has no extension). The key's default value is trimmed of quotes and has its variables expanded, and it counts only if that file exists.
4. Otherwise it is **unresolved, so it is searched.** Nothing is launched.

Whatever resolved, executable or document, opens through `Win32Helper.InvokeWin32ComponentAsync(fullPath, shellPage, args, workingDirectory)`, the call upstream uses to open files. It ends in `LaunchHelper.LaunchAppAsync`, where a full path to a program starts with `Process.Start` without the shell, and a document goes to the shell with `ErrorDialog = false`, which .NET maps to `SEE_MASK_FLAG_NO_UI`. If a launch of something resolved still fails, the method returns `false`: the text stays in the box and no search runs. That only happens when a real file failed to open, and searching would hide that.

App-execution aliases in `%LOCALAPPDATA%\Microsoft\WindowsApps` (on the user PATH, for example `wt`) are reparse-point files that `File.Exists` reports, so they resolve through step 2.

### `>` in path mode

`>` plus text looks for a command: first an exact `CommandCodes` name (case-insensitive, `ICommandManager[string]`, as the obsolete method did: `>RefreshItems`), then a unique exact case-insensitive match on `Label` or `Description` among `IsAccessibleGlobally` commands (`>refresh` matches `RefreshItems`, whose label is "Refresh"). A match that can run is executed and focus returns to the pane. A match that can't run shows the existing `CommandNotExecutable` dialog and keeps the text. No match switches to command-palette mode with the text after `>` prefilled, through the existing `RestoreOmnibarText(viewModel, OmnibarCommandPaletteMode, …)` pending-text mechanism, so the palette lists its suggestions.

### Hide the search-mode button (with a way back)

- New setting `IAppearanceSettingsService.ShowOmnibarSearchModeButton`, default `false`, next to `ShowShelfPaneToggleButton` (`src/Files.App/Data/Contracts/IAppearanceSettingsService.cs:115-118`, `src/Files.App/Services/Settings/AppearanceSettingsService.cs:156-161`). It is a `bool`, so the serializer context needs no change.
- `NavigationToolbarViewModel.IsOmnibarSearchModeVisible => ShowOmnibarSearchModeButton || InstanceViewModel?.IsPageTypeSettings == true`. On the Settings page, search mode is the settings search with suggestions for settings cards, so it stays reachable there. It is raised from `AppearanceSettingsService_PropertyChanged` (:316) and `InstanceViewModel_PropertyChanged` (:255, next to the existing `OmnibarSearchModePlaceholder` raise).
- `NavigationToolbar.xaml:349`: on `OmnibarSearchMode`, add `Visibility="{x:Bind ViewModel.IsOmnibarSearchModeVisible, Mode=OneWay}"`.
- **Omnibar control** (`src/Files.App.Controls/Omnibar/Omnibar.cs`, upstream code, small diff). Today it assumes every mode is visible, so collapsing one would leave its separator behind and the text box's right padding a button too wide.
  - `PopulateModes` (:102-126): keep the separator inserted before each mode and sync its `Visibility` to that mode's, through `mode.RegisterPropertyChangedCallback(VisibilityProperty, …)`. The callback also recomputes the padding, and when the mode being collapsed is `CurrentSelectedMode` it selects the `IsDefault` mode.
  - `ChangeMode` (:157-166): move the padding computation into `UpdateAutoSuggestBoxPadding()`. It counts only visible modes for the index and count, takes the separator width as the largest `ActualWidth` among the separators (one may be collapsed), and caches the mode-button width that `ChangeMode` measures today (`newMode.ActualWidth`). A recompute outside `ChangeMode` then doesn't read the selected mode's star-sized width.
- Settings UI: `AppearanceViewModel.ShowOmnibarSearchModeButton` (next to `ShowShelfPaneToggleButton`, `src/Files.App/ViewModels/Settings/AppearanceViewModel.cs:~395`), and a `SettingsCard` with a `ToggleSwitch` in the existing "Address bar" expander of `src/Files.App/Views/Settings/AppearancePage.xaml:259-275`, between the shelf-pane and status-center cards.

### Ctrl+F / F3

`SearchAction.ExecuteAsync` (`src/Files.App/Actions/Global/SearchAction.cs:39-47`) calls `SwitchToSearchMode()` when `ToolbarViewModel.IsOmnibarSearchModeVisible` is true: with the setting on (upstream behaviour, the full way back) or on the Settings page. Otherwise it calls `SwitchToPathMode()`, the Ctrl+L path. That focuses the Omnibar in path mode, and `Omnibar.AutoSuggestBox_GotFocus` selects all the text, so typing replaces the path. Label, hotkeys and `IsExecutable` are unchanged.

### Path-mode placeholder

New string `OmnibarPathModeSearchTextPlaceholder` = "Enter a path, or type to search...", referenced from `NavigationToolbar.xaml:249` instead of `OmnibarPathModeTextPlaceholder`. It is a new key rather than an edit to the upstream value, so other languages fall back cleanly and upstream merges don't conflict.

### File-by-file

| File | Change |
|------|--------|
| `src/Files.App/ViewModels/UserControls/NavigationToolbarViewModel.Omnibox.cs` (new) | Fork partial: `IsOmnibarSearchModeVisible`; `IsPathShapedInput(string, bool isFtp)`; `IsUriShaped(string)` (`[GeneratedRegex]`); `TryLaunchRegisteredUriAsync(string)`; `HandleNonPathInputAsync(IShellPage, string text)` (rows 5-8); `SubmitOmniboxSearch(IShellPage, string query)` (search, or settings filter on the Settings page via `(MainWindow.Instance.Content as FrameworkElement)?.FindDescendant<SettingsPage>()` with `[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]`, the same lookup the toolbar uses at `NavigationToolbar.xaml.cs:237`); `FindPathModeCommand(string text)` (the `>` lookup). |
| `src/Files.App/ViewModels/UserControls/NavigationToolbarViewModel.cs` | One inserted block in `HandleItemNavigationAsync` right after `normalizedInput = StorageFileExtensions.GetResolvedPath(...)` (:584), before the current-path re-check: `if (!IsPathShapedInput(normalizedInput, isFtp)) return await HandleNonPathInputAsync(shellPage, PathText ?? path);`. It goes before the re-check because `ResolvePath` maps anything starting with "Home" to `Home`, so `Homework` typed on Home would otherwise do nothing. Plus the two property-changed raises above. Nothing else in the upstream chain changes. |
| `src/Files.App/UserControls/NavigationToolbar.xaml.cs` | Move the path branch of `Omnibar_QuerySubmitted` (:186-198) into `internal async Task<string> SubmitPathModeQueryAsync(string text)`. It handles `?` (calls `SubmitOmniboxSearch`) and `>` (above), then the existing `HandleItemNavigationAsync`, restore-on-reject and `FocusActivePane`. It returns `"command:<Code>"`, `"palette"`, `"rejected"` or `"handled"` for automation. The event handler calls it. |
| `src/Files.App/UserControls/NavigationToolbar.xaml` | `Visibility` binding on `OmnibarSearchMode`; new placeholder key on `OmnibarPathMode`. |
| `src/Files.App.Controls/Omnibar/Omnibar.cs` | Hidden-mode support as described. |
| `src/Files.App/Actions/Global/SearchAction.cs` | Mode choice described above. |
| `src/Files.App/Data/Contracts/IAppearanceSettingsService.cs`, `src/Files.App/Services/Settings/AppearanceSettingsService.cs` | `ShowOmnibarSearchModeButton` (default `false`). |
| `src/Files.App/ViewModels/Settings/AppearanceViewModel.cs`, `src/Files.App/Views/Settings/AppearancePage.xaml` | Toggle in the "Address bar" expander. |
| `src/Files.App/Strings/en-US/Resources.resw` | `ShowSearchButtonInAddressBar` = "Show search button in address bar", next to `ShowShelfPaneButtonInAddressBar` (:4492). `OmnibarPathModeSearchTextPlaceholder` next to `OmnibarPathModeTextPlaceholder` (:4595). |
| `src/Files.App/Automation/AutomationHandlers.Omnibar.cs` (new) + two `Methods` entries and two switch cases in `AutomationHandlers.cs` | `omnibar` and `omnibar.submit`, described below. |
| `.claude/CLAUDE.md` | Add `omnibar` / `omnibar.submit` to the "Driving the app" method list, and one Architecture-gotchas line: "path-mode Enter searches anything that isn't path-shaped, a registered URI, a child folder or a launchable program; `?` forces a search and `>` runs a command". |

### Automation methods

- `omnibar`: `{ mode: OmnibarCurrentSelectedModeName, focused: Omnibar.IsFocused, pathText, searchButtonVisible: IsOmnibarSearchModeVisible }`, read from the window's `NavigationToolbar`, found as `NavigationToolbarViewModel.AddressToolbar` finds it (`(MainWindow.Instance.Content as Frame)?.FindDescendant<NavigationToolbar>()`).
- `omnibar.submit` (`text`, `timeoutMs` default 60000, `limit` default 20): does what typing and Enter do in path mode. It sets `toolbar.ViewModel.PathText = text` (the TwoWay binding would) and awaits `toolbar.SubmitPathModeQueryAsync(text)`, bounded by `timeoutMs` because a dialog in a cloaked window never closes, which is reported as `pending: true`. It subscribes to the active pane's `ItemLoadStatusChanged` before submitting, the way the `search` method's `ui` mode does (`AutomationHandlers.cs:422-438`). If the pane ends up on the results page, it waits for `Complete` and reports `search: { query: InstanceViewModel.CurrentSearchQuery, backends: FolderSearch.LastResultsPageBackends, count, paths }`. Otherwise it waits until `IsLoadingItems` is false (polled, after 500 ms). It also reports dialogs: `dialogs.app` lists the titles of in-app `ContentDialog`s open afterwards (`VisualTreeHelper.GetOpenPopupsForXamlRoot`), and `dialogs.ownWindows` counts new visible top-level windows of this process (`Win32Helper.GetDesktopWindows()` before and after, filtered by `GetWindowThreadProcessId`), which is where in-process shell error boxes appear. Prompts from other processes, such as the get-an-app prompt or `OpenWith.exe`, are not detectable cheaply and are not reported. Returns `{ result, pending, dialogs, omnibar: <as above>, pane: PaneState(), search }`. Results are built with `System.Text.Json.Nodes`, with no reflection.

### Constraints that shaped this

- Keep `notepad` and `cmd` launching (ruling 1), which is why app launch stays ahead of search and `?` exists as the override.
- Keep the upstream diff small: the fork logic sits in a new partial file, with one inserted block in `HandleItemNavigationAsync`; the Omnibar control change is the only upstream-control edit.
- Fork principle: the search-mode button is hidden by default and comes back with a Settings toggle, which also restores upstream's Ctrl+F.
- Path-shaped text keeps the invalid-item dialog, a deliberate reading of ruling 1, since "doesn't resolve to a path" fits a mistyped path badly. Easy to flip if the overseer prefers search there too.
- Shared resources touched: `NavigationToolbar*`, `NavigationToolbarViewModel`, `IAppearanceSettingsService` and its implementation, `AppearanceViewModel`, `AppearancePage.xaml`, `Resources.resw`, `AutomationHandlers.cs` (two lines each in `Methods` and the switch), `.claude/CLAUDE.md`. Nothing touches `SidebarViewModel`, the toolbar layout flyout, `OpenTerminalAction` or startup code. The settings serializer context is unchanged.

## Verification

Build (mine): `msbuild -restore src/Files.App/Files.App.csproj -p:Configuration=Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly`, error-free, with no new IL2xxx/IL3xxx warnings in added code. Files.App.Controls builds as a dependency. The cold full-solution build of this worktree already passes on `main`.

App checks (overseer, on merged main, hidden instance). `python .claude/scripts/files_auto.py ...`:

1. `ping` until `ready`.
2. `omnibar`: `searchButtonVisible` false, `mode` `OmnibarPathMode`. Visually (after `window.show`), the Omnibar shows two mode buttons, with no trailing separator or gap.
3. `navigate path=C:\Windows`, then `omnibar.submit text=C:\Windows\System32`: `pane.path` is `C:\Windows\System32`, `pageType` is not SearchResults.
4. `navigate path=C:\Windows`, then `omnibar.submit text=tasks`: `pane.path` is `C:\Windows\Tasks` (relative child folder, opened in Files, not Explorer).
5. `omnibar.submit text=..`: `pane.path` is `C:\Windows`.
6. `omnibar.submit 'text=>refresh'`: `result` is `command:RefreshItems`. `omnibar.submit 'text=>RefreshItems'`: the same.
7. `omnibar.submit 'text=>zzz'`: `result` is `palette`, `omnibar.mode` is `OmnibarCommandPaletteMode`.
8. `omnibar.submit text=hosts` (from `C:\Windows`): `pane.pageType` is SearchResults, `search.query` is `hosts`, `search.backends` includes Everything, `search.paths` includes `C:\Windows\System32\drivers\etc\hosts`.
9. `omnibar.submit text=ext:ini` (from `C:\Windows`): SearchResults served by Everything, with no "get an app" prompt (check with `window.show` or a screenshot).
10. `omnibar.submit 'text=?notepad'`: SearchResults, `search.query` is `notepad`.
11. `navigate path=Home`, then `omnibar.submit text=drivers`: SearchResults (not a navigation to a relative `drivers` path, and no Explorer window).
11b. `navigate path=C:\Windows`, then `omnibar.submit text=zzqqxx`: SearchResults with `search.query` `zzqqxx`, `dialogs.app` empty, `dialogs.ownWindows` 0, and with `window.show` no Windows prompt on screen.
12. Optional (opens a window): `omnibar.submit text=notepad`: `result` is `handled`, the pane is unchanged, Notepad starts.
13. `omnibar.submit text=C:\NoSuchFolderXyz`: `pending` true (the invalid-item dialog, unchanged, never closes in a cloaked window). Skip it if a stuck dialog is unwanted.
14. Ctrl+F: `command code=Search`, then `omnibar`: `mode` is `OmnibarPathMode` (`focused` may be false in a cloaked window; with `window.show`, the path text is selected).
15. Way back: `settings.set 'values={"ShowOmnibarSearchModeButton": true}'`, then `omnibar` shows `searchButtonVisible` true. `command code=Search`, then `omnibar` shows `mode` `OmnibarSearchMode`. Reset with `settings.set 'values={"ShowOmnibarSearchModeButton": false}'`.
16. Settings page: `omnibar.submit text=Settings`, then `omnibar` shows `searchButtonVisible` true. `omnibar.submit text=theme`: `pane.pageType` stays Settings (the settings filter is applied).
17. Settings UI: Settings, Appearance, Address bar shows the new toggle, and flipping it shows or hides the button live.

Unverified by me (no app runs for card agents): whether `QueryUriSupportAsync` reports classic Win32 protocols (the registry check covers them either way), and the visual padding with a hidden mode (check 2).

## Out of scope / follow-ups (proposed, not filed)

- **Live search suggestions in path mode** (ruling 4). Not included: path-mode suggestions are folder completions with their own model and template (`OmnibarPathModeSuggestionModel`, `PopulateOmnibarSuggestionsForPathMode` :994) and flicker handling. Mixing in `FolderSearch` results needs a merged model, an icon template, debounce and cancellation (the search-mode version at :1185 shows the pattern), and a rule for when text counts as path-ish. On folders Everything doesn't cover, every keystroke would start a Windows Search query. Worth its own card.
- On the results page, Ctrl+F could prefill the current query (Chrome shows the query in the omnibox).
- Upstream quirk left alone: `ResolvePath` maps any text starting with `Home`, `Settings` or `ReleaseNotes` to that page name. The new block runs before the code it affects, so `Homework` is searched.
