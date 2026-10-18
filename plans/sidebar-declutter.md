# Sidebar declutter (card eb8e8418)

Branch `feat/sidebar-declutter`, worktree `.claude/worktrees/sidebar-declutter`.

## Context

The card: the user decides which sidebar sections show. Files already stores a `Show<X>Section` setting for Pinned, Bookmarks (fork, batch 1), Library (default off), Drives, Cloud Drives, Network, WSL and Tags. They are reachable in two places only: the toggle list on the right-click menu of the sidebar's empty area (`MainPage.xaml:58-70`, `SidebarContextMenu`) and "Hide {0} section" on each section header's own context menu (`SidebarViewModel.HideSection`, `SidebarViewModel.cs:967-999`).

Research findings that correct the overseer brief:

- **There is no Settings page for sidebar sections.** None of the `Show<X>Section` settings appear anywhere under `src/Files.App/Views/Settings` or `ViewModels/Settings` (grep confirmed). The audit's "Settings toggles" means the stored settings plus the sidebar right-click menu. Harald's principle ("noise is hidden by default or moved into Settings") argues for adding them to Settings, so this plan adds a small "Sidebar" group to the General settings page. This is new UI, flagged for the overseer's ruling (see Decisions).
- **Home is always added** (`SidebarViewModel.CreateSectionAsync`, `:566-570`, called once from the constructor via `CreateItemHomeAsync`, `:319`/`:348`). It has no setting, no "Hide section", and no entry in the right-click toggle list.
- **Nothing depends on the Home sidebar item existing.** The only consumer is selection sync, `UpdateSidebarSelectedItemFromArgs` (`:118`: `"Home" => sidebarItems.FirstOrDefault(x => x.Path == "Home")`), which already tolerates `null` the same way it does for a path whose section is hidden. The Home tab header/icon (`NavigationHelpers.cs:224,264-267`) uses `SidebarSectionIcons.For(SectionType.Home)`, not the item. `ModernShellPage`, `NavigationToolbarViewModel`, `LocationItem` and `StorageFileExtensions` compare *paths* to `"Home"`, never look up the sidebar item. `Files.App.Controls/Sidebar` has no Home or first-item assumptions. Home has no children, so tab-expansion tracking (`SidebarViewModel.TabExpansion.cs`) never touches it. Home stays reachable through the Home toolbar button, the Home tab and `navigate path=Home`.
- **Trap:** `UpdateSectionVisibilityAsync(SectionType.Home, true)` must not be used. Its show path calls `Manager_DataChanged(Home, Reset)`, whose `getElements` switch (`:388-398`) throws `ArgumentOutOfRangeException` for Home inside a dispatcher lambda. Home gets its own small sync method instead.
- **Cloud providers.** `CloudDrivesManager` (`Utils/Cloud/CloudDrivesManager.cs`) holds one `DriveItem` per detected provider (`Text` = provider name such as "Google Drive" or "OneDrive - Personal", `Path` = sync folder, `Type = DriveType.CloudDrive`), raising `DataChanged` Add events progressively. The sidebar copies them in through `Manager_DataChanged` -> `SyncSidebarItemsAsync` -> `AddElementToSectionAsync`. Filtering in the sidebar only (not in the manager, which other code uses to recognise cloud paths) is a clean, small addition, so per-provider hiding is in scope.

## Design

Fork logic goes in new files where possible; upstream files get small hunks.

### 1. Settings

`src/Files.App/Data/Contracts/IGeneralSettingsService.cs` — next to the section toggles (`:138-176`):

- `bool ShowHomeSection { get; set; }` ("Gets or sets a value indicating if the home item should be visible in the sidebar.").
- `List<string>? HiddenSidebarCloudDrives { get; set; }` ("Gets or sets the sync folder paths of the cloud drives hidden from the sidebar.").

`src/Files.App/Services/Settings/GeneralSettingsService.cs` — next to `ShowPinnedSection` (`:170`) and the other list settings (`:278-288`):

