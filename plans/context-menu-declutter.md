# Context-menu declutter (cards 7bd9851e, 47e48e26, 6998b9d6)

Branch `feat/context-menu-declutter`, worktree `.claude/worktrees/context-menu-declutter`, one PR for three cards.

## Context

Three right-click-menu preferences for Harald's fork, all following the fork principle that noise is hidden by default or moved into Settings, never deleted without a way back.

1. **Icon row (7bd9851e).** `FastContextFlyout.Build` (`src/Files.App/Helpers/MenuFlyout/FastContextFlyout.cs:74-89`) always pulls the `IsPrimary` models (Cut, Copy, Paste to selection, Rename, Share, Delete, Properties in the file-area menu, built in `ContentPageContextFlyoutFactory.cs:491-546`; Copy and Properties in the sidebar, `SidebarViewModel.cs:1112-1131`; the widget menus) into a horizontal icon-button row. No setting gates it. Ruling: a setting, default OFF; when off the primary commands render as ordinary text entries where they sit in the model list (they already sit in Explorer-like positions: Cut/Copy/Paste after the Run-as separator, Rename/Share/Delete/Properties further down).
2. **Shell-entry blocklist (47e48e26).** Shell entries are only filtered by the hard-coded verb/label set in `ShellContextFlyoutFactory.GetShellContextmenuAsync` → `FilterMenuItems` (`src/Files.App/Data/Factories/ShellContextFlyoutHelper.cs:82-104`). Ruling: a user-editable list in Settings, matching by label and by verb, applied to the file, folder and background menus including inside "Show more options". Ships EMPTY (Harald decides what is junk among VLC, Git GUI/Bash, ChatGPT, WinMerge, 7-Zip, Notepad++, "Transfer a copy...", "Sync or back up this folder", "Give access to", "Restore previous versions", "Send to").
3. **New submenu filter (6998b9d6).** The New submenu lists every ShellNew entry from `IAddItemService.GetEntries()` (cached at startup by `AddItemService`, `src/Files.App/Services/Windows/WindowsShellService.cs`), with no filter. Ruling: a list of hidden entries edited as a checklist in Settings; default shows all; Folder / File / Shortcut built-ins always stay.

All shell entries in every Files menu flow through one function, `ShellContextFlyoutFactory.LoadMenuFlyoutItemAsync` (`ShellContextFlyoutHelper.cs:118-213`), which also runs for every lazily loaded shell submenu (`LoadSubMenuAction`, :197-201). Its callers cover the item menu (`BaseLayoutPage.cs:722`), the background menu (`BaseLayoutPage.cs:967`), the sidebar and widget menus (`ShellContextFlyoutHelper.LoadShellMenuItemsAsync`, :301) and the automation `contextMenu` method (`AutomationHandlers.cs:331`). Filtering there is the single choke point.

## Constraints that shaped the design

- Keep upstream diffs small; fork logic goes in new files, upstream files get call-site edits only.
- Settings search (`SettingsSearchIndexer.Walk`) only indexes `SettingsCard`/`SettingsExpander` that are direct children of panels on the page, so the new cards go inline in `GeneralPage.xaml`, not in a separate UserControl.
- The sibling session-restore card is likely editing the Startup part of `GeneralPage.xaml` / `GeneralViewModel.cs`. To keep merges trivial I do not touch `GeneralViewModel.cs` at all: the new settings UI gets its own view model, exposed by a one-line property in `GeneralPage.xaml.cs`, and the XAML goes into the "Right-click menu" section, far from Startup.
- "Open with" and "Send to" are not blocklistable: Files swaps its own placeholders for them (`BaseLayoutPage.cs:711-737`, `ShellContextFlyoutHelper.cs:308-338`), and hiding the shell entry would leave a dead "Send to" leaf in the menu. Send to already has its own toggle ("Show Send to menu", same Settings section). The blocklist ignores items whose verb is `openas`/`sendto` or whose label is the shell's Send-to label.
- Settings changes must apply live: every reader reads the setting when the menu is built, never caches it.
- No new serialized types: the two lists are `List<string>`, already registered in `UserSettingsJsonSerializationContext` (`UserSettingsService.cs:23`).

## Design

### Settings (shared resource: settings service)

`src/Files.App/Data/Contracts/IGeneralSettingsService.cs` and `src/Files.App/Services/Settings/GeneralSettingsService.cs`, next to `MoveShellExtensionsToSubMenu` (interface :208-211, implementation :254-258):

