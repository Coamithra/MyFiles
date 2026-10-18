# Trim the View menu (card 88509645)

Branch `feat/view-menu`, worktree `.claude/worktrees/view-menu`.

## Context

The card asks for a configurable "View menu": the user picks which view options appear there, everything stays reachable elsewhere, and the app ships with the stock set so nothing disappears until it is turned off. In Files the View menu is the toolbar's **Layout** button flyout (`src/Files.App/UserControls/Toolbar.xaml:264-842`, `LayoutOptionsButton` / `LayoutFlyout`). It is fixed today: toolbar customization (`CustomToolbarItems`, `ToolbarCustomizationViewModel`) only governs the command buttons, not this flyout. The background right-click menu has a matching **Layout** submenu (`src/Files.App/Data/Factories/ContentPageContextFlyoutFactory.cs:113-149`) with the same layout types plus Adaptive.

The flyout today, top to bottom:

1. Header TextBlock "Layout" (`Toolbar.xaml:288`)
2. Five layout radios in a horizontal StackPanel (`:291-416`): Details, List, Cards, Grid, Columns
3. Divider Border (`:419-423`)
4. "Size" TextBlock (`:426`) + a Grid holding one size slider per layout, only the active layout's visible (`:427-759`)
5. Divider Border (`:762-766`)
6. Toggle Grid with `RowSpacing="4"` (`:769-836`): Hidden items, File extensions, Adaptive (Adaptive is already `x:Load`ed on `Commands.LayoutAdaptive.IsExecutable`, false while "Sync folder preferences across directories" is on)

There is direct precedent in the fork for this kind of setting: `HiddenNewMenuEntries` (a `List<string>?` of hidden ids, default null) with a checkbox-per-entry `SettingsExpander` on the General page (`ContextMenuDeclutterViewModel`, `GeneralPage.xaml:430-445`). This design copies that shape.

## Design decisions (and the constraints behind them)

- **Granularity: 9 entries**, ids are stable strings, reusing `CommandCodes` names where a command exists (via `nameof`, so a rename breaks the build rather than silently orphaning stored ids): `LayoutDetails`, `LayoutList`, `LayoutCards`, `LayoutGrid`, `LayoutColumns`, `Size` (the size section; the slider shown always follows the active layout, so one entry, not five), `ToggleShowHiddenItems`, `ToggleShowFileExtensions`, `LayoutAdaptive`. Labels come from the commands' own localized `Label` (`Commands[code].Label`) and `Strings.Size`, so the picker reads exactly like the flyout and needs no per-entry strings.
- **One setting**: `ILayoutSettingsService.HiddenLayoutMenuEntries` (`List<string>?`, default null = stock set). `List<string>` is already in `UserSettingsJsonSerializationContext` (`UserSettingsService.cs:23`), so no serializer change. Unknown ids in the list are ignored.
- **Hidden means hidden, including the active layout.** If the active layout's radio is hidden, no radio is checked, but the flyout still works: the Size section keeps following the active layout (it is keyed off `ViewModel.Is*Layout`, not the radios), and the toolbar button's own icon already shows the active layout (`ViewModel.LayoutThemedIcon`, `Toolbar.xaml:278`). Alternative I rejected: force-show the active layout's radio even when hidden. That makes "hide all layout radios" impossible (one radio always lingers, which is noise for someone who switches by Ctrl+Shift+1..6) and makes the radio row change shape as layouts change. **Overseer: flag this if you prefer the force-show rule; it is a two-line change in `IsEntryShown`.**
- **Everything stays reachable**: layout types via Ctrl+Shift+1..6, the command palette and Settings > Layout > Layout type; Size via Ctrl+mouse wheel / Ctrl+Shift+= / Ctrl+Shift+- (`LayoutIncreaseSize`/`LayoutDecreaseSize`, also addable to the toolbar via toolbar customization); Hidden items / File extensions via Settings > Files & folders and their hotkeys; Adaptive via Ctrl+Shift+6 and Settings > Layout > Layout type.
- **No empty flyout, no dangling dividers.** Three sections: Layout (header + radios; visible if any radio shown), Size (visible if `Size` shown), Toggles (visible if any toggle *effectively* shown, where Adaptive also requires `Commands.LayoutAdaptive.IsExecutable`). A divider shows only when the section after it is visible and some section before it is visible. If **nothing** is shown, the Layout **toolbar button itself collapses** rather than opening an empty flyout (hiding everything is a legitimate "I don't want this button" choice, consistent with the declutter principle; it comes back by re-ticking any entry).
- **The background menu's Layout submenu follows the same setting** for the six entries it has (the five layout types + Adaptive). Consistency argues for it: one "which layout options do I see" choice, not two. If all six are hidden the submenu itself is dropped (no empty submenu). Sort by / Group by submenus and the toolbar's Sort & group flyout are out of scope.
- **One source of truth for visibility**: a new app-lifetime singleton `LayoutMenuVisibility` (ObservableObject) owns every visibility rule. The Toolbar XAML binds its properties, the context-menu factory calls it, the Settings picker writes the setting it reads, and the automation method reports straight from it, so the pipe check cannot drift from what the XAML binds. It is a singleton (not per-Toolbar) because it only listens to other app-lifetime singletons (the layout settings service and `Commands.LayoutAdaptive`), so there is nothing to unsubscribe; the Toolbar's existing `x:Bind`s to `Commands.*` already rely on the same pattern.