- `ShowHomeSection => Get(true)`: **default true**, stock behaviour; Harald decides.
- `HiddenSidebarCloudDrives => Get<List<string>>(null)`, same shape as `HiddenNewMenuEntries`. `List<string>` is already in `UserSettingsJsonSerializationContext` (`UserSettingsService.cs:23`), so no serializer change.

Keyed by sync-folder path rather than provider name so two accounts of one provider (two OneDrives) hide independently; compared case-insensitively.

### 2. Sidebar view model

New partial `src/Files.App/ViewModels/UserControls/SidebarViewModel.Declutter.cs` (same pattern as `SidebarViewModel.Bookmarks.cs`):

- `public bool ShowHomeSection` property, get/set through `GeneralSettingsService.ShowHomeSection` (copy of `ShowBookmarksSection`).
- `private void SyncHomeSection()`: when shown and `GetSection(SectionType.Home)` is null, call `CreateSectionAsync(SectionType.Home)`; when hidden, remove the Home section from `sidebarItems`. Either way finish with `UpdateSidebarSelectedItemFromArgs(CurrentPath)` so the selection follows (Home re-selected when the current page is Home; a removed Home item never stays selected).
- `private bool IsCloudDriveHidden(INavigationControlItem item)`: `item.Path` is in `HiddenSidebarCloudDrives` (OrdinalIgnoreCase).
- `private IReadOnlyList<INavigationControlItem> GetVisibleCloudDrives()`: `CloudDrivesManager.Drives` minus hidden.
- `private void SyncCloudDriveVisibility()`: if `ShowCloudDrivesSection` is off, nothing. If no visible cloud drive remains, remove the CloudDrives section (so no empty header lingers). Otherwise `Manager_DataChanged(SectionType.CloudDrives, Reset)`, whose Reset branch adds newly-unhidden drives and removes newly-hidden ones (it tears down their watchers, `:461-469`) while keeping the rest and their expansion state, and recreates the section when it was gone.
- `HideCloudDriveCommand` (`RelayCommand`, created lazily by its getter, so the constructor is untouched): appends `rightClickedItem.Path` to `HiddenSidebarCloudDrives` (new list instance, so the setter fires).

`src/Files.App/ViewModels/UserControls/SidebarViewModel.cs` hunks:

- `CreateSectionAsync` Home case (`:566-570`): `if (!ShowHomeSection) break;` and give Home `ContextMenuOptions { IsLocationItem = true, ShowHideSection = true }` so its header menu offers "Hide Home section" like every other section.
- `CreateSectionAsync` CloudDrives case (`:600`): `CloudDrivesManager.Drives.Any()` becomes `GetVisibleCloudDrives().Any()`.
- `Manager_DataChanged` `getElements` (`:391`): `SectionType.CloudDrives => GetVisibleCloudDrives()`.
- `AddElementToSectionAsync` DriveItem branch (`:495`): skip a cloud drive for which `IsCloudDriveHidden` is true. This is the one choke point for the progressive Add events, which carry the manager's items unfiltered.
- `UserSettingsService_OnSettingChangedEvent` (`:686-740`): add `ShowHomeSection` (`SyncHomeSection()`, `OnPropertyChanged(nameof(ShowHomeSection))`) and `HiddenSidebarCloudDrives` (`SyncCloudDriveVisibility()`).
- `HideSection` (`:972`): `case SectionType.Home: ShowHomeSection = false`.
- `GetLocationItemMenuItems` (`:1201`, next to the "Hide section" entry): new entry "Hide from sidebar" (glyph `\uE77A`, the hide glyph the section entry uses), `Command = HideCloudDriveCommand`, `ShowItem = item is DriveItem { Type: DriveType.CloudDrive }`.

`AreSectionsHidden` (`:200-209`) is deliberately unchanged: it already excludes Home, so the "add sections by right-clicking" hint keeps showing when only Home is left, exactly as in stock Files, and still shows when Home is hidden too. Its `!ShowCloudDrivesSection` term is not changed to "no visible cloud drive" because cloud detection is progressive and `AreSectionsHidden` isn't re-raised on cloud data changes; that would risk a stale hint on startup.