- `bool ShowContextMenuIconRow` — `Get(false)`. Primary commands as an icon row (true) or as text entries (false).
- `List<string>? HiddenShellMenuEntries` — `Get<List<string>>(null)`. Blocklist patterns.
- `List<string>? HiddenNewMenuEntries` — `Get<List<string>>(null)`. Hidden ShellNew extensions (e.g. `.accdb`), compared case-insensitively.

Keys are the property names, so `settings.set` drives all three.

### 1. Icon row — `FastContextFlyout.cs`

- Add `public static bool ShowsIconRow` reading `GeneralSettingsService.ShowContextMenuIconRow` through the existing lazy `userSettingsService` field (:23, :153).
- Add `internal static bool IsOmittedPrimary(ContextMenuFlyoutItemViewModel model)`: true for a primary model that is disabled while the icon row is off. Reason: the file menu forces some primary commands visible even when they can't run (`PasteItemToSelection` has `IsVisible = true`, :500-504) only so the icon row keeps a stable layout. As a text entry that would put a greyed "Paste" in every file menu, which Explorer doesn't do and the text-menu convention in this codebase avoids (the builder drops non-executable commands, `ContextMenuFlyoutItemViewModelBuilder.cs:44-50`).
- `Build` (:74-89): read `ShowsIconRow` once. When on, behaviour is unchanged. When off, `primary` is empty and the secondary filter keeps primary models that pass the same visibility test the row used (`!IsHidden && ShowItem`, :78) and are not `IsOmittedPrimary`; they render in place through `ContextFlyoutModelToElementHelper` like any other entry, with icon, access key and shortcut text (`KeyboardAcceleratorTextOverride`). With no row, `FinalizePrimaryRowPosition`/`ApplyEstimatedWidth` already no-op on `primaryRow is null`.
- Update the class/`Build` summary comments to say the row is optional.

This one change covers every host (file area, background, sidebar, widgets), since all call `Build`.

### 2. Shell-entry blocklist

New file `src/Files.App/Helpers/MenuFlyout/ShellMenuBlocklist.cs` (namespace `Files.App.Helpers`):

- `sealed class ShellMenuBlocklist` built by `static ShellMenuBlocklist FromSettings(IGeneralSettingsService)` from `HiddenShellMenuEntries` (trimmed, empties dropped).
- `bool Matches(string? verb, string label)`: true when any pattern matches the verb or the label, case-insensitive (`OrdinalIgnoreCase`). A pattern matches exactly, or as a glob where `*` stands for any text (small hand-written matcher, no Regex). The `*` lets one entry cover labels that carry variable text (e.g. `*VLC*` for both VLC entries).
- Seen-entries catalog for the Settings picker: `static void RecordSeen(string label)` and `static IReadOnlyList<string> GetSeenEntries()`. In-memory, this session only, de-duplicated case-insensitively, capped at 300, guarded by a lock. Not persisted: it is a convenience list of "what menus have shown since Files started".

`ShellContextFlyoutHelper.cs`, `LoadMenuFlyoutItemAsync` (:118-213):

- At the top (before the overflow split at :129), build the blocklist once from settings and drop non-separator items it matches: `menuFlyoutItems = menuFlyoutItems.Where(x => x.Type is MFT_SEPARATOR || !IsBlocked(x)).ToList();`. Filtering before the existing separator handling means the leading/trailing trim (:155-157) and the duplicate-separator skip (:163) clean up any separators a removed entry leaves behind; `FastContextFlyout.AddShellModels` trims again on the main/overflow split (:236-237).
- `IsBlocked(Win32ContextMenuItem)`: false for reserved items (verb `openas` or `sendto`, or label equal to the cached `shell32.dll` 30312 Send-to string already used at :185); otherwise `blocklist.Matches(item.CommandString, ExtractLabelAndAccessKey(item.Label).Label)` so `&` access-key markers never break a label match.
- Record each non-reserved, non-blocked labelled item (leaf or submenu) with `ShellMenuBlocklist.RecordSeen` where `CreateShellMenuItem` is called (:188, :208).
- Because lazy submenus and the "Show more options" contents are produced by this same function, the filter applies at every depth and in every menu.

