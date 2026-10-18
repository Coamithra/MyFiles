# Restore session on restart (Chrome-style) — card 16aea75f

Branch `feat/session-restore`, worktree `.claude/worktrees/session-restore`. Base: `main` @ 732df4fce. Cold build of the untouched branch is green.

## Context

Harald wants closing and reopening MyFiles to bring back exactly where he left off, like Chrome: every window, every tab with its folder and back/forward history, split panes, window size and position, the active tab, also after a crash or a reboot.

What stock Files already does (verified in code, `ContinueLastSessionOnStartUp`, default on):

- Tabs, split panes and pane arrangement: `AppLifecycleHelper.SaveSessionTabs` (`src/Files.App/Helpers/Application/AppLifecycleHelper.cs:414`) serializes every `TabBarItem.NavigationParameter` (`TabBarItemParameter.Serialize`, `src/Files.App/Data/Parameters/TabBarItemParameter.cs:20`) into the user setting `LastSessionTabList`. It is called after every navigation of the selected tab (`MainPage.TabItemContent_ContentChanged`, `src/Files.App/Views/MainPage.xaml.cs:189`), on tab close (`BaseTabBar.CloseTab`, `src/Files.App/UserControls/TabBar/BaseTabBar.cs:165`), on window close (`App.Window_Closed`, `src/Files.App/App.xaml.cs:309`) and from the crash handler.
- Restore: `MainPageViewModel.OnNavigatedToAsync` → `RestoreSessionTabsAsync` (`src/Files.App/ViewModels/MainPageViewModel.cs:290-295`, `:350`), with the active tab from `LastSessionSelectedTabIndex`.
- Window size/position: `WindowEx.StoreWindowPlacementData` / `RestoreWindowPlacementData` (`src/Files.App/Data/Items/WindowEx.cs:108`, `:156`), one shared blob in LocalSettings, written only in `WindowEx_Closed`.
- Crash: `HandleAppUnhandledException` (`AppLifecycleHelper.cs:590-617`) sets `RestoreTabsOnStartup`, guards a restart loop with `LastCrashedTabList`, and relaunches `files-dev:`.

The three gaps and what I found about each:

1. **Back/forward history is lost.** A pane's history lives only in its `Frame` (`BaseShellPage.ItemDisplay.BackStack` / `.ForwardStack`, exposed as `IShellPage.BackwardStack` / `ForwardStack`, `src/Files.App/Views/Shells/BaseShellPage.cs:71-73`) as `PageStackEntry(pageType, NavigationArguments)`. `PaneNavigationArguments` (`src/Files.App/Data/EventArguments/PaneNavigationArguments.cs`) holds only the two pane paths, select-item hints and the arrangement. The Ctrl+Shift+T stack (`BaseTabBar.RecentlyClosedTabs`, a `Stack<TabBarItemParameter[]>`) stores the same path-only parameter, so a reopened tab also starts with no history.
2. **Multiple windows.** Every Files window is its **own process** (`MainWindow.Instance` and `MainPageViewModel.AppInstances` are process-wide statics; Ctrl+N runs `NavigationHelpers.LaunchNewWindowAsync` → `files-dev:?window=`, which `Program.Main` does not redirect because the query has an `=`). All processes share one settings file through `CachingJsonSettingsDatabase`, which caches the whole file per process and rewrites the whole file on every `SetValue` (`src/Files.App/Utils/Serialization/Implementation/CachingJsonSettingsDatabase.cs:39-52`). So per-window state can never live in user settings: any write from window B overwrites window A's copy wholesale. Side finding: because `?window=` falls through to `rootFrame.Navigate(typeof(MainPage), null)` (`src/Files.App/MainWindow.xaml.cs:190-192`), a Ctrl+N window today runs the startup branch and **re-opens the last saved session's tabs** instead of a fresh window.
3. **No relaunch after reboot/sign-in.** `RegisterApplicationRestart` is in `src/Files.App.CsWin32/NativeMethods.txt:82` and only called by the sideload updater (`src/Files.App/Services/App/AppUpdateSideloadService.cs:208`). `WM_QUERYENDSESSION` / `WM_ENDSESSION` are generated (the `WM_*` wildcard) but nothing handles them. `MainWindow.OnWindowMessageReceived` (`src/Files.App/MainWindow.xaml.cs:457`) is the existing wndproc hook.

