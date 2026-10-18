# Always-visible "Open cmd here" — configurable terminal (card 5ac9913e)

Branch `feat/terminal-setting`, worktree `.claude/worktrees/terminal-setting`. Baseline full build of `main` (e110d1769) in this worktree: green.

## Context

Explorer hides "Open command window here" behind Shift. Files already has a top-level "Open in Windows Terminal" entry in the folder and background menus (`OpenTerminal`, Ctrl+'; `OpenTerminalAsAdmin`, Ctrl+Shift+'; plus `OpenTerminalFromHome` / `OpenTerminalFromSidebar` subclasses for the Home widgets and sidebar; visibility toggle `ShowOpenTerminal`, default on). `OpenTerminalAction.GetProcessStartInfo` (src/Files.App/Actions/Open/OpenTerminalAction.cs:77-109) launches `wt.exe -d <p1> ; nt -d <p2>...` when Windows Terminal is the default console host (`IsWindowsTerminalDefault`, :111-142, registry + COM probe), else `cmd.exe` with `WorkingDirectory` = first folder only. The label is always "Open in Windows Terminal" (Resources.resw:2538-2543), even when cmd opens.

Harald's ruling: the terminal is a Settings choice, default **cmd.exe**, regardless of the default-console-host detection. Choices: Command Prompt (default), PowerShell (pwsh if installed, else Windows PowerShell), Windows Terminal, Custom (executable + arguments with a `{path}` placeholder), plus Windows default terminal (upstream's detection: Windows Terminal when it is the default console host, else cmd; overseer ruling). Used by all four OpenTerminal* actions, for one or several selected folders and the background menu. The menu label follows the choice.

Constraints that shaped the design:
- Keep upstream diffs small: the launch logic moves into one new fork file; the upstream action files only change where they must.
- Launch and automation preview share one code path (`OpenTerminalAction.GetStartInfos()`), so the preview cannot drift from what runs.
- Elevation: Windows forces the current directory of an elevated System32 binary (cmd.exe, powershell.exe) to System32, so `WorkingDirectory` alone is not enough for the admin variant. Every built-in choice therefore passes the folder in its arguments as well (cmd `pushd`, PowerShell `Set-Location`, wt `-d`); `WorkingDirectory` is set too. I can't test UAC launches here (no app runs for card agents), so the overseer's check is the preview; a real elevated launch is a manual check for Harald.
- Settings are per-process cached (Architecture gotchas): a change made in one window reaches other already-open windows only after restart, like every other user setting. No new shared-file mechanism.

## Design, file by file

### 1. New `src/Files.App/Data/Enums/TerminalType.cs`
`public enum TerminalType { CommandPrompt, PowerShell, WindowsTerminal, Custom, WindowsDefault }` (order = stored numeric value = ComboBox index; `WindowsDefault` last so the default `CommandPrompt = 0` and the stored numbers never shift). Doc comments per member. `WindowsDefault` is upstream's behaviour: Windows Terminal when it is the default console host, else Command Prompt.

### 2. `src/Files.App/Data/Contracts/IGeneralSettingsService.cs` (next to `ShowOpenTerminal`, :271)
- `TerminalType TerminalType { get; set; }` — which terminal Open in terminal launches.
- `string CustomTerminalPath { get; set; }` — executable path or alias for `TerminalType.Custom`.
- `string CustomTerminalArguments { get; set; }` — arguments for Custom; `{path}` is replaced with the folder.

### 3. `src/Files.App/Services/Settings/GeneralSettingsService.cs` (after `ShowOpenTerminal`, :344-348)
Defaults: `Get(TerminalType.CommandPrompt)`, `Get(string.Empty)`, `Get(string.Empty)`. Keys = property names.

### 4. `src/Files.App/Services/Settings/UserSettingsService.cs` (:29-42)
Add `[JsonSerializable(typeof(TerminalType))]` (stored as a number, like the other enums). Shared resource: serialization context.