### 3. Sidebar right-click toggle list

`src/Files.App/Views/MainPage.xaml:58`: add `<ToggleMenuFlyoutItem IsChecked="{x:Bind SidebarAdaptiveViewModel.ShowHomeSection, Mode=TwoWay}" Text="{helpers:ResourceString Name=Home}" />` as the first entry, matching the section order.

### 4. Settings page

New `src/Files.App/ViewModels/Settings/SidebarDeclutterViewModel.cs` (pattern: `ContextMenuDeclutterViewModel.cs`), exposed from `GeneralPage.xaml.cs` as `public SidebarDeclutterViewModel SidebarDeclutter { get; } = new();`:

- One bool property per section (`ShowHomeSection`, `ShowPinnedSection`, `ShowBookmarksSection`, `ShowLibrarySection`, `ShowDrivesSection`, `ShowCloudDrivesSection`, `ShowNetworkSection`, `ShowWslSection`, `ShowFileTagsSection`), each reading/writing `GeneralSettingsService`.
- `ObservableCollection<SidebarCloudDriveEntry> CloudDrives`: one entry per `CloudDrivesManager.Drives` item at page creation (`Name` = drive text, `Path`, `IsShown` = not hidden). Toggling an entry saves `HiddenSidebarCloudDrives` = paths of unchecked entries **plus** any hidden paths not currently detected (so hiding survives a provider being offline when Settings opens).
- Settings is a tab, so the sidebar sits next to it and "Hide section" / the toggle list can change these while the page is open. The view model subscribes to `IUserSettingsService.OnSettingChangedEvent` and raises `PropertyChanged` for the matching section property, and refreshes `IsShown` on the entries (without re-saving) when `HiddenSidebarCloudDrives` changes. The subscription follows the page: `GeneralPage` calls `SidebarDeclutter.Attach()` on `Loaded` (which also catches up on changes made while detached) and `Detach()` on `Unloaded`.

`src/Files.App/Views/Settings/GeneralPage.xaml`: a "Sidebar" group after the Widgets expander (`:216`) and before "Dual Pane" (`:218`), in the same markup as the neighbouring groups:

- Group header `TextBlock` with the existing `Sidebar` string.
- `SettingsExpander` "Sidebar sections" (glyph `&#xE8A0;`), one `SettingsCard` + `ToggleSwitch` per section, headers from the existing section-name strings (`Home`, `Pinned`, `Bookmarks`, `SidebarLibraries`, `Drives`, `SidebarCloudDrives`, `Network`, `WSL`, `FileTags`).
- `SettingsExpander` "Cloud drives in the sidebar" with description "Uncheck a cloud drive to hide it from the sidebar.", `ItemsSource` = `SidebarDeclutter.CloudDrives`, item template a `SettingsCard` (header = name, description = path) with a `CheckBox`, as the New-menu-entries expander does (`:431-445`).

### 5. Strings

`src/Files.App/Strings/en-US/Resources.resw`, inserted next to related strings (`Sidebar` at `:1932`, `SideBarHideSectionFromSideBarText` at `:1788`):

- `SidebarSections` = "Sidebar sections"
- `SidebarCloudDriveVisibility` = "Cloud drives in the sidebar"
- `SidebarCloudDriveVisibilityDescription` = "Uncheck a cloud drive to hide it from the sidebar."
- `HideFromSidebar` = "Hide from sidebar"

`Strings.*` keys are generated from the resw by `Files.Core.SourceGenerator/Generators/StringsPropertyGenerator.cs`, so no hand edit.

### 6. Automation

`src/Files.App/Automation/AutomationHandlers.cs` `Sidebar()` (`:387-401`): add `["selected"] = ReferenceEquals(item, sidebar.SidebarSelectedItem)` to each node, so selection sync is checkable. Additive; existing consumers ignore the extra key. Update the method description to mention it.

## Verification

Mine: full `Files.slnx` build, Debug x64, `-clp:ErrorsOnly`, error-free and no new IL2xxx/IL3xxx warnings in the added code.

