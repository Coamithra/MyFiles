# Restore every window on restart (card fee9ee4e)

Branch `feat/multi-window-session`, worktree `.claude/worktrees/multi-window-session`, based on main `e110d1769`.

## Context

Harald's goal: "when you close and open it just remembers its state, same as Chrome". Every Files window is its own process (Ctrl+N launches `files-dev:?window=`), and all processes share `LocalState\settings\user_settings.json`, which each process caches whole and rewrites whole on any setting write. So today there is one `LastSessionTabList` for all windows: the last writer wins, and a restart brings back one window.

Target semantics (Chrome):

- Closing a window while other windows are open forgets that window.
- Closing the last window remembers it; the next launch restores it (tabs, panes, per-pane back/forward history, selected tab, placement).
- When windows end without being closed (reboot, sign-out, kill, update, crash), every one of them is restored on the next launch, each with its own tabs and placement.
- A crashed window comes back as itself, even while other windows run.

Already on main (Part A, PR #6), re-verified for this plan:

- `PaneHistoryHelper.CaptureTab` makes each saved tab string carry its panes' back/forward stacks; `AppLifecycleHelper.SaveSessionTabs` (`AppLifecycleHelper.cs:415-433`) writes `LastSessionTabList` + `LastSessionSelectedTabIndex`. It is called on every tab content change (`MainPage.xaml.cs:189`), tab close (`BaseTabBar.cs:168`), window close (`App.xaml.cs:308-309`), crash (`AppLifecycleHelper.cs:583`), session end (`SessionEndHelper.cs:100-101`), settings restart (`GeneralViewModel.cs:123`) and update install (`AppUpdateStoreService.cs:150`).
- `SessionEndHelper`: one process registers with `RegisterApplicationRestart(null, …)` through the mutex `Files-{env}-RestartOwner`; hidden and parked instances never register. `OnEndSession` sets `IsSessionEnding` and saves. The relaunch after sign-in is a plain launch with no arguments.
- `MainWindow.InitializeApplicationAsync` `case "window"` (`MainWindow.xaml.cs:191-196`) opens one Home tab with `IgnoreStartupSettings`.
- `Program.Main` never redirects a protocol activation whose query contains `=` (`Program.cs:221-233`), so `files-dev:?session=<id>` always starts its own process. The static constructor's redirect (`Program.cs:51-70`) only fires while a parked (`LeaveAppRunning`) instance holds the `Files-{env}-Instance` semaphore; parking only happens when no other Files process runs (`App.xaml.cs:350-352`), so no flow in this design launches `?session=` while a parked instance exists.
- Window placement is one shared LocalSettings blob (`WindowEx.StoreWindowPlacementData` / `RestoreWindowPlacementData`, `WindowEx.cs:108-217`), restored at the first `WM_SHOWWINDOW` (`WindowEx.cs:299-313`), which happens inside `MainWindow.ActivateWithBackdropAsync` (`App.xaml.cs:168`), after DI is ready (`App.xaml.cs:150-157`) and before `InitializeApplicationAsync` (`App.xaml.cs:209`).

Constraints that shaped the design:

- Per-window state must not live in user settings (the multi-process gotcha in `.claude/CLAUDE.md`). It goes in one file per window, `LocalState\sessions\<windowId>.json`, with cross-process decisions under a named mutex and writes by atomic replace (the `BookmarksService` pattern, `BookmarksService.cs:190-296`).
- The hidden automation instance (`-AutomationHidden`) must never be part of the user's saved session and never restore or spawn visible windows. It keeps today's behaviour exactly (it reads and writes `LastSessionTabList` as before) and never touches the session folder.
- The `ContinueLastSessionOnStartUp` setting still governs whether a launch restores anything. With it off, nothing changes for the user except that crash/settings-restart relaunches target the window that crashed/restarted.
- No new settings, no new user-visible strings, no new P/Invoke, no reflection (System.Text.Json.Nodes only).
- Keep hot upstream files' diffs small; the logic lives in one new fork file.

## Design

### New file `src/Files.App/Helpers/Application/WindowSessionManager.cs` (namespace `Files.App.Helpers`, `internal static class`, ~300 lines)

State (process-wide, like `MainWindow.Instance`):

- `WindowId` (string, `Guid.ToString("N")`), `IsEnabled` (false for the hidden automation instance), `StartupKind` (for diagnostics: `disabled`, `legacy`, `fresh`, `restored`), `AdoptedPlacement` (base64 blob or null), the pending startup tabs + selected index, the sibling ids to launch, `_isForgotten`, and the last written JSON text (to skip identical rewrites).
- Folder: `Path.Combine(ApplicationData.Current.LocalFolder.Path, "sessions")`. Mutex: `Local\{PackageFamilyName}.WindowSessions` (same family-name helper shape as `BookmarksService.GetPackageFamilyName`), 2 s timeout, `AbandonedMutexException` counts as owned.

File format (one JSON object, written with `JsonObject`):

```json
{ "id": "…32 hex…", "pid": 1234, "processStart": 133…(FILETIME UTC), "savedAt": "2026-10-18T12:00:00Z",
  "tabs": ["<TabBarItemParameter string>", …], "selectedIndex": 0, "placement": "<WindowEx base64 blob>", "handoff": true }
```

`handoff` (present only when true) marks a file handed to a process that is being launched for it: a cold-start sibling claimed by the orchestrator, or this window's own file just before a crash/settings-restart relaunch. It is what lets `?session=` accept a file whose recorded pid is still live.

Written atomically: `<id>.<pid>.tmp` then `File.Move(tmp, file, overwrite: true)`. Reads skip `*.tmp`; an unreadable or invalid file is skipped (logged at debug).

Liveness: a file is live when `pid != Environment.ProcessId` and a process with that pid exists whose `StartTime.ToFileTimeUtc()` equals `processStart` (guards against pid reuse; an exception reading the process means not live).

Ids from the outside (`?session=`) are accepted only if they are exactly 32 hex characters, so a crafted URI can't reach a path outside the folder.

Public surface:

- `EnsureInitialized(object? activationData)`: idempotent; decides the window's identity and startup plan. Classification of `activationData`:
  - hidden automation instance (`AutomationServer.StartHidden`): `IsEnabled = false`, kind `disabled`; the pending startup is "legacy" (use `LastSessionTabList` as today). Nothing is read or written.
  - `IProtocolActivatedEventArgs` with query key `session` and a valid id: **explicit**. Under the mutex: read that file; adopt it (tabs, selected index, placement; re-stamp with this pid/start, clearing `handoff`; `WindowId` = that id) only if the id is exactly 32 hex characters, the file exists and parses, and it is either not live or marked `handoff`. Restores regardless of `ContinueLastSessionOnStartUp` (it is an explicit relaunch of a specific window). Any mismatch: kind `rejected`, a new id, and `MainWindow`'s `case "session"` opens a fresh Home window exactly like `?window=` (`IgnoreStartupSettings`).
  - query key `window`, `tab` or `folder` (Ctrl+N, tab dragged out, open in new window): **fresh**: new id, stamp an empty file so other processes see this window as live.
  - anything else (plain launch, Win+E, path/command line, file activation, `files-dev:`, `?cmd=`, startup task, the restart relaunch after sign-in): **cold-start candidate**. Under the mutex:
    - `ContinueLastSessionOnStartUp` off: kind `legacy`, new id, stamp. (Today's behaviour; the LastSessionTabList branches simply don't restore because the setting is off.)
    - another window is live: kind `fresh`, new id, stamp. (Only reachable with `OpenTabInExistingInstance` off, since otherwise such launches are redirected to the existing window.)
    - the sessions folder doesn't exist yet: kind `legacy` (one-time migration: restore `LastSessionTabList` as today), new id, stamp.
    - otherwise take every non-live file with at least one non-empty tab, newest `savedAt` first. None: kind `fresh`. Else adopt the newest (its id, tabs, selected index, placement; re-stamp with this pid), stamp the others with this pid/start (the claim, so a concurrent cold start sees them as live) and remember their ids as siblings to launch. Non-live files without tabs are deleted.
  - On mutex timeout: kind `fresh`, new id, only its own stamp is written (never risks double-restoring).
- `TakeStartupSession()` → `WindowStartup(List<string>? Tabs, int SelectedIndex, bool UseLastSessionTabList)`: returns the pending plan once, then resets it to fresh. `Tabs` non-null only when adopted tabs contain a non-empty entry; `UseLastSessionTabList` true for `legacy`/`disabled`. If siblings are pending, schedules their launch: await `MainPageViewModel.StartupTabsLoadedTask`, then on the UI dispatcher `Launcher.LaunchUriAsync(new Uri($"files-dev:?session={id}"))` for each, sequentially (so the first window is interactive before the others start). A child that fails to launch keeps this process's stamp, so it turns non-live when this process exits and is restored next time.
- `Save(List<string> tabs, int selectedIndex)`: called from `SaveSessionTabs`. No-op when disabled, forgotten, before `MainPageViewModel.StartupTabsLoadedTask` completes (so a restore in progress never overwrites the file with a partial tab list), or when the window is parked (`App.AppModel.IsMainWindowClosed` and no tabs: a parked window's later saves, e.g. at `WM_ENDSESSION`, must not wipe the session it parked with). Captures placement through `MainWindow.Instance.CapturePlacementData()` inside a try/catch (keeps the previous blob on failure, since the crash path may call this off the UI thread). Skips the write when nothing but `savedAt` would change. A process-local lock serializes concurrent callers.
- `OnWindowClosed()`: called from `App.Window_Closed` after the final save. No-op when disabled or `SessionEndHelper.IsSessionEnding`. Under the mutex: if any other file is live, delete this window's file and set `_isForgotten` (closing one of several windows forgets it); otherwise keep it and delete every other non-live file (the session is now exactly this window), then set the pending startup to this window's own last-saved tabs if `ContinueLastSessionOnStartUp` is on, else fresh — this is what a parked (`LeaveAppRunning`) window restores when it is resumed. On mutex timeout: keep the file.
- `Forget()`: delete this window's file (used when the crash-loop guard trips).
- `PrepareRelaunchUri()`: marks this window's file `handoff` and returns `files-dev:?session=<WindowId>` when enabled, else `files-dev:` (today's URI). The scheme is built exactly the way Ctrl+N's `?window=` launch builds it (`NavigationHelpers.LaunchNewWindowAsync`, `NavigationHelpers.cs:413-416`): a `files-dev:` literal in a `.cs` file. There is no helper or constant for the scheme in the codebase; CI's `.github/scripts/Configure-AppxManifest.ps1` rewrites the text `files-dev` in every `*.cs` file to `files-preview`/`files-stable`, so the literal is what makes it channel-correct. The literals live once, as constants at the top of the manager.
- `ListSessions()` and `DryRunColdStart()`: diagnostics for the automation method (below); the dry run computes the cold-start decision exactly as a new process would see it (no mutex, no writes).

### `src/Files.App/Data/Items/WindowEx.cs` (`:106-217`)

Split placement capture from storage so a window can persist its own blob:

- `internal string? CapturePlacementData()`: the current body of `StoreWindowPlacementData` up to the base64 string (monitor list + `WINDOWPLACEMENT`, including the fullscreen fallback to `_lastOverlappedPlacement`); returns null when `!PersistPlacement` or no overlapped placement is known.
- `StoreWindowPlacementData()` becomes: capture, then write to the shared store as now (same keys, same `WinUIEx` cleanup).
- `protected virtual string? GetPlacementToRestore()`: the current store lookup (including the old `WindowPersistance_FilesMainWindow` key).
- `RestoreWindowPlacementData()` reads its blob from `GetPlacementToRestore()`; the parsing/monitor check/apply code is unchanged.

The shared blob keeps being written on close and at `WM_ENDSESSION`, so fresh windows (Ctrl+N) still open where the last window was.

### `src/Files.App/MainWindow.xaml.cs`

- Next to `PersistPlacement` (`:31`): `protected override string? GetPlacementToRestore() => WindowSessionManager.AdoptedPlacement ?? base.GetPlacementToRestore();` so each restored window opens in its own spot.
- First line of `InitializeApplicationAsync` after the frame check (`:105-108`): `WindowSessionManager.EnsureInitialized(activatedEventArgs);` (covers the startup-task + `LeaveAppRunning` path, which first shows the window on resume; a no-op otherwise).
- In the protocol switch (`:169-200`), an explicit `case "session":` that navigates `MainPage` with a null parameter (the default case already does this; the explicit case documents that `?session=` is a full restore, not a folder). The folder lookup above the switch fails harmlessly for a GUID, as for `?window=`.

### `src/Files.App/App.xaml.cs`

- In `ActivateAsync`, inside the `if (!isStartupTask || !isLeaveAppRunning)` block before `ShowSplashScreen`/`ActivateWithBackdropAsync` (`:160-170`): `WindowSessionManager.EnsureInitialized(appActivationArguments.Data);` — identity and placement must be known before the first `WM_SHOWWINDOW`.
- In `Window_Closed`, right after the save/`CloseAllTabs` block (`:307-311`), before the `OutputPath` early returns and the parking branch: `WindowSessionManager.OnWindowClosed();`.

### `src/Files.App/ViewModels/MainPageViewModel.cs` (`:259-334`, `:350-379`)

- Parameter-null branch: call `TakeStartupSession()` first. New first branch: if it returned tabs (and `AppInstances.Count == 0`), clear `RestoreTabsOnStartup` if set (a crash/settings relaunch sets it; the explicit relaunch has now consumed it) and `RestoreSessionTabsAsync(tabs, selectedIndex)`. The existing `RestoreTabsOnStartup` and `OpenSpecificPageOnStartup` branches follow unchanged; the existing `ContinueLastSessionOnStartUp && LastSessionTabList` branch additionally requires `UseLastSessionTabList`. A fresh window therefore falls through to `AddNewTabAsync()` (Home).
- Path-parameter branch (when `!ignoreStartupSettings`): same — adopted tabs first (when `AppInstances.Count == 0`), then the existing branches, the LastSessionTabList one gated by `UseLastSessionTabList`. So Win+E or `files-dev.exe C:\x` on a cold start restores every window and opens the path as a new tab in the adopted one, as today with one window.
- `RestoreSessionTabsAsync(List<string> sessionTabs, int? selectedIndex = null)`: use the given index, falling back to `LastSessionSelectedTabIndex` as now.

### `src/Files.App/Helpers/Application/AppLifecycleHelper.cs`

- `SaveSessionTabs` (`:415-433`): build the list into a local, write the two settings as now, then `WindowSessionManager.Save(list, selectedIndex)`.
- Crash handler (`:598-617`): the relaunch uses `WindowSessionManager.PrepareRelaunchUri()` (marks the file `handoff`) instead of `files-dev:`, so the crashed window comes back as itself even when another window is running (a plain launch would be redirected into that window or start fresh). When the `LastCrashedTabList` loop guard trips, also call `WindowSessionManager.Forget()` so a crashing window isn't restored by the next cold start either. `RestoreTabsOnStartup`/`LastCrashedTabList` keep being set as today (fallback if the child fails to launch, and the loop guard input).

### `src/Files.App/ViewModels/Settings/GeneralViewModel.cs` (`:117-127`)

`DoRestartAsync` (the "restart to apply" button, e.g. after a language change) launches `WindowSessionManager.PrepareRelaunchUri()` instead of `files-dev:`, for the same reason. One-line change.

### Automation: `session.windows`

- `src/Files.App/Automation/AutomationHandlers.cs`: one `Methods` entry and one switch case (shared file; two lines).
- New `src/Files.App/Automation/AutomationHandlers.Session.cs` (partial): returns `windowId`, `pid`, `enabled`, `startup` (`kind`: disabled/legacy/fresh/rejected/restored, `adopted` id, `siblings` relaunched), `folder`, `windows` (per file, newest first: `id`, `pid`, `status` = current/claimed/live/stale, `savedAt`, `tabCount` (non-empty tabs), `selectedIndex`, `hasPlacement`, `tabs` as `{left, right}` paths decoded from the tab strings), and `coldStart` (dry run: `decision` = restore/fresh/legacy, `reason`, `adopt`, `launch`). `claimed` = marked `handoff` while its recorded process (or this one) is alive.

### Docs

`.claude/CLAUDE.md`: add `session.windows` to the method list in "Driving the app", and to "Architecture gotchas" one line: per-window sessions live in `LocalState\sessions\<id>.json`; the hidden instance never reads or writes them.

### Shared resources touched

`App.xaml.cs`, `MainWindow.xaml.cs`, `MainPageViewModel.cs`, `WindowEx.cs`, `AppLifecycleHelper.cs`, `GeneralViewModel.cs`, `AutomationHandlers.cs` (2 lines), `.claude/CLAUDE.md`. Not touched: `Program.cs`, `SessionEndHelper.cs`, `Resources.resw`, settings services/serialization context, `SidebarViewModel`, Settings pages, NavigationToolbar, OpenTerminalAction, toolbar layout flyout.

### Size

About 300 lines in the new manager, ~90 in the automation partial, ~60 changed lines across the upstream files. One reviewable PR; no staging proposed. If the overseer prefers a split: stage 1 = store, cold-start restore-all, close semantics, placement; stage 2 = crash/settings relaunch by id and the crash-guard `Forget()`.

## Behaviour changes worth knowing

- With `OpenTabInExistingInstance` off, a plain second launch while a window is open now opens a fresh Home window instead of duplicating the last-saved tab list.
- The `LastSessionTabList` fallback is used only once (first launch after this ships, while `LocalState\sessions` doesn't exist) and by the hidden automation instance.
- Closing a window by closing its last tab leaves an empty session (as upstream, which restores Home in that case).
- Closing several windows one after another (taskbar "Close all windows") keeps only the last one, as in Chrome.

## Verification

Build (mine): the focused `msbuild` of `Files.slnx` / `src/Files.App/Files.App.csproj`, Debug x64, `-clp:ErrorsOnly`, no new IL2xxx/IL3xxx warnings in the new code.

Pipe checks (overseer, on merged main). The hidden instance is deliberately excluded from sessions, so checks 1-6 use visible instances (they briefly show windows on screen). Every check is self-cleaning: it records the Files processes and session files that exist when it starts, and its cleanup closes only the processes it started and deletes only the session files it created.

Shared setup (PowerShell, from the repo root; precondition: `dev-deploy.ps1 -NoLaunch` has run, so no Files process exists; `ContinueLastSessionOnStartUp` defaults to true):

```powershell
$S = "$env:LOCALAPPDATA\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\sessions"
function Ids { if (Test-Path $S) { @((Get-ChildItem $S -Filter *.json).BaseName) } else { @() } }
function Begin { $script:basePids = @((Get-Process Files -ea 0).Id); $script:baseIds = Ids }
function Cleanup { Get-Process Files -ea 0 | ? { $_.Id -notin $basePids } | Stop-Process -Force; Start-Sleep 1; Ids | ? { $_ -notin $baseIds } | % { Remove-Item "$S\$_.json" } }
function Launch { param($uri = 'shell:AppsFolder\FilesDev_ykqwq8d6ps0ag!App') Start-Process explorer.exe $uri }
# pipe calls: python .claude\scripts\files_auto.py <method> ...; after each Launch, call ping until ready is true
```

1. Store (Begin; Launch; ping). `session.windows` → `enabled: true`, `startup.kind` is `legacy` (first launch: no `sessions` folder yet) or `fresh`; `windows` has one entry with `status: current` and `pid` = this pid. Cleanup.
2. Saving (Begin; Launch; ping). `navigate path=C:\Windows`, `tabs.open path=C:\Users`, then `session.windows` → the current entry has `tabCount: 2`, tabs `C:\Windows` and `C:\Users`, `hasPlacement: true`; `coldStart.decision: fresh` with reason `window <id> is live`. Cleanup.
3. A new window is live (Begin; Launch; ping). `command code=NewWindow`, wait 5 s → `session.windows` has two entries: `current`, and the new window with `status: live`, its own pid and `tabCount: 1`. Cleanup.
4. A reboot restores every window (Begin; Launch; ping; `navigate path=C:\Windows`; `command code=NewWindow`; wait 5 s; note both ids from `session.windows`). Simulate the reboot by stopping just those two processes (`Get-Process Files | ? { $_.Id -notin $basePids } | Stop-Process -Force`): no close handler runs, so both files stay (`Ids` still lists them). Launch; ping; wait 5 s. Expect two Files processes started by the check. `session.windows` from the pipe window (normally the orchestrator, whose pipe starts before it relaunches the other): `startup.kind: restored`, `startup.adopted` = the newer of the two ids, `startup.siblings` = the other id; both entries `current`/`live` with the new pids; the `C:\Windows` window still has that path. (If the relaunched sibling won the pipe instead, it reports `startup.kind: restored` with `adopted` = its own id and the orchestrator as `live`.) `history` on the pipe window shows its restored back stack. Cleanup.
5. Close semantics (Begin; Launch; ping; `command code=NewWindow`; wait 5 s; `$other` = the new window's pid from `session.windows`). `(Get-Process -Id $other).CloseMainWindow()` (a real WM_CLOSE), wait 3 s → `session.windows` lists only the current window (the other window was forgotten; its file is gone). `quit`, wait 3 s → `Ids` still contains the pipe window's id (the last window keeps its session; with `LeaveAppRunning` on the process parks, and Cleanup stops it). Launch; ping → `startup.kind: restored`, `adopted` = that id, `siblings` empty. Cleanup.
6. `?session=` validation (Begin; Launch; ping; `$id` = the current id). `Launch 'files-dev:?session=0123'` and `Launch "files-dev:?session=$id"` (the second names a live window that was not handed over); wait 5 s → two new windows, each with one Home tab: `session.windows` from the pipe window lists them as `live` with `tabCount: 1` and ids other than `$id`, and the pipe window's own entry is unchanged (`current`, same pid). Cleanup.
7. Hidden instance (Begin; `dev-deploy.ps1 -NoBuild -Hidden`; ping). `session.windows` → `enabled: false`, `startup.kind: disabled`, no entry has `status: current`; `navigate path=C:\Windows`, then `Ids` equals `$baseIds`. Cleanup (stops the hidden instance).

Only Harald can check by hand:

- Open three windows in different spots (one maximized, one on another monitor if available), each with several tabs and some back history; restart Windows; sign in → all three come back, each in its own spot with its tabs and back/forward history.
- Close two of three windows, then the third; reopen → only the third comes back, where it was.
- `LeaveAppRunning` on: close the last window (it parks), reopen from the taskbar → it comes back with its tabs.
- A crash relaunch can't be triggered on demand; covered by review only.

## Out of scope

- Tray "Quit"/"Restart" act on one process only (as today): Quit on a visible window forgets it like a normal close; tray Restart (`AppInstance.Restart("")`) is a plain launch, so it restores the window only when no other window is open. A Chrome-style "exit all windows" command would be a separate card.
- A startup task (`Run at Windows startup` without `LeaveAppRunning`) together with the restart-after-sign-in registration can start two launches at sign-in; the second now opens a fresh Home window instead of a duplicate session. Pre-existing double launch, not addressed.
- The hidden instance crashing still relaunches a visible plain `files-dev:` as today.
- Stale `*.tmp` files from a process killed mid-write are not cleaned up (they are ignored when reading).
- `Program.cs` static-constructor redirect: no exemption for `?session=` is added, because no flow launches it while a parked instance holds the semaphore.