## Size verdict and split proposal

Gaps 1 and 3 are small and self-contained. Gap 2 is **large**: it needs a new per-window persistence store outside user settings, a startup orchestrator that spawns one process per saved window, per-window placement (otherwise all restored windows stack on one spot), new close semantics (closing one of several windows forgets it, like Chrome), a change to what Ctrl+N opens, and careful handling of LeaveAppRunning parking, the crash relaunch, single-instance redirection and the hidden automation instance. It touches six hot upstream files (`App.xaml.cs`, `MainWindow.xaml.cs`, `MainPageViewModel.cs`, `WindowEx.cs`, `AppLifecycleHelper.cs`, `Program.cs` possibly) plus ~250 lines of new code, and most of its behaviour cannot be proven headless.

**Proposal:** this branch ships gaps 1 and 3 (Part A below). Gap 2 becomes a follow-up card with the design in Part B, which I've worked out far enough to implement next. Part A is built so Part B slots on top without rework: the session strings Part B stores per window are the same history-carrying strings Part A produces, and Part A's restart election already relaunches exactly one process, which is what Part B's orchestrator expects.

If the overseer would rather have all three on this branch, Part B can follow as a second commit set after Part A's review.

## Part A — design (this branch)

### A1. Persist and restore each pane's back/forward history

New types, in a new fork file `src/Files.App/Data/Parameters/PaneHistory.cs`:

```csharp
internal sealed class PaneHistory
{
	public List<string>? Back { get; set; }     // Frame.BackStack order: oldest first, last = where Back goes
	public List<string>? Forward { get; set; }  // Frame.ForwardStack order, unchanged
}
```

Entries are the `NavigationArguments.NavPathParam` of each `PageStackEntry`: a folder path, `Home`, `Settings`, `ReleaseNotes`, or a `tag:` query. Search-result entries (`IsSearchResultPage` and not a `tag:` path) are skipped, because their path is the search root, not the results; restoring them as a plain folder would be wrong. Stack order is preserved exactly as the Frame holds it, so restore is an order-agnostic round-trip.

Cap: **20 back + 20 forward per pane**, keeping the entries nearest the current page (`Back.TakeLast(20)`, `Forward.TakeLast(20)` since the nearest forward entry is last). `LastSessionTabList` is rewritten into the settings JSON on every navigation, so the cap bounds that write: ~40 paths × ~60 bytes ≈ 2.4 KB per pane, ~50 KB for 10 dual-pane tabs. Chrome-sized history (50) would more than double it for little gain.

Changes:

- `src/Files.App/Data/EventArguments/PaneNavigationArguments.cs` (upstream, +2 properties): `public PaneHistory? LeftPaneHistory { get; set; }` and `RightPaneHistory`. **Left out of `==`/`GetHashCode` on purpose**: history is not part of which folders a tab shows, and the equality (used for tab-info updates) must not change. Serialization: `AppJsonSerializerContext` already has `[JsonSerializable(typeof(PaneNavigationArguments))]`; the source generator emits metadata for the nested `PaneHistory`/`List<string>` automatically, so no new context entry and no reflection. Old saved strings lack the properties and deserialize to `null`; System.Text.Json ignores unknown properties by default, so an older build also reads new strings. No settings migration.
- `src/Files.App/Data/Parameters/NavigationParams.cs` (upstream, +1 property): `public PaneHistory? History { get; set; }`, the channel from `ShellPanesPage` to each pane.
- `src/Files.App/Views/ShellPanesPage.xaml.cs` `OnNavigatedTo` (`:643-667`): pass `History = paneArgs.LeftPaneHistory` / `RightPaneHistory` into the two `NavigationParams` it builds. Nothing else in that file changes: its `TabBarItemParameter` stays history-free, because history is captured live at save time (below), not kept current on every navigation.
- `src/Files.App/Views/Shells/ModernShellPage.xaml.cs`:
  - `NavParams` setter (`:25-34`) stores `value?.History` in a new `PendingHistory` field (`internal PaneHistory? PendingHistory { get; private set; }`).
  - `ItemDisplayFrame_Navigated` (`:158`): if `PendingHistory` is set, call `PaneHistoryHelper.Apply(ItemDisplayFrame, PendingHistory, this, InstanceViewModel.FolderSettings)`, clear it, then set `ToolbarViewModel.CanGoBack/CanGoForward` from the frame (the toolbar normally learns these on `ItemLoadStatus.InProgress`, which never comes for Home). This sits before the existing `_navigationInteractionTracker.CanNavigate*` lines so the overscroll tracker sees it too. One-shot: later `NavParams` changes (Open in other pane etc.) carry no history and so leave the stack alone.