Overseer, on merged main with the app running hidden (`dev-deploy.ps1 -Hidden`, `ping` until ready):

```powershell
# Home hidden live, page still reachable, selection follows
python .claude\scripts\files_auto.py navigate path=Home
python .claude\scripts\files_auto.py sidebar          # first node: section "Home", selected true
python .claude\scripts\files_auto.py settings.set 'values={"ShowHomeSection":false}'
python .claude\scripts\files_auto.py sidebar          # no node with section "Home"; no node selected
python .claude\scripts\files_auto.py navigate path=C:\Windows
python .claude\scripts\files_auto.py navigate path=Home
python .claude\scripts\files_auto.py state            # page type Home despite no Home sidebar item
python .claude\scripts\files_auto.py settings.set 'values={"ShowHomeSection":true}'
python .claude\scripts\files_auto.py sidebar          # Home back as the FIRST node, selected true

# Per-provider hiding (needs at least one cloud provider on the machine; take a path from the CloudDrives section of `sidebar`)
python .claude\scripts\files_auto.py sidebar          # note CloudDrives children paths, e.g. P1 and P2
python .claude\scripts\files_auto.py settings.set 'values={"HiddenSidebarCloudDrives":["<P1>"]}'
python .claude\scripts\files_auto.py sidebar          # CloudDrives section lists P2 but not P1
python .claude\scripts\files_auto.py settings.set 'values={"HiddenSidebarCloudDrives":["<P1>","<P2>"]}'
python .claude\scripts\files_auto.py sidebar          # no CloudDrives section at all (when those were the only providers)
python .claude\scripts\files_auto.py settings.set 'values={"HiddenSidebarCloudDrives":[]}'
python .claude\scripts\files_auto.py sidebar          # CloudDrives section back with all providers
```

Plus a restart check: set `ShowHomeSection` false and a hidden provider, `quit`, relaunch, `sidebar` shows neither.

Manual UI checks (not pipe-drivable), with `window.show`:

1. Right-click the Home header: "Hide Home section" hides it. Right-click the sidebar's empty area: the toggle list now starts with "Home" and re-enables it.
2. Right-click a cloud drive: "Hide from sidebar" hides only that provider.
3. Settings > General > Sidebar: the section toggles and the cloud-drive checklist reflect and change the sidebar live, and stay in sync when the sidebar menus change them with the Settings tab open.
4. With Home hidden, Up/Down keyboard navigation in the sidebar and the Home tab header/icon behave normally.

## Decisions for the overseer

- **New Settings UI.** The brief assumed a Settings sidebar-options page existed; it doesn't. Plan adds a "Sidebar" group to the General settings page with all nine section toggles plus the cloud-drive checklist. Alternative: skip the Settings page and rely on the right-click menus only (much smaller: drops sections 4 and most of 5).
- `ShowHomeSection` defaults to true (stock).
- `AreSectionsHidden` left unchanged (reasoning in section 2).
- Cross-window: like every other user setting, a change made in one window's process reaches other open windows only after restart, and a later setting write from a stale window can overwrite it (the multi-process gotcha). Section toggles have always behaved this way; the hidden-provider list is preference data of the same kind, so it stays in user settings rather than its own mutex-guarded file.

## Shared resources touched

`SidebarViewModel.cs`, `MainPage.xaml` (SidebarContextMenu only), `GeneralPage.xaml` + `.xaml.cs` (new group between Widgets and Dual Pane; the terminal sibling may touch the context-menu group of the same page), `IGeneralSettingsService.cs` / `GeneralSettingsService.cs`, `Resources.resw`, `AutomationHandlers.cs` (`Sidebar()` only). No change to the serialization context.

## Out of scope

- Gallery: Files has no Gallery section.
- Hiding individual items in other sections (drives, network computers, WSL distros); drives can already be unpinned/hidden via existing options.
- Reordering sections.
- Filtering cloud drives anywhere other than the sidebar (Home widgets, `CloudDrivesManager` consumers).