The hard-coded `FilterMenuItems` set stays as it is (upstream's de-duplication, different purpose).

### 3. New submenu filter

New file `src/Files.App/Extensions/AddItemServiceExtensions.cs`: `public static List<ShellNewEntry> GetVisibleEntries(this IAddItemService service)` = `GetEntries()` minus entries whose `Extension` is in `HiddenNewMenuEntries` (case-insensitive). `GetEntries()` stays unfiltered (the Settings checklist and the automation listing need the full set).

Call sites switched to `GetVisibleEntries()`:

- `ContentPageContextFlyoutFactory.GetNewItemItems` (:708), the right-click New submenu. The Folder / File / Shortcut items above it are untouched.
- `Toolbar.xaml.cs` (:504), the toolbar's New dropdown, which is the same "New" list.

Not switched: `AddItemDialog.xaml.cs:48` (the "Create new item" dialog). It keeps every type, so a hidden type is still one dialog away when Harald occasionally needs it. Flagged for the overseer below.

### Settings UI — General page, "Right-click menu" section

New file `src/Files.App/ViewModels/Settings/ContextMenuDeclutterViewModel.cs` (`ObservableObject`, no event subscriptions, so nothing to dispose when the settings indexer builds the page):

- `bool ShowContextMenuIconRow` get/set through the setting.
- `ObservableCollection<string> HiddenShellEntries` loaded from the setting; `AddHiddenShellEntry(string?)` (trim, ignore empty or duplicate, save), `RemoveHiddenShellEntry(string)` (save); `IReadOnlyList<string> GetShellEntrySuggestions(string? query)` = seen entries not already hidden, containing the query, sorted.
- `ObservableCollection<NewMenuEntryItem> NewMenuEntries` from `IAddItemService.GetEntries()`; `NewMenuEntryItem` (same file, `ObservableObject`) has `Name`, `Extension`, `IsShown`; toggling `IsShown` rewrites `HiddenNewMenuEntries` from the unchecked items. One checkbox per extension (the setting's key): entries are grouped by extension case-insensitively and a group's names are joined with " / " (in practice `GetNewContextMenuEntries` yields one entry per `HKCR\.ext` key, so a group has one entry). Each card's description shows the extension, so two entries with the same name (e.g. "Microsoft Access Database" for `.accdb` and `.mdb`) are told apart. Entries with an empty extension can't be keyed, are not listed and always show (`GetVisibleEntries` never hides them).

`src/Files.App/Views/Settings/GeneralPage.xaml.cs`: `public ContextMenuDeclutterViewModel ContextMenuDeclutter { get; } = new();` plus the AutoSuggestBox / remove-button handlers (UI-control plumbing, delegating to the view model): `GotFocus` opens the suggestion list with all suggestions, `TextChanged` (user input) refilters, `QuerySubmitted` adds the chosen suggestion or the typed text and clears the box, the Add button does the same with the box text, the remove button removes its row's string.

`src/Files.App/Views/Settings/GeneralPage.xaml`, inserted after the "Show more options" overflow card (:333-339), before "Scrolling":

1. **SettingsCard** "Show cut, copy, rename and delete as an icon row", description "When off, they appear as regular entries in the menu.", ToggleSwitch bound to `ContextMenuDeclutter.ShowContextMenuIconRow`.
2. **SettingsExpander** "Hidden context menu entries", description "Entries other apps add to the right-click menu, hidden everywhere including under Show more options. Match an entry's name or verb; * matches any text." Its `ItemsHeader` copies the startup-pages editor pattern (:100-169): a row with an AutoSuggestBox (placeholder "Type a name, or pick one seen in a menu since Files started") and an Add button, then a ListView of the hidden entries, each with the existing Remove button template (glyph E74D, `Remove` string).
3. **SettingsExpander** "Entries in the New menu", description "Choose which file types the New menu offers. Folder, File and Shortcut always appear.", `ItemsSource` = `NewMenuEntries`, item template a SettingsCard (header = name, description = extension) with a CheckBox bound two-way to `IsShown`.

All three headers are plain `ResourceString` strings, so settings search finds them.

### Strings — `src/Files.App/Strings/en-US/Resources.resw` (shared resource)

Inserted right after `SettingsContextMenuOverflow` (:719-721): `ShowContextMenuIconRow`, `ShowContextMenuIconRowDescription`, `HiddenShellMenuEntries`, `HiddenShellMenuEntriesDescription`, `HiddenShellMenuEntriesPlaceholder`, `NewMenuEntries`, `NewMenuEntriesDescription`. Existing `Add` and `Remove` are reused.

### Automation — `src/Files.App/Automation/AutomationHandlers.cs` (shared resource)

- `MenuTree` (:339-348): `primary` becomes `item.IsPrimary && FastContextFlyout.ShowsIconRow` (true only for entries the user sees in the icon row); entries with `FastContextFlyout.IsOmittedPrimary` are left out, as the menu leaves them out; new field `verb` = `(item.Tag as Win32ContextMenuItem)?.CommandString` so verbs can be picked for blocklist checks.
- `NewMenu` (:351-359): each entry gains `hidden` (true when in `HiddenNewMenuEntries`); still lists every entry.
- Help strings for `contextMenu` and `newMenu` updated accordingly.

## Verification

Build (mine): `msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly` from the worktree, zero errors, no new IL2xxx/IL3xxx warnings in added code. The cold baseline build of this worktree already passed (EXIT=0).

App checks (overseer, on merged main; `python .claude/scripts/files_auto.py ...`, after `ping` reports ready):

Icon row:

1. `settings.get 'keys=["ShowContextMenuIconRow"]'` → null (default off).
2. `navigate path=C:\Windows`, then `contextMenu 'names=["notepad.exe"]' shell=false` → the `files` tree contains Cut, Copy, Rename, Delete, Properties (and Share if executable) with `primary: false`; there is no disabled Paste entry.
3. `settings.set 'values={"ShowContextMenuIconRow": true}'`, repeat step 2 → the same entries plus Paste report `primary: true`.
4. Visual (`window.show`, right-click notepad.exe, screenshot): off = no icon row, the commands are text rows with icons and shortcut text; on = the icon row is back. Also right-click a sidebar pinned folder: off = Copy and Properties are text rows. Restore with `settings.set 'values={"ShowContextMenuIconRow": false}'`.

Blocklist:

1. `contextMenu 'names=["notepad.exe"]'` → note a top-level shell entry (e.g. "Edit with Notepad++") and an entry inside a shell submenu (e.g. 7-Zip → "Add to archive..."), with their `verb`s.
2. `settings.set 'values={"HiddenShellMenuEntries":["Edit with Notepad++","Add to archive..."]}'`, repeat → neither text appears anywhere in the `shell` tree (search recursively); the 7-Zip submenu remains with its other children.
3. Verb match: set the list to one entry's `verb` (from step 1) → that entry is gone.
4. Wildcard: `["*VLC*"]` on a media file or folder → both VLC entries are gone.
5. Background: `contextMenu` (no names) in a Git checkout folder with `["Open Git GUI here","Open Git Bash here"]` (labels as reported) → gone from the background `shell` tree.
6. Reserved: `["Send to","Open with"]` → the `shell` tree still has the `sendto` and `openas` entries.
7. Reset: `settings.set 'values={"HiddenShellMenuEntries":[]}'`.
8. Visual: right-click a file, open Settings → General → "Hidden context menu entries", focus the box → the seen entries are suggested; pick one and Add → listed and gone from the next right-click (main menu and "Show more options"); Remove → back.

New submenu:

1. `newMenu` → every entry `hidden: false`.
2. `settings.set 'values={"HiddenNewMenuEntries":[".accdb",".xlsx"]}'` (use two extensions `newMenu` listed) → `newMenu` reports those two `hidden: true`.
3. `contextMenu` (no names) → the New submenu's children no longer include those two names; Folder, File and Shortcut are still there.
4. Visual: toolbar New dropdown lacks them too; Settings → General → "Entries in the New menu" shows them unchecked; checking one → `settings.get 'keys=["HiddenNewMenuEntries"]'` drops it.
5. Same-name and shared-extension behaviour: the Settings list has one row per extension and each row's description is its extension (the two "Microsoft Access Database" rows show different extensions); hiding an extension hides every ShellNew entry with that extension. Entries without an extension are not listed and always show.
6. Reset with `[]`.

## Out of scope

- Hiding Files' own entries (Open in new tab, Copy path, ...): upstream already has toggles under "Context menu options".
- Persisting the seen-entries catalog across restarts, or a "hide this entry" action inside the menu itself.
- Reordering entries, or blocking entries in the toolbar's Open with dropdown.
- The "Create new item" dialog (keeps all types, see the question below).
- The hard-coded shell de-duplication set in `FilterMenuItems` and the "Open in Windows Terminal" entry (card 5ac9913e follows).

## Overseer rulings (plan approved)

1. Disabled primary commands (chiefly "Paste" on a file selection) are left out in text mode rather than shown greyed: yes.
2. The New filter applies to the right-click New submenu and the toolbar New dropdown; the "Create new item" dialog stays unfiltered as the way back: yes.
3. "Open with" and "Send to" are reserved and can't be blocked: yes.
4. Blocklist patterns support `*` as a wildcard: yes.
5. Added: handle ShellNew entries sharing an extension or lacking one (one checkbox per extension, see the view model and Verification step 5 of the New submenu).