- New fork file `src/Files.App/Helpers/Navigation/PaneHistoryHelper.cs`, static:
  - `const int MaxDepth = 20;`
  - `PaneHistory? Capture(ModernShellPage pane)`: returns `pane.PendingHistory` while it is still pending (a restored tab that has never been shown has panes whose frames have not navigated yet: their live stacks are empty and the history must survive another save), else builds from `pane.BackwardStack` / `ForwardStack`, capped; `null` when both are empty.
  - `TabBarItemParameter CaptureTab(TabBarItem tab)`: when `tab.NavigationParameter.NavigationParameter is PaneNavigationArguments args` and `tab.TabItemContent is ShellPanesPage panes`, returns a new `TabBarItemParameter` whose args are a field-by-field copy of `args` with `LeftPaneHistory = Capture(pane 0)` and `RightPaneHistory = Capture(pane 1)` (only when `RightPaneNavPathParam` is set). Otherwise returns `tab.NavigationParameter` unchanged (it may already carry history from a restore).
  - `void Apply(Frame frame, PaneHistory history, IShellPage owner, LayoutPreferencesManager folderSettings)`: builds `PageStackEntry`s with `NavigationArguments { NavPathParam = path, AssociatedTabInstance = owner }` and the page type chosen exactly as `ModernShellPage.OnNavigationParamsChanged` (`:114-145`) does: `Home` → `HomePage`, `ReleaseNotes` → `ReleaseNotesPage`, `Settings` → `SettingsPage`, `tag:` → layout type plus `IsSearchResultPage`/`SearchPathParam = "Home"`/`SearchQuery`, otherwise `folderSettings.GetLayoutType(path)` (the same call `BaseShellPage.ResetNavigationStackLayoutMode` uses). Back entries are inserted before anything already on `BackStack` (normally empty after a first navigation), forward entries replace `ForwardStack`. Paths are not checked for existence: probing up to 80 paths per tab at startup could hang on offline network shares, and a vanished folder in history behaves exactly as it does today when a folder is deleted after you leave it.
  - `PaneHistory Snapshot(ModernShellPage pane)`: uncapped live view (or pending) for the automation method.
- `src/Files.App/Helpers/Application/AppLifecycleHelper.cs` `SaveSessionTabs` (`:418-429`): serialize `PaneHistoryHelper.CaptureTab(tab)` instead of `tab.NavigationParameter`. One-line change.
- `src/Files.App/UserControls/TabBar/BaseTabBar.cs` `CloseTab` (`:150-171`): capture `var closedTab = PaneHistoryHelper.CaptureTab(tabItem);` **before** `Items.Remove`/`Unload` (unload disposes the frames), and push that instead of `tabItem.NavigationParameter`. `ReopenClosedTabAsync` already passes `item.NavigationParameter` (now history-carrying `PaneNavigationArguments`) to `AddNewTabByParamAsync`, so reopened tabs get their history with no further change. All close paths (close tab, close others/left/right/all, move to new window) go through `CloseTab`.
- No change needed in `RestoreSessionTabsAsync`: it hands each deserialized parameter to `AddNewTabByParamAsync`, and the history flows `TabBarItem` → `ShellPanesPage.OnNavigatedTo` → `NavigationParams.History` → pane. The "continue last session off" path (`MainPageViewModel.cs:264-271`) that pushes the last session onto the reopen stack also carries history for free.