## File-by-file changes

### New: `src/Files.App/Data/Models/LayoutMenuVisibility.cs`

`public sealed partial class LayoutMenuVisibility : ObservableObject` (namespace `Files.App.Data.Models`, already globally imported). UI-thread only, like its callers.

- `public static LayoutMenuVisibility Instance => instance ??= new();`
- `public static IReadOnlyList<string> EntryIds` = the 9 ids above, in flyout order (used by the picker and the automation method).
- `public static string GetLabel(string id)`: `Size` -> `Strings.Size.GetLocalizedResource()`, otherwise `Commands[Enum.Parse<CommandCodes>(id)].Label` (or a small switch over the 8 command ids to avoid `Enum.Parse`; implementer's choice, both AOT-safe).
- Constructor: read `HiddenLayoutMenuEntries` into a `HashSet<string>` (Ordinal); subscribe `layoutSettings.PropertyChanged` (on `nameof(ILayoutSettingsService.HiddenLayoutMenuEntries)` re-read the set) and `Commands.LayoutAdaptive.PropertyChanged` (on `IsExecutable`); both then call `OnPropertyChanged(string.Empty)` so every `x:Bind` re-evaluates.
- `public bool IsEntryHidden(string id)` – setting only.
- `public bool IsEntryShown(string id)` – `!IsEntryHidden(id)`, and for `LayoutAdaptive` also `Commands.LayoutAdaptive.IsExecutable` (mirrors the existing `x:Load` at `Toolbar.xaml:821,830`).
- Per-entry properties for XAML: `ShowDetails`, `ShowList`, `ShowCards`, `ShowGrid`, `ShowColumns`, `ShowSize`, `ShowHiddenItemsToggle`, `ShowFileExtensionsToggle`, `ShowAdaptiveToggle`, each `=> IsEntryShown(<id>)`.
- Section properties: `ShowLayoutSection` (any of the 5 radios), `ShowSizeSection` (= `ShowSize`), `ShowTogglesSection` (any of the 3 toggles), `ShowSizeDivider` (`ShowSizeSection && ShowLayoutSection`), `ShowTogglesDivider` (`ShowTogglesSection && (ShowLayoutSection || ShowSizeSection)`), `IsAnyEntryShown` (any section).

### `src/Files.App/Data/Contracts/ILayoutSettingsService.cs` (after `ColumnsViewSize`, :227)

```csharp
/// <summary>
/// Gets or sets the ids of the Layout menu entries to hide (see <see cref="LayoutMenuVisibility.EntryIds"/>).
/// </summary>
List<string>? HiddenLayoutMenuEntries { get; set; }
```

### `src/Files.App/Services/Settings/LayoutSettingsService.cs` (after `ColumnsViewSize`, :328-332)

`public List<string>? HiddenLayoutMenuEntries { get => Get<List<string>>(null); set => Set(value); }` – same shape as `HiddenNewMenuEntries` in `GeneralSettingsService.cs:284`.

### `src/Files.App/UserControls/Toolbar.xaml.cs` (one line, next to the other injected members at :22-27)

`private LayoutMenuVisibility LayoutMenu { get; } = LayoutMenuVisibility.Instance;` – the binding source for the XAML. The batch-1 New-dropdown line is not touched.

### `src/Files.App/UserControls/Toolbar.xaml` (Layout flyout only)

- `LayoutOptionsButton` (:265): add `Visibility="{x:Bind LayoutMenu.IsAnyEntryShown, Mode=OneWay}"` (keeps its existing `x:Load` on `IsPageTypeNotHome`).
- Header TextBlock (:288) and the radios StackPanel (:291): `Visibility="{x:Bind LayoutMenu.ShowLayoutSection, Mode=OneWay}"`.
- Each RadioButton (:294, :318, :343, :368, :393): `Visibility="{x:Bind LayoutMenu.ShowDetails/ShowList/ShowCards/ShowGrid/ShowColumns, Mode=OneWay}"`.
- First divider Border (:419): `Visibility="{x:Bind LayoutMenu.ShowSizeDivider, Mode=OneWay}"`.
- "Size" TextBlock (:426) and the sizes Grid (:427): `Visibility="{x:Bind LayoutMenu.ShowSizeSection, Mode=OneWay}"`.
- Second divider Border (:762): `Visibility="{x:Bind LayoutMenu.ShowTogglesDivider, Mode=OneWay}"`.
- Toggle options (:769-836): replace the 3-row `Grid RowSpacing="4"` with a `StackPanel Spacing="4"` (visibility `ShowTogglesSection`) holding one 2-column Grid per toggle (same `*`/`Auto` columns, same TextBlock + ToggleSwitch with all their existing attributes, minus `Grid.Row`), each row Grid with `Visibility` bound to `ShowHiddenItemsToggle` / `ShowFileExtensionsToggle` / `ShowAdaptiveToggle`. Reason: a Grid applies `RowSpacing` between rows even when a row's content is collapsed, which would leave stray gaps; a StackPanel skips collapsed children. The Adaptive row's TextBlock/ToggleSwitch keep their existing `x:Name` + `x:Load`; the row Grid's Visibility already folds in `IsExecutable`.
- Fallback named in advance (not a STOP): if the visual check shows the outer `StackPanel Spacing="12"` still reserving space for collapsed sections, switch those section elements from `Visibility` to `x:Load` on the same properties (adding `x:Name`s).

### `src/Files.App/Data/Factories/ContentPageContextFlyoutFactory.cs` (Layout submenu, :113-149)

- Read `var layoutMenu = LayoutMenuVisibility.Instance;` once at the top of `GetBaseItemMenuItems`.
- Each of the six builders gets `IsVisible = !layoutMenu.IsEntryHidden(<id>) && Commands.<cmd>.IsExecutable`. This is exactly the builder's current default (`isVisible ?? command.IsExecutable`, `ContextMenuFlyoutItemViewModelBuilder.cs:21`) when the entry isn't hidden, so the stock menu is unchanged.
- The submenu's `ShowItem = !itemsSelected` (:117) becomes `!itemsSelected && <any of the six built items has ShowItem>` so an all-hidden submenu disappears. Build the six items into a local list first so the check reads the built items.

### New: `src/Files.App/ViewModels/Settings/LayoutMenuSettingsViewModel.cs`

Mirrors `ContextMenuDeclutterViewModel`'s New-menu half: `ObservableCollection<LayoutMenuEntryItem> Entries` built from `LayoutMenuVisibility.EntryIds` with `GetLabel(id)` and `IsShown = !hidden.Contains(id)`; toggling any checkbox saves `HiddenLayoutMenuEntries = [.. Entries.Where(e => !e.IsShown).Select(e => e.Id)]`, or `null` when nothing is hidden (keeps the stock state byte-identical to never-changed). `LayoutMenuEntryItem` (Id, Label, IsShown with a save callback) lives in the same file, same shape as `NewMenuEntryItem`.

### `src/Files.App/Views/Settings/LayoutPage.xaml.cs` + `LayoutPage.xaml`

- Code-behind: `public LayoutMenuSettingsViewModel LayoutMenu { get; } = new();` (as `GeneralPage.xaml.cs:12` does for `ContextMenuDeclutter`).
- XAML: a new `SettingsExpander` right after the "Layout Type" card (:63), before "Sorting & grouping" (:65): `Header="{helpers:ResourceString Name=LayoutMenuEntries}"`, `Description="{helpers:ResourceString Name=LayoutMenuEntriesDescription}"`, a FontIcon header icon, `ItemsSource="{x:Bind LayoutMenu.Entries, Mode=OneWay}"`, item template = `SettingsCard Header="{x:Bind Label}"` with a `CheckBox IsChecked="{x:Bind IsShown, Mode=TwoWay}"` (copy of `GeneralPage.xaml:438-444`). The settings search indexer (`SettingsSearchIndexer.cs`) walks the page, so the expander is searchable with no extra registration.

### `src/Files.App/Strings/en-US/Resources.resw` (next to `LayoutType`, :4198)

- `LayoutMenuEntries` = "Layout menu"
- `LayoutMenuEntriesDescription` = "Choose what the toolbar's Layout menu shows. Layout types you hide also leave the Layout submenu of the right-click menu. If you hide everything, the Layout button disappears."

### `src/Files.App/Automation/AutomationHandlers.cs` (shared; 2 lines) + new `AutomationHandlers.ViewMenu.cs`

- `Methods` entry: `("layoutMenu", "Entries of the toolbar's Layout menu as the flyout binds them: hidden = hidden in Settings, shown = visible now (Adaptive also needs to be applicable); sections and dividers that show; button = the Layout button shows at all; activeLayout.")` and the switch case `"layoutMenu" => Done(LayoutMenu())`.
- `AutomationHandlers.ViewMenu.cs` (partial): `LayoutMenu()` builds a `JsonObject` from `LayoutMenuVisibility.Instance` only: `button` (`IsAnyEntryShown`), `activeLayout` (`IDisplayPageContext.LayoutType.ToString()`), `entries` = `EntryIds` mapped to `{id, label, hidden: IsEntryHidden, shown: IsEntryShown}`, `sections` = `{layout, sizeDivider, size, togglesDivider, toggles}`. System.Text.Json.Nodes only, `(JsonNode)` casts into `JsonArray`.
- The background menu's Layout submenu is checked with the existing `contextMenu` method (it builds through the same factory).

## Shared resources touched (for sequencing)

`Toolbar.xaml` (Layout flyout region only) + one line in `Toolbar.xaml.cs`; `ILayoutSettingsService` / `LayoutSettingsService` (one property each, no serialization-context change); `Resources.resw` (two strings next to `LayoutType`); Layout settings page (`LayoutPage.xaml(.cs)`); `AutomationHandlers.cs` (one `Methods` entry + one case); `ContentPageContextFlyoutFactory.cs` (Layout submenu only). Not touched: NavigationToolbar/Omnibar, OpenTerminalAction, sidebar, multi-window code, General page.

## Verification

Build (mine): `msbuild -restore Files.slnx -p:Configuration=Debug -p:Platform=x64 -p:RestorePackagesConfig=true -v:quiet -clp:ErrorsOnly` via the DevShell, error-free, no new IL2xxx/IL3xxx in the added code. (Cold build of untouched `main` in this worktree: EXIT=0.)

Pipe checks (overseer, on merged main, `-Hidden` launch, after `ping` is ready):

1. Stock: `navigate path=C:\Windows`, then `layoutMenu` -> all 9 entries `hidden:false, shown:true` (Adaptive `shown:true` while `SyncFolderPreferencesAcrossDirectories` is false), every section/divider true, `button:true`. `contextMenu` (no names) -> `files` has the Layout submenu with Details, Cards, List, Grid, Columns, Adaptive, as before.
2. `settings.set 'values={"HiddenLayoutMenuEntries":["LayoutCards","LayoutColumns"]}'` -> `layoutMenu`: those two `hidden:true, shown:false`, others shown, sections unchanged. `contextMenu`: Layout submenu has Details, List, Grid, Adaptive only.
3. Hide the active layout: `command code=LayoutDetails`, then `settings.set 'values={"HiddenLayoutMenuEntries":["LayoutDetails"]}'` -> `layoutMenu`: `activeLayout:"Details"`, LayoutDetails `shown:false`, `sections.size:true`, `button:true`.
4. All layout radios + Adaptive hidden: `["LayoutDetails","LayoutList","LayoutCards","LayoutGrid","LayoutColumns","LayoutAdaptive"]` -> `sections.layout:false`, `sizeDivider:false` (Size is now first), `size:true`, `togglesDivider:true`, `toggles:true`; `contextMenu` has no Layout submenu at all (Sort by / Group by still there).
5. Size + all toggles hidden: `["Size","ToggleShowHiddenItems","ToggleShowFileExtensions","LayoutAdaptive"]` -> `layout:true`, `sizeDivider:false`, `size:false`, `togglesDivider:false`, `toggles:false`.
6. Adaptive not applicable: `settings.set 'values={"SyncFolderPreferencesAcrossDirectories":true,"HiddenLayoutMenuEntries":["ToggleShowHiddenItems","ToggleShowFileExtensions"]}'` -> LayoutAdaptive `hidden:false, shown:false`, `toggles:false`, `togglesDivider:false`.
7. Everything hidden (all 9 ids) -> `button:false`, every section false.
8. Reset: `settings.set 'values={"HiddenLayoutMenuEntries":null,"SyncFolderPreferencesAcrossDirectories":false}'` -> back to check 1.

Visual (overseer, `window.show` + screenshots): open the Layout flyout in states 2, 4 and 5 and confirm no stray gaps or dividers; open Settings > Layout and confirm the "Layout menu" expander lists the 9 entries with checkboxes and that unticking one hides it in the flyout immediately.

## Out of scope

- The toolbar's Sort & group flyout (`ArrangementOptions`, `Toolbar.xaml:154-262`) and the background menu's Sort by / Group by submenus.
- Per-layout size sliders as separate entries (only the active layout's slider is ever shown, so one Size entry covers it).
- Live propagation to other open windows: each window is its own process with its own settings cache (existing behaviour for every setting).