### 5. New `src/Files.App/Utils/Shell/TerminalLauncher.cs` (namespace `Files.App.Utils.Shell`, already a global using)
Static, pure, no settings reads beyond what it is passed except `GetEffectiveType` / `GetLabel`:
- `string GetLabel(TerminalType type, bool asAdmin)` — maps a resolved type to the localized `Strings.*` below.
- `List<ProcessStartInfo> Build(TerminalType type, string customPath, string customArguments, string[] paths)`:
  - **CommandPrompt**: one process per folder. `FileName = "cmd.exe"`, `Arguments = $"/s /k \"pushd \"{path}\"\""`, `WorkingDirectory = path`, `UseShellExecute = true`. This is Explorer's own `Directory\shell\cmd` verb (`/s /k pushd "%V"`) with an extra outer quote pair so `/s` strips that pair instead of the path's quotes (keeps `&`/`^` in folder names literal). `pushd` also handles UNC paths (maps a temp drive), which `WorkingDirectory` can't.
  - **PowerShell**: one process per folder. `FileName` = full path of the first `pwsh.exe` found on `PATH` (covers the MSI install and the Store alias in WindowsApps), else `"powershell.exe"`. `ArgumentList = { "-NoExit", "-Command", "Set-Location -LiteralPath " + Win32Helper.ToPowerShellStringLiteral(path) }`, `WorkingDirectory = path`, `UseShellExecute = true`. PATH is scanned per call (cheap; reflects a fresh pwsh install without restart).
  - **WindowsTerminal**: one process for all folders, unchanged from upstream: `wt.exe -d p1 ; nt -d p2 ...` via `ArgumentList`, `UseShellExecute = false` (the admin override flips it to true as today). No default-console-host detection any more.
  - **Custom**: one process per folder. `FileName = Environment.ExpandEnvironmentVariables(customPath.Trim())`, `Arguments` = template with every `{path}` replaced by the folder path (when the placeholder is followed directly by `"` and the path ends in `\`, e.g. `C:\`, the backslash is doubled so `"{path}"` doesn't turn into an escaped quote), `WorkingDirectory = path`, `UseShellExecute = true` (resolves aliases / App Paths, allows `runas`). An empty template launches the executable with no arguments in the folder.
- Behaviour change worth noting: upstream's cmd fallback opened only the first selected folder; now cmd/PowerShell/Custom open one window per selected folder (selection is already capped by `Constants.Actions.MaxSelectedItems` in `GetIsExecutable`). The admin variant then shows one UAC prompt per folder; Windows Terminal stays one window with tabs.

### 6. `src/Files.App/Actions/Open/OpenTerminalAction.cs` (upstream file)
- New `internal TerminalType GetEffectiveTerminalType()`: `Custom` with a blank `CustomTerminalPath` counts as `CommandPrompt` (a half-configured Custom never leaves a dead menu entry); `WindowsDefault` resolves through upstream's `IsWindowsTerminalDefault()` (kept in place, unchanged) to `WindowsTerminal` or `CommandPrompt`. Label and launch both use it. No caching: the probe runs on each label read and launch, as upstream ran it per launch, and only when `WindowsDefault` is chosen.
- `Label` (:25-26) → `TerminalLauncher.GetLabel(GetEffectiveTerminalType(), IsAdmin)` with `protected virtual bool IsAdmin => false`.
- Constructor: also subscribe to `IUserSettingsService.GeneralSettingsService.PropertyChanged`; on `TerminalType` or `CustomTerminalPath` raise `OnPropertyChanged(nameof(Label))` so the command palette / Actions page / toolbar follow (context menus read `Label` when built, so they follow anyway). `SetValueByName` (automation `settings.set`) raises the same PropertyChanged, so that path works too.
- New `internal List<ProcessStartInfo> GetStartInfos()` = `GetProcessStartInfos(GetPaths())` (empty when no paths). `ExecuteAsync` (:53-75) uses it and starts each item inside the existing `DispatcherQueue.TryEnqueue` + `catch (Win32Exception)`.
- `GetProcessStartInfo(string[])` (:77-109) becomes `protected virtual List<ProcessStartInfo> GetProcessStartInfos(string[] paths)` → `TerminalLauncher.Build(GetEffectiveTerminalType(), CustomTerminalPath, CustomTerminalArguments, paths)`.
- `WindowsTerminalDelegationClsids` and `IsWindowsTerminalDefault` stay as they are (overseer ruling: small upstream diff, upstream behaviour reachable as a choice).

### 7. `src/Files.App/Actions/Open/OpenTerminalAsAdminAction.cs` (upstream file)
Replace the `Label` override with `protected override bool IsAdmin => true`; the start-info override loops over the base list setting `Verb = "runas"`, `UseShellExecute = true`. Description stays ("Open folder in terminal as administrator").

### 8. `OpenTerminalFromHomeAction.cs` / `OpenTerminalFromSidebarAction.cs` (upstream files)
Remove their `Label` and `Description` overrides (:11-15 in each): they repeat the base's old strings and would pin the Home/sidebar label to "Windows Terminal". The base now provides both.

### 9. `src/Files.App/Strings/en-US/Resources.resw` (shared)
Next to `OpenTerminal` / `OpenTerminalAsAdmin` (:2538-2543), keep both (they now mean Windows Terminal) and add:
`OpenInCommandPrompt` "Open in Command Prompt", `OpenInCommandPromptAsAdmin` "Open in Command Prompt as administrator", `OpenInPowerShell` "Open in PowerShell", `OpenInPowerShellAsAdmin` "Open in PowerShell as administrator", `OpenInCustomTerminal` "Open in terminal", `OpenInCustomTerminalAsAdmin` "Open in terminal as administrator".
Next to `ShowOpenTerminal` (:4660) add: `TerminalSetting` "Terminal", `TerminalSettingDescription` "Opens from the right-click menu and with Ctrl+' (Ctrl+Shift+' as administrator)", `CommandPrompt` "Command Prompt", `PowerShell` "PowerShell", `WindowsTerminal` "Windows Terminal", `WindowsDefaultTerminal` "Windows default terminal", `CustomTerminalArgumentsDescription` "{path} is replaced with the folder's path". Reuse existing `Custom`, `PathOrAlias`, `Arguments`.
Change the en-US value of `ShowOpenTerminal` from "Show option to open folders in Windows Terminal" to "Show option to open folders in a terminal" (other locales keep their old translation until upstream-style re-translation; acceptable for a personal fork).

### 10. New `src/Files.App/ViewModels/Settings/TerminalSettingsViewModel.cs`
`ObservableObject`, same shape as `ContextMenuDeclutterViewModel` (no event subscriptions, so `SettingsSearchIndexer` constructing `GeneralPage` stays cheap): `SelectedTerminalIndex` (int ↔ `TerminalType`, also raises `IsCustomTerminal`), `IsCustomTerminal`, `CustomTerminalPath`, `CustomTerminalArguments` (setters write the setting only when changed).

### 11. `src/Files.App/Views/Settings/GeneralPage.xaml(.cs)` (shared Settings page)
- `.xaml.cs`: `public TerminalSettingsViewModel Terminal { get; } = new();` next to `ContextMenuDeclutter` (:12).
- `.xaml`: in the "Right-click menu" section, directly after the "Context menu options" expander closes (:328) and before "Overflow Options" (:330), so it sits next to the `ShowOpenTerminal` toggle:
  - `SettingsCard` Header `TerminalSetting`, Description `TerminalSettingDescription`, HeaderIcon glyph `&#xE756;` (the action's own glyph), content `uc:ComboBoxEx` with five `ComboBoxItem`s (`CommandPrompt`, `PowerShell`, `WindowsTerminal`, `Custom`, `WindowsDefaultTerminal`), `SelectedIndex="{x:Bind Terminal.SelectedTerminalIndex, Mode=TwoWay}"` — same pattern as the tab-scroll-direction card on this page.
  - Two `SettingsCard`s visible only when `Terminal.IsCustomTerminal` (`BoolVisibilityConverter`): Header `PathOrAlias` with a `TextBox` bound TwoWay to `CustomTerminalPath`; Header `Arguments`, Description `CustomTerminalArgumentsDescription`, `TextBox` bound to `CustomTerminalArguments`. Default `LostFocus` update trigger, so `user_settings.json` isn't rewritten per keystroke. `MinWidth` on the TextBoxes from an existing resource if one fits, else a plain width like neighbouring cards.
- Page: **Settings > General > Right-click menu**. Batch 1's three cards (icon row, hidden shell entries, New menu) are untouched.

### 12. Automation (shared `AutomationHandlers.cs` + new `AutomationHandlers.Terminal.cs`)
- `AutomationHandlers.cs`: one `Methods` entry `("terminal.preview", "What Open in terminal would launch for the active pane's selection (or the folder when nothing is selected), without launching. Params: code (OpenTerminal (default), OpenTerminalAsAdmin, OpenTerminalFromSidebar, OpenTerminalFromHome).")` and one switch case `"terminal.preview" => Done(TerminalPreview(p))`.
- `AutomationHandlers.Terminal.cs`: looks the command up through `ICommandManager[code]`, requires its `ActionCommand.Action` to be an `OpenTerminalAction`, and returns `{ code, label, executable, terminalType (setting), effectiveType, launches: [{ fileName, arguments, argumentList: [...], workingDirectory, verb, useShellExecute }] }` built with `System.Text.Json.Nodes` from `action.GetStartInfos()` — the same call `ExecuteAsync` makes.

## Verification

Mine: focused build `msbuild -restore src/Files.App/Files.App.csproj -p:Configuration=Debug -p:Platform=x64 -v:quiet -clp:ErrorsOnly` (then full `Files.slnx`), no new IL2xxx/IL3xxx warnings in added code.

Overseer, on merged main with the app running hidden (`files_auto.py` from the repo root; settings are restored at the end):

```powershell
python .claude/scripts/files_auto.py navigate path=C:\Windows
python .claude/scripts/files_auto.py select 'names=[]'
python .claude/scripts/files_auto.py terminal.preview
#   effectiveType CommandPrompt; one launch: fileName cmd.exe, arguments /s /k "pushd "C:\Windows"", workingDirectory C:\Windows, verb "", useShellExecute true
python .claude/scripts/files_auto.py contextMenu shell=false
#   background menu shows "Open in Command Prompt"
python .claude/scripts/files_auto.py select 'names=["System32","Web"]'
python .claude/scripts/files_auto.py terminal.preview
#   two cmd launches, one per folder
python .claude/scripts/files_auto.py contextMenu 'names=["System32","Web"]' shell=false
#   "Open in Command Prompt"
python .claude/scripts/files_auto.py terminal.preview code=OpenTerminalAsAdmin
#   label "Open in Command Prompt as administrator"; both launches verb runas
python .claude/scripts/files_auto.py --json '{"values": {"TerminalType": 1}}' settings.set
python .claude/scripts/files_auto.py terminal.preview
#   fileName = full pwsh.exe path if installed, else powershell.exe; argumentList [-NoExit, -Command, Set-Location -LiteralPath 'C:\Windows\System32']; label "Open in PowerShell"
python .claude/scripts/files_auto.py --json '{"values": {"TerminalType": 2}}' settings.set
python .claude/scripts/files_auto.py terminal.preview
#   one launch: wt.exe, argumentList [-d, C:\Windows\System32, ;, nt, -d, C:\Windows\Web], useShellExecute false; label "Open in Windows Terminal"
python .claude/scripts/files_auto.py --json '{"values": {"TerminalType": 3, "CustomTerminalPath": ""}}' settings.set
python .claude/scripts/files_auto.py terminal.preview
#   effectiveType CommandPrompt (blank custom path falls back); label "Open in Command Prompt"
python .claude/scripts/files_auto.py --json '{"values": {"CustomTerminalPath": "%SystemRoot%\\System32\\cmd.exe", "CustomTerminalArguments": "/k title \"{path}\""}}' settings.set
python .claude/scripts/files_auto.py navigate path=C:\
python .claude/scripts/files_auto.py select 'names=[]'
python .claude/scripts/files_auto.py terminal.preview
#   effectiveType Custom; fileName C:\WINDOWS\System32\cmd.exe; arguments /k title "C:\\"; workingDirectory C:\; label "Open in terminal"
python .claude/scripts/files_auto.py --json '{"values": {"TerminalType": 4}}' settings.set
python .claude/scripts/files_auto.py terminal.preview
#   effectiveType WindowsTerminal or CommandPrompt, matching what Windows reports as the default terminal on this machine; label matches it
python .claude/scripts/files_auto.py commands filter=Terminal
#   OpenTerminal / OpenTerminalAsAdmin labels follow the setting
python .claude/scripts/files_auto.py --json '{"values": {"TerminalType": 0, "CustomTerminalPath": "", "CustomTerminalArguments": ""}}' settings.set
```

Settings UI (overseer, `window.show`): General > Right-click menu shows the Terminal card with the five choices; the two Custom cards appear only for Custom; edits persist (`settings.get keys=["TerminalType","CustomTerminalPath","CustomTerminalArguments"]`).

Manual, for Harald (spawns real windows): Ctrl+' on a folder opens cmd there; Ctrl+Shift+' opens elevated cmd in the folder (not System32); each choice once.

## Out of scope

- An error dialog when a Custom executable fails to start (upstream swallows `Win32Exception` silently; unchanged).
- Making the Open in terminal entry depend on Shift, or moving it in the menu.
- Other locales' strings for the new resources (en-US only, per fork convention).