Not changed: `MultitaskingTabsHelpers.MoveTabToNewWindow` keeps sending the path-only parameter, because it goes through a `files-dev:?tab=` URI and up to 80 escaped paths would blow past URI length limits. A tab dragged to a new window starts with fresh history, as today.

### A2. Relaunch after reboot or sign-in (restart manager)

New fork file `src/Files.App/Helpers/Application/SessionEndHelper.cs`, static, called from `MainWindow.OnWindowMessageReceived` (`src/Files.App/MainWindow.xaml.cs:457`, +4 lines; returns `false` so the default window procedure still answers):

- `WM_QUERYENDSESSION` → `OnQueryEndSession()`: if this window is shown (`!App.AppModel.IsMainWindowClosed`, so a LeaveAppRunning instance parked in the background does not pop up after sign-in) and not the hidden automation instance (`!AutomationServer.StartHidden`), take a process-lifetime named mutex `Files-{AppEnvironment}-RestartOwner` (`new Mutex(true, name, out createdNew)`, kept in a static field). Only the creator calls `PInvoke.RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG)`. Why the election: every Files window is its own process, and Windows relaunches every registered process. Two plain relaunches would both restore the same `LastSessionTabList` (duplicate window). With one registrant, exactly one process comes back and runs the normal startup restore. Registering at `WM_QUERYENDSESSION` is the documented last opportunity and avoids holding a registration in every process all day. `RESTART_NO_CRASH | RESTART_NO_HANG` keeps Windows Error Reporting from relaunching on a crash, which the app's own crash relaunch already does (`AppLifecycleHelper.cs:610-615`); without these flags a crash would bring up two windows. `null` command line = a plain launch, so whatever the startup setting says applies (with `ContinueLastSessionOnStartUp` on, the full session).
- `WM_ENDSESSION` with `wParam != 0` → `OnEndSession()`: set `public static bool IsSessionEnding` (Part B needs it), and call `AppLifecycleHelper.SaveSessionTabs()` under the same condition `Window_Closed` uses (`ContinueLastSessionOnStartUp || RestoreTabsOnStartup`). The process is killed after this message without `Window.Closed`, so this is the last chance; today's navigation-time saves already cover tabs, this makes sure the last state and history are written.
- `src/Files.App/Data/Items/WindowEx.cs` `NewWindowProc` (`:297-338`, upstream, +5 lines): add `case 0x0016 /*WM_ENDSESSION*/ when param2 != 0:` → `StoreWindowPlacementData()` in the same try/catch shape as `WindowEx_Closed`. Today placement is written only on close, so after a reboot the window comes back at the position of the last clean close.
- Records `SessionEndHelper.RestartRegistration` (`"none"`, `"registered"`, `"notOwner"`, `"skippedHidden"`, or the failing HRESULT) for the automation check.
- CsWin32: `RegisterApplicationRestart` and `REGISTER_APPLICATION_RESTART_FLAGS` are already generated; `WM_QUERYENDSESSION`/`WM_ENDSESSION` come from `WM_*`. **No `NativeMethods.txt` change, no `DllImport`.**

No new setting: whether apps come back after sign-in is the Windows setting "Automatically save my restartable apps and restart them when I sign back in" (Settings > Accounts > Sign-in options), and what the relaunch opens is Files' existing startup setting.

### A3. Automation (`src/Files.App/Automation/AutomationHandlers.cs`, shared resource)

