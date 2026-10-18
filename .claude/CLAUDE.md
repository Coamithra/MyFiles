# MyFiles — Harald's personal fork of Files

This is a personal fork of [files-community/Files](https://github.com/files-community/Files), the open-source WinUI 3 file manager, turned into Harald's own Windows Explorer replacement. The upstream `CLAUDE.md` / `AGENTS.md` at the repo root hold upstream's coding conventions, build commands and review rules — follow them. This file holds only what is specific to the fork. It lives in `.claude/` so the root files stay identical to upstream and merges never conflict on them.

## Why a fork, not Explorer tweaks

Explorer can only be trimmed through registry hacks and Windhawk hooks, and it cannot gain real features (a proper bookmarks tree, Everything search, reopen-closed-tab). Files already has parity with Explorer plus a real Settings page, so every change Harald wants becomes a preference there instead of a registry edit. Guiding principle: noise is hidden by default or moved into Settings, never just deleted without a way back.

## Repo and remotes

- `origin` = `Coamithra/MyFiles` (public fork). `upstream` = `files-community/Files`, fetch-only (push URL is `no_push`).
- Default branch: `main`. Pull upstream with `git fetch upstream` + merge `upstream/main` into `main`; keep fork changes small and localized so those merges stay cheap.
- Prefer adding new files/services over editing hot upstream files; when an upstream file must change, keep the diff minimal.

## Project tracking (Trello — local backend)

The board is a **local** (file-backed) Trello board in the `trello` CLI's local store, not on trello.com.

- **Board:** `MyFiles — Explorer replacement` · id `8bd03a82`. Always pass `--backend local --board 8bd03a82`.
- **Columns:** `To Do` `1a21de83` · `Doing` `f10844b0` · `Done` `9859000d` · `Moot` `5a810eb8` (the CLI's `grab` defaults match, so no `--from`/`--to` needed). Cards that stock Files already covers go to `Moot`, not to the archive.
- The board is the backlog (Harald's wishlist: declutter-as-preferences plus new features). Check it for live status rather than trusting any summary.
- **When picking up a card, follow `~/.claude/CONTRIBUTING.md`** with these specifics:
  - Worktrees: `.claude/worktrees/<branch-name>` (ignored via `.claude/.gitignore`). No per-worktree bootstrap beyond `msbuild -restore`.
  - Verification gate: the focused `msbuild` build from the root `CLAUDE.md` succeeds with `-clp:ErrorsOnly`, and the change is checked in the running app through the automation pipe (see "Driving the app" below), headless unless the change is visual. Upstream has no agent-usable test suite.
  - PRs target `main` of `Coamithra/MyFiles` (never upstream): `gh pr create --repo Coamithra/MyFiles --base main`. Ignore the root `CLAUDE.md`'s upstream PR-title prefixes and template.
  - Agents may merge their own PRs into `main` once the verification gate passes; no review from Harald is needed first.

## Toolchain

- Visual Studio 2026 Community (18.x) at `C:\Program Files\Microsoft Visual Studio\18\Community`, with .NET desktop, WinUI application development, Windows App SDK C# templates, Windows 11 SDK 26100, MSVC x64 + ATL. VS 2022 is also installed and is too old for this repo.
- .NET 10 SDK (pinned by `global.json`). The solution contains C++ projects (Launcher, OpenDialog, SaveDialog), hence MSVC + ATL.
- Upstream's DevShell example uses the `Professional` path and instance id `1ba2cc4e`; on this machine it is `Community`, instance `36c5d38d`. `pwsh` is not installed, so use `powershell.exe` (5.1).
- `msbuild -restore` alone fails on the C++ Launcher project, which still uses `packages.config`; add `-p:RestorePackagesConfig=true`.

## Build and run

Build, deploy and launch the app from the current checkout (about 1.5 minutes for a warm full build):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\scripts\dev-deploy.ps1
```

`-NoBuild` re-deploys the last build, `-NoLaunch` skips starting it. The script builds `Files.slnx` (Debug x64 by default), copies every file in the build's `Files.App.build.appxrecipe` into `bin\...\win-x64\AppX`, installs the Windows App Runtime framework if this machine lacks the version the build needs, stops any running instance, registers the layout as the `FilesDev` package and launches `shell:AppsFolder\FilesDev_ykqwq8d6ps0ag!App`.

- Registering a loose layout needs Windows Developer Mode, which is on here; without it `Add-AppxPackage -Register` fails.
- Don't register the raw build output (`bin\...\win-x64\AppxManifest.xml`). Content assets such as `Assets\AppTiles` only exist in the layout, and without them the app crashes about 5 seconds after start with a stowed exception (`0xc000027b`, `80070003`) that never reaches `debug.log`.
- The registration points at one checkout's layout folder. Deploying from another worktree re-registers the package there and resets its LocalState (settings, `debug.log`).
- App log: `%LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\debug.log`. Native crashes only show up in the Application event log (`Application Error`, `Windows Error Reporting`).

## Driving the app

Debug builds host a named pipe, `\\.\pipe\Files.Automation.Dev`, that drives and inspects the running app without UI automation (fork code in `src/Files.App/Automation/`). Launch it headless, then call methods with the Python client:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .claude\scripts\dev-deploy.ps1 -Hidden
python .claude\scripts\files_auto.py help
python .claude\scripts\files_auto.py navigate path=C:\Windows
python .claude\scripts\files_auto.py select 'names=["notepad.exe"]'
python .claude\scripts\files_auto.py contextMenu 'names=["notepad.exe"]'
```

- `help` lists every method: state, items, navigate, select, commands/command (any app action by its `CommandCodes` name), contextMenu (Files' own and shell entries, `primary` marks the icon row), newMenu, terminal.preview (what an Open in terminal command would launch, without launching), layoutMenu (the toolbar's Layout menu entries as the flyout binds them), sidebar, bookmarks.* (the Bookmarks tree in `LocalState\settings\bookmarks.json`: list, add, addGroup, rename, move as a sidebar drop would, remove), search (which backends served it), tabs.*, history (per-pane back/forward stacks), session.endSession (dry run of the sign-out handler), session.windows (the saved window sessions and a dry run of what a cold start would restore), settings.get/set, window.show/hide and quit. For multi-step checks, `from files_auto import call` in a Python script.
- `-Hidden` launches through the `files-dev.exe` alias with `-AutomationHidden`. The window is DWM-cloaked: it renders and behaves normally but is invisible, absent from Alt+Tab and never takes focus. `window.show` uncloaks it for UI debugging.
- Call `ping` until `ready` is true before anything else; the client waits up to `--timeout` seconds for the pipe itself.
- Files restores the previous session's tabs on start, so close or ignore leftover tabs rather than assuming a single Home tab. A hidden instance restores the shared `LastSessionTabList` and never reads or writes the per-window sessions, so it can't disturb the user's saved windows; checks of window restore need a visible instance.
- The pipe is Debug-only (`#if DEBUG`) and accepts only the current user. Requests are one JSON line each (`{"id":1,"method":...,"params":{...}}`), answered with `result` or `error`.
- One instance owns the pipe: the first to start. A later Debug instance logs a warning to `debug.log` and serves nothing, so stop old instances before launching (`dev-deploy.ps1` does).
- A hidden instance never takes part in single-instance redirection, so your own launches never open tabs in the invisible window.
- `settings.set` writes through `UserSettingsService.SetValueByName`, which raises the same change notifications as the property setters. Prefer it to `ImportSettings`, which rewrites the whole file and drops session and history keys.
- `navigate` rejects file-system paths that don't exist, because Files reports them with a dialog that a hidden window never shows.
- Add a method when a card needs one: a `Methods` entry plus a case in `AutomationHandlers.InvokeAsync`. Build results with `System.Text.Json.Nodes` (no reflection).

## Architecture gotchas

- Every Files window is its own process (Ctrl+N launches `files-dev:?window=`). They all share `LocalState\settings\user_settings.json`, but each process caches the whole file and rewrites all of it on any setting write, and `LastSessionTabList` is written on every navigation. User data that several windows edit (bookmarks, for example) belongs in its own file with read-modify-write under a mutex, like `BookmarksService`, never in user settings.
- Each window saves its own session (tabs, selected tab, placement) to `LocalState\sessions\<id>.json` through `WindowSessionManager`. Closing one of several windows deletes its file; the last window keeps its file, and so does every window that ends without closing (sign-out, crash, kill). A launch while no window is open adopts the newest file and relaunches the others with `files-dev:?session=<id>`, as Chrome restores its windows.
- Search goes through Everything first (`FolderSearch.Everything.cs`, setting `UseEverythingForSearch`). `tag:` queries, a leading `$` and `System.*` property terms stay on Windows Search, and each folder falls back to Windows Search when Everything isn't running or doesn't index it. The `search` automation method reports which backend served a query.

## Out of scope

Windows resetting default apps is a Windows association issue, not a file-manager one.