- `history` — "Back/forward stacks of every pane. Params: index (tab; all when omitted)." Returns `[{ index, panes: [{ path, pending, back: [...], forward: [...] }] }]` via `PaneHistoryHelper.Snapshot` (uncapped live stacks; `pending: true` when the history is still waiting for the pane's first navigation, i.e. a restored tab not shown yet). Built with `System.Text.Json.Nodes`, `(JsonNode)` casts.
- `session.endSession` — "Run the sign-out handlers as if Windows sent WM_QUERYENDSESSION and WM_ENDSESSION(FALSE). Returns the restart registration." Calls `SessionEndHelper.OnQueryEndSession(force: true)` (the `force` flag bypasses only the hidden-instance skip, so the overseer can test headless) and reports `RestartRegistration`. It deliberately does not run the session-ending save (`WM_ENDSESSION(TRUE)`), which would leave `IsSessionEnding` set.

### A4. Ctrl+N opens a fresh window (overseer extension)

`src/Files.App/MainWindow.xaml.cs` `InitializeApplicationAsync` protocol switch: new `case "window":` navigates `MainPage` with `MainPageNavigationArguments { Parameter = "Home", IgnoreStartupSettings = true }`, the same shape as the existing `folder` case. The new window gets one Home tab and skips the startup restore branch. It does not clear or consume `LastSessionTabList`, but as with every window its own later saves overwrite that shared key (last writer wins until Part B / card fee9ee4e).

## Part B — design for gap 2 (follow-up card unless the overseer extends scope)

- **Store:** one JSON file per window in `ApplicationData.Current.LocalFolder\Sessions\<windowId>.json`: `{ tabs: [Part A strings], selectedIndex, placement (WindowEx base64 blob), pid, processStart, savedAt }`, written atomically (temp + `File.Move` overwrite) by `SaveSessionTabs` next to the existing `LastSessionTabList` write (kept as the fallback and for the crash loop guard). New fork file `Helpers/Application/WindowSessionStore.cs`, `System.Text.Json.Nodes` only. Each process has a `WindowId` (new GUID, or adopted). A window is *live* when a process with its `pid` and `processStart` exists and is not the current process.
- **Startup** (`MainPageViewModel` restore branches): launched with `files-dev:?session=<id>` (new `case "session"` in `MainWindow.InitializeApplicationAsync`, also parsed early in `App.OnLaunched` so placement is known before the window shows) → restore that file. Otherwise, if no other window is live, this is a cold start: take every non-live, non-empty session, newest first; adopt the newest, claim the rest by stamping own pid, and `Launcher.LaunchUriAsync("files-dev:?session=<id>")` each (children re-stamp). No session files → today's `LastSessionTabList` path (migration). If another window is live (Ctrl+N, tab drag) → a fresh Home window, which fixes the duplicate-session Ctrl+N noted above.
- **Close:** `Window_Closed` deletes its own file when another window is live and `!SessionEndHelper.IsSessionEnding` (closing one of several windows forgets it, as Chrome does); the last window keeps its file. Reboot, crash and kill keep every file.
- **Placement:** `WindowEx` gains a `protected virtual string? GetPlacementToRestore()` (default: the shared LocalSettings blob) that `MainWindow` overrides with the adopted session's blob, so restored windows open in their own spots.
- **Crash relaunch:** `files-dev:?session=<own id>` instead of `files-dev:`, so the crashed window comes back as itself even when another window is running and receives the redirect.
- **Hidden automation instance:** adopts the newest session but never spawns siblings or deletes files. Verification: an automation `session.windows` method listing files (id, live, tab paths) plus a visible relaunch.

## Constraints that shaped the design

- Saved-string compatibility both ways (old ↔ new builds), so no migration and no new settings key or serialization-context type for Part A.
- Nothing per-window may go through user settings (whole-file last-writer-wins across processes).
- Exactly one process may be relaunched by Windows, and WER must not add a second restart on top of the app's own crash relaunch.
- The write on every navigation must stay small, hence the 20/20 cap.
- AOT: source-generated JSON and `System.Text.Json.Nodes` only. No reflection, no new P/Invoke declarations.
- Upstream diff kept small: two properties, one property, two short method edits, a 4-line hook, a 5-line wndproc case. Logic lives in three new fork files.
- Shared resources touched: `AppLifecycleHelper.SaveSessionTabs` (one line), `BaseTabBar.CloseTab`, `AutomationHandlers.cs` (two methods). Not touched: `MainPageViewModel`, `UserSettingsJsonSerializationContext`, `Resources.resw`, Settings pages.

## Verification

Build (mine): `msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly` green, no new IL2xxx/IL3xxx warnings in the new code.

Pipe checks (overseer, on merged main, after `dev-deploy.ps1 -Hidden` and `ping` ready; leftover tabs from earlier sessions may exist, so close extras or use the reported indexes):

1. History saved and restored exactly:
   ```
   python .claude/scripts/files_auto.py command code=CloseOtherTabsSelected   # or tabs.close until one tab is left
   python .claude/scripts/files_auto.py navigate path=C:\Windows
   python .claude/scripts/files_auto.py navigate path=C:\Windows\System32
   python .claude/scripts/files_auto.py navigate path=C:\Windows\System32\drivers
   python .claude/scripts/files_auto.py navigate path=C:\Users
   python .claude/scripts/files_auto.py command code=NavigateBack      # now at ...\drivers, forward = [C:\Users]
   python .claude/scripts/files_auto.py history                        # record back/forward for the pane
   python .claude/scripts/files_auto.py settings.get 'keys=["LastSessionTabList"]'   # string contains LeftPaneHistory with the same lists
   python .claude/scripts/files_auto.py quit
   (relaunch: dev-deploy.ps1 -NoBuild -Hidden, ping until ready)
   python .claude/scripts/files_auto.py history                        # identical back/forward lists for that tab
   python .claude/scripts/files_auto.py state                          # pane.canGoBack = true, canGoForward = true
   python .claude/scripts/files_auto.py command code=NavigateForward
   python .claude/scripts/files_auto.py state                          # pane.path = C:\Users
   python .claude/scripts/files_auto.py command code=NavigateBack
   python .claude/scripts/files_auto.py command code=NavigateBack
   python .claude/scripts/files_auto.py state                          # pane.path = C:\Windows\System32
   ```
2. Restored but never-shown tabs keep their history across a second restart: open two tabs with history, select tab 0, `quit`, relaunch, `history` shows tab 1 with `pending: true` and its lists; `quit` again without selecting tab 1, relaunch, `history` still shows the same lists for tab 1.
3. Cap: navigate through 25 distinct folders (e.g. subfolders of `C:\Windows`), `history` shows 25 live back entries, `settings.get keys=["LastSessionTabList"]` holds only the nearest 20.
4. Reopen closed tab keeps history: in a tab with history, `tabs.close index=<i>`, `command code=ReopenClosedTab`, `history` for the new tab shows the same lists (`pending` until it is shown; after `tabs.select` they are live and `state` reports `canGoBack`).
5. Split pane: `command code=SplitPaneVertically`, navigate in each pane (the active pane is the new one; `command code=FocusOtherPane` switches), `quit`, relaunch, `history` shows two panes with their own lists.
6. Restart registration: `session.endSession` returns `registered` for the first call. On real hardware (Harald, not headless): enable the Windows sign-in setting above, keep a visible MyFiles window open more than 60 s, sign out and back in (or reboot). MyFiles relaunches once with its tabs and history. Also check that a deliberate crash brings up one window, not two.

7. Ctrl+N fresh window: with 2+ tabs open in the hidden instance, `command code=NewWindow` starts a second, visible process (the pipe stays with the first one). That window must show one Home tab. Headless proof: it saves its tabs on its first navigation, so after a few seconds `%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\settings\user_settings.json` has `LastSessionTabList` with exactly one entry whose `LeftPaneNavPathParam` is `Home`. Before the fix it held a copy of the saved session. Read the file directly: the hidden instance's `settings.get` serves its own in-memory cache. The hidden instance's `state` still lists its own tabs. Close the new window afterwards.

Not verifiable headless: the actual Windows relaunch after sign-in (needs a real sign-out; packaged-app restart behaviour is documented but unproven on this machine), and the WM_ENDSESSION placement save.

## Out of scope

- Gap 2 (multiple windows), unless the overseer extends scope: Part B above.
- History for a tab moved to a new window (URI length).
- Per-pane scroll position and selection, column-layout inner column history, search-result pages in history.
- A Files setting to turn the sign-in relaunch off (the Windows setting covers it).
