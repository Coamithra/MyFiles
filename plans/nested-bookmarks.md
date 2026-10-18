# Nested bookmarks under my control (card e17e095f)

Branch `feat/nested-bookmarks`, worktree `.claude/worktrees/nested-bookmarks`.

## Context

Harald wants real bookmarks he fully controls, organised into folders/levels, instead of the sidebar's "Pinned" section. Pinned is Windows Quick Access (`Services/Windows/WindowsQuickAccessService.cs`, `Data/Models/PinnedFoldersManager.cs`): flat, shared with Explorer, no custom names, no app-owned storage. This card adds a new app-owned **Bookmarks** sidebar section holding a tree: groups nested to any depth, and folder bookmarks with an optional custom name. Quick Access / Pinned is left untouched (it can already be hidden).

How the sidebar works today (verified on main @ 732df4fce):

- `SidebarViewModel` (`src/Files.App/ViewModels/UserControls/SidebarViewModel.cs`) keeps `sidebarItems`: one `LocationItem` per section (`IsHeader = true`, `ChildItems = []`), created by `CreateSectionAsync` (:550) and placed by `SectionOrder` (:78). Sections are filled by `Manager_DataChanged` → `SyncSidebarItemsAsync` (:367-464), which matches children **by Path** and appends; that is unsuitable for bookmarks (groups have no path, order is user-defined, duplicates possible), so bookmarks get their own sync.
- The control renders a **flat** list (`SidebarViewModel.FlatTree.cs`): any `ISidebarItemModel` whose `Children` is a non-null collection is a container; the flat tree listens to every child collection's `CollectionChanged` and to `IsExpanded`. A container that is not `IsLeafWithChildren` renders as a group header (semibold text, chevron; click toggles expansion and never navigates — `SidebarView.RaiseItemInvoked` returns early, `SidebarItem.Clicked` toggles).
- Expansion is per tab (`SidebarViewModel.TabExpansion.cs`): `GetExpansionKey` is `section:<Type>` for headers and the Path otherwise; `ApplyStateToItem` (:301) **collapses every item whose key is null** on each tab switch and on each newly added item. Section expansion is mirrored to `IGeneralSettingsService.Is*SectionExpanded`.
- Drag and drop (`Files.App.Controls/Sidebar/SidebarItem.cs`): a row can be dragged only when `Item.Path` is rooted (:138); `DragStarting` (:257) puts the path as Text plus a StorageItems provider. `DetermineDropTargetPosition` (:628) returns Top/Center/Bottom only when the unused `UseReorderDrop` DP is true, otherwise always Center. The insert-line visual states (`DragInsertAbove/Below`) already exist in `SidebarStyles.xaml`. `MainPage.xaml.cs` (:583, :597) takes a deferral and awaits `SidebarViewModel.HandleItemDragOverAsync` / `HandleItemDroppedAsync`.
- Item context menus are built in `SidebarViewModel.GetLocationItemMenuItems` (:1036) from `ContextMenuFlyoutItemViewModelBuilder(Commands.X)` entries for the `*FromSidebar` actions, which read `ISidebarContext.RightClickedItem`.
- Section show/hide: `IGeneralSettingsService.Show*Section`, mirrored by `SidebarViewModel.Show*Section` properties, toggled from `MainPage.xaml`'s `SidebarContextMenu` (:58-70) and from a section header's "Hide section" (`HideSection`, :945). There is no Settings-page UI for sections.

## Decisions (please confirm or overrule)

1. **Section**: new `SectionType.Bookmarks`, placed right after Pinned in `SectionOrder`. Header text "Bookmarks", expanded by default. Shown even when empty, because the empty header is the drop target for adding the first bookmark.
2. **Show/hide**: `ShowBookmarksSection` setting, **default true**, following the existing `Show*Section` pattern: toggle in the sidebar's pane context menu, "Hide section" on the header. Default on because this is the feature Harald asked for; it costs one header row when empty.
3. **Groups** render as nested sub-headings: semibold, chevron, no icon, click toggles. They are never navigation targets. Group expansion is **global and persisted** in the bookmark tree (not per tab like folder rows), because a bookmark tree is a curated layout, not a browsing state. New groups start expanded.
4. **Bookmarks** are leaves: they open the folder on click; Ctrl+click / middle-click opens a new tab; the normal location context menu (open in new tab/window/pane, copy, properties, shell items) applies. They do **not** expand into the filesystem subfolder tree (Pinned doesn't either), so groups are the only containers. Display text is the custom name, or else the shell display name of the folder.
5. **Missing folders** are never removed automatically. A bookmark whose folder can't be resolved when its sidebar item is built is shown dimmed (`Constants.UI.DimItemOpacity`, as hidden folders are) with the generic folder icon; clicking it gets Files' normal "folder not found" handling, and the user removes it from its context menu. The check runs when the item is built (app start, or when the bookmark is added), so a drive that comes back later shows undimmed only after a restart. Accepted as a v1 limitation.
6. **Duplicates**: the model allows the same folder in several places (drag-in is an explicit placement). The Add to Bookmarks command only adds folders not yet bookmarked anywhere, so repeated use never duplicates.
7. **What can be bookmarked**: rooted filesystem paths (local, UNC, libraries) that are folders; not Recycle Bin, archives, FTP or Home.
8. **Entry points** (all `[GeneratedRichCommand]` actions, so they also appear in the Actions settings page where hotkeys can be assigned):
   - `AddBookmark` "Add to Bookmarks": the selected folders, or the current folder when nothing is selected; appended at the root. In the file list's item and background context menus (next to Pin to sidebar) and the command palette. **Proposed default hotkey Ctrl+Shift+D** (Ctrl+D is Delete, Alt+D edit path, Ctrl+Shift+D is free). Say if you'd rather have none.
   - `AddBookmarkFromSidebar`: on any non-bookmark sidebar row with a rooted folder path (Pinned items, drives and their tree folders, libraries) — adds it at the root.
   - `NewBookmarkGroup`: on the Bookmarks header (creates at root) and on a group (creates inside it). Name dialog.
   - `RenameBookmark`: on a bookmark or group. Name dialog prefilled; for a bookmark, an empty name resets it to the folder's name.
   - `RemoveBookmark` "Remove from Bookmarks": on a bookmark (immediate) or group (confirmation dialog when the group is non-empty; removes the group and everything in it; never touches the folders on disk).
   - Move is by drag and drop only (no Move up/down menu entries in v1).
9. **Drag and drop** (bookmark rows and groups split into Top 20% / Center 60% / Bottom 20%, the section header is always Center):
   - Dragging a bookmark or group inside the section moves it: Top = before the row, Bottom = after the row (on an expanded non-empty group, Bottom = first child, matching where the insert line is drawn), Center on a group or the header = append inside it, Center on a bookmark row = not accepted (it would otherwise mean a filesystem move into that folder). A group can't be dropped into itself or its descendants.
   - Dropping folders from the file list (or from other sidebar sections) onto the header or a group's Center adds them there; onto the Top/Bottom of any bookmark row inserts them at that position. Files in the drag are ignored; a drag with no folders is not accepted. Caption "Add to Bookmarks".
   - Dropping files/folders onto the **Center of a bookmark row** keeps the existing behaviour of every location row: copy/move into that folder.
   - Dragging a bookmark row *out* of the section behaves like dragging any sidebar folder row today (it carries the folder as StorageItems), e.g. dropping it on a drive copies the folder. Groups carry nothing outside the section.
10. **Storage** (revised at plan review): the tree lives in its own file, `LocalState\settings\bookmarks.json`, not in `user_settings.json`. Each Files window is its own process, and `CachingJsonSettingsDatabase` caches the whole settings file per process and rewrites all of it on any `SetValue` (`LastSessionTabList` on every navigation), so a bookmark added in one window would be wiped by the next navigation in another. The file holds the tree including each group's `IsExpanded`. Every mutation runs under a named mutex scoped to the package: re-read the file, apply the change, write a temp file and move it over the original. A `FileSystemWatcher` (debounced, marshalled to the UI thread) reloads the tree when another window writes it, so every window's sidebar follows. A missing file is an empty tree; a corrupt one is an empty tree plus a logged warning, and the next write first copies it to `bookmarks.json.bak`. `ShowBookmarksSection` (default true) and `IsBookmarksSectionExpanded` (default true) stay ordinary user settings: section expansion is persisted only through the explicit `Is*SectionExpanded` switch in `SidebarViewModel.TabExpansion.cs`, so without that setting the section would start collapsed on every launch.
11. **Section icon** (revised at plan review): no generated art. The section header uses the existing `App.ThemedIcons.Favorite` star (a `ThemedIcon`, layered, 16 px, built in code the way `PropertiesNavigationItemsFactory` builds its sidebar icons), through a small `BookmarksSectionItem : LocationItem` that re-implements `ISidebarItemPresentationModel.IconElement`. The commands use `App.ThemedIcons.FavoriteAdd` / `Favorite` glyphs.

Constraints that shaped this: keep upstream diffs small (new files for everything that can be new; upstream files get small, localized hooks); no reflection (STJ source generation, with the bookmarks file's own `JsonSerializerContext`); several Files processes may edit the same bookmarks at once; everything on the UI thread (all mutations come from commands, drag handlers and the automation pipe, which all run there); Quick Access must not change.

## Design, file by file

### New files

- `src/Files.App.Controls/Sidebar/ISidebarReorderableItem.cs` — `public interface ISidebarReorderableItem { string ReorderId { get; } }` ("this row can be rearranged by drag and drop inside the sidebar") and `public static class SidebarReorderData { public const string Format = "Files.App.SidebarReorderId"; }`. A separate interface rather than a default member on `ISidebarItemModel`, because DIMs don't re-map through subclasses of `LocationItem`.
- `src/Files.App/Data/Models/BookmarkNode.cs` — persisted node: `Id` (Guid "N"), `Name` (string?; group name, or a bookmark's custom name), `Path` (string?; null for a group), `IsExpanded` (groups), `Children` (`List<BookmarkNode>?`; non-null for groups). `[JsonIgnore] IsGroup => Path is null`. Plain get/set properties for STJ source generation.
- `src/Files.App/Data/Contracts/IBookmarksService.cs` + `src/Files.App/Services/App/BookmarksService.cs` — owns the in-memory tree, backed by `bookmarks.json` (Decision 10), with `BookmarksJsonSerializationContext` (`List<BookmarkNode>`) in the same file. API: `event EventHandler? BookmarksChanged`, `IReadOnlyList<BookmarkNode> Roots`, `BookmarkNode? Find(string id)`, `BookmarkNode? FindParent(string id)`, `bool IsBookmarked(string path)` (OrdinalIgnoreCase, trailing separator trimmed), `IReadOnlyList<string> AddBookmarks(IEnumerable<string> paths, string? parentId = null, int index = -1)`, `string AddGroup(string name, string? parentId = null, int index = -1)`, `bool Rename(string id, string? name)`, `bool Remove(string id)`, `bool Move(string id, string? parentId, int index)` (index is in the target list before removal; same-parent moves are adjusted; refuses a group into its own subtree), `void SetExpanded(string id, bool expanded)` (saves, no change event). Every mutation goes through one `Mutate(Func<List<BookmarkNode>, bool>)`: take the mutex `Local\<package family name>.Bookmarks` (2 s timeout; on timeout log and give up), read the file, apply, write `bookmarks.json.tmp` and `File.Move(..., overwrite: true)`, release, then swap the in-memory tree and raise `BookmarksChanged`. Applying to the freshly read tree means ids that another window just removed simply make the change a no-op. The watcher's reload compares the file's text with the last text this process read or wrote and ignores its own writes. Registered as a singleton next to `IQuickAccessService` in `AppLifecycleHelper` (:364).
- `src/Files.App/Data/Items/BookmarkSidebarItems.cs` (also holds `BookmarksSectionItem`, the header with the star icon):
  - `IBookmarkSidebarItem { string BookmarkId { get; } }`.
  - `BookmarkLocationItem : LocationItem, ISidebarReorderableItem, IBookmarkSidebarItem` — leaf. `Section = Bookmarks`, `ChildItems = null`, `MenuOptions = { IsLocationItem, ShowProperties, ShowShellItems }`, `IsMissing`, `SetDisplayText(string)` (sets `Text` and raises `PropertyChanged(Text)`, because `LocationItem.Text` doesn't notify). `ReorderId => BookmarkId`.
  - `BookmarkGroupItem : LocationItem, ISidebarReorderableItem, IBookmarkSidebarItem` — container: `Path = null`, `ChildItems = []`, `MenuOptions = new()` (no location entries), no icon. In its constructor it subscribes to its own `PropertyChanged` and forwards `IsExpanded` to `IBookmarksService.SetExpanded`.
- `src/Files.App/ViewModels/UserControls/SidebarViewModel.Bookmarks.cs` (new partial):
  - `ShowBookmarksSection` property (same shape as `ShowPinnedFoldersSection`).
  - `InitializeBookmarks()` / `DisposeBookmarks()` — subscribe/unsubscribe `BookmarksChanged` (marshalled to the dispatcher) and run the first sync.
  - `SyncBookmarksSectionAsync()` — re-entrancy safe (a running flag plus a "run again" flag, loop until clean). If the section is hidden, remove it and drop the item cache. Otherwise get or create the section (`GetOrCreateSectionAsync(SectionType.Bookmarks)`; on first creation `TrackSidebarItem(section)` + `ApplyTabStateToNewlyAddedItem(section)` as `AddElementToSectionAsync` does for other sections). Items are cached by bookmark id and reused, so icons and expansion survive edits; new bookmark items are built in parallel (`Task.WhenAll`) before any collection changes: display name and validity resolved as `PinnedFoldersManager.CreateLocationItemFromPathAsync` does (`DriveHelpers.GetRootFromPathAsync` + `StorageFileExtensions.DangerousGetFolderFromPathAsync`), icon via `FileThumbnailHelper.GetIconAsync(..., ReturnIconOnly | SkipSizeSnapping)` fire-and-forget, the generic folder icon (`UIHelpers.GetSidebarIconResource(Constants.ImageRes.Folder)`) when missing. Renames update cached items via `SetDisplayText`. Then a two-phase reconcile of the real collections against the tree: phase 1 walks every container and `RemoveAt`s each child that no longer belongs to that container; phase 2 walks top-down and makes each container's order match with `Insert` (and `RemoveAt` + `Insert` for reorders; `BulkConcurrentObservableCollection` has no Move). Removals-before-inserts matters: the flat tree skips inserting an item it still has registered, so a cross-group move must leave its old parent first. This relies on the flat tree's collection handlers running synchronously on the UI thread (`EnqueueOrInvokeAsync` invokes inline when it has thread access, and inserts under 100 rows complete synchronously). Fine-grained Remove/Add events (not Reset) also keep `TabExpansion`'s tracking sets clean.
  - The `ShowBookmarksSection` setting change also just runs `SyncBookmarksSectionAsync()`, which removes the section when hidden and rebuilds it when shown (`UpdateSectionVisibilityAsync` can't be used: its `Manager_DataChanged` getter throws for unknown sections).
  - Drag and drop: `Task<bool> TryHandleBookmarkDragOverAsync(ItemDragOverEventArgs)` and `Task<bool> TryHandleBookmarkDropAsync(ItemDroppedEventArgs)`, returning false when the target/position isn't theirs so the existing logic runs (Center on a bookmark row with a non-bookmark drag). Internal drags are recognised by `DataView.Contains(SidebarReorderData.Format)`; the id is read with `GetDataAsync`. The placement rules in Decision 9 live in one pure method `ResolveBookmarkDrop(INavigationControlItem target, SidebarItemDropPosition position, string? draggedId) → (bool accepted, string? parentId, int index)`, shared by drag-over, drop and the automation `bookmarks.move`.
- Actions in `src/Files.App/Actions/Sidebar/Bookmarks/`: `AddBookmarkAction.cs` (content page context: `IContentPageContext`, same rules as `PinFolderToSidebarAction` plus rooted path/not archive/not FTP, executable only if something new would be added; re-evaluates on `Folder`/`SelectedItems` changes and on `BookmarksChanged`; hotkey Ctrl+Shift+D), `AddBookmarkFromSidebarAction.cs`, `NewBookmarkGroupAction.cs`, `RenameBookmarkAction.cs`, `RemoveBookmarkAction.cs` (sidebar context: `ISidebarContext.RightClickedItem`, `IsAccessibleGlobally => false` like the other `*FromSidebar` actions).
- `src/Files.App/Helpers/Dialog/BookmarkDialogHelper.cs` — `Task<string?> ShowNameDialogAsync(title, initialText, placeholder, primaryText)` and `Task<bool> ShowRemoveGroupConfirmationAsync(name)`, built like `LibraryManager.ShowCreateNewLibraryDialogAsync` (`DynamicDialog` + `TextBox`, Enter submits, Escape cancels).
- `src/Files.App/Automation/AutomationHandlers.Bookmarks.cs` — the `bookmarks.*` handlers (see Verification).

### Edits to existing files

- `src/Files.App.Controls/Sidebar/SidebarItem.cs` (upstream control; 3 small hunks):
  - :138 `CanDrag = (rooted path) || Item is ISidebarReorderableItem;`
  - :257 `SidebarItem_DragStarting`: if `Item is ISidebarReorderableItem r`, `args.Data.SetData(SidebarReorderData.Format, r.ReorderId)` and include Move in `RequestedOperation`; the existing rooted-path branch stays as is (so groups, which have no path, carry only the reorder id).
  - :630 `if (UseReorderDrop || Item is ISidebarReorderableItem)`.
- `src/Files.App/Constants.cs` `LocalSettings` — `BookmarksFileName = "bookmarks.json"`.
- `src/Files.App/Data/Contracts/INavigationControlItem.cs` :44 — append `Bookmarks` to `SectionType` (appended, so existing values keep their numbers).
- `src/Files.App/Data/Contracts/IGeneralSettingsService.cs` + `src/Files.App/Services/Settings/GeneralSettingsService.cs` — `ShowBookmarksSection` (default true) next to `ShowFileTagsSection`, `IsBookmarksSectionExpanded` (default true) next to `IsFileTagsSectionExpanded`.
- `src/Files.App/Helpers/Application/AppLifecycleHelper.cs` :364 — `.AddSingleton<IBookmarksService, BookmarksService>()`.
- `src/Files.App/ViewModels/UserControls/SidebarViewModel.cs` (shared with the later "Sidebar declutter" card; every hunk is a line or a case):
  - :81 `SectionOrder`: `SectionType.Bookmarks` after Pinned.
  - :193 `AreSectionsHidden`: `!ShowBookmarksSection &&`.
  - ctor (:318/:326): `InitializeBookmarks();`.
  - `CreateSectionAsync` (:562 area): `case SectionType.Bookmarks` building the header with `ContextMenuOptions { ShowHideSection = true }`, guarded by `ShowBookmarksSection`.
  - `UserSettingsService_OnSettingChangedEvent` (:710 area): `case ShowBookmarksSection` → `SyncBookmarksSectionAsync()` + the two `OnPropertyChanged` calls.
  - `Dispose` (:756): `DisposeBookmarks();`.
  - `HideSection` (:970): `case SectionType.Bookmarks`.
  - `FindDeepestVisibleAncestorRecursive` (:147): descend through path-less expanded containers (bookmark groups), otherwise bookmarks inside groups never get the selection highlight.
  - `GetLocationItemMenuItems` (after Unpin, :1152): four `ContextMenuFlyoutItemViewModelBuilder(Commands.AddBookmarkFromSidebar / NewBookmarkGroup / RenameBookmark / RemoveBookmark)` entries with `IsVisible = <command>.IsExecutable` (AddBookmarkFromSidebar also requires `ShowBookmarksSection`).
  - `HandleItemDragOverAsync` (:1215) / `HandleItemDroppedAsync` (:1388): first line `if (await TryHandleBookmark…Async(args)) return;`.
- `src/Files.App/ViewModels/UserControls/SidebarViewModel.TabExpansion.cs` — `GetPersistedExpandedSectionKeys` and `PersistSectionExpansion` gain the Bookmarks section; `ApplyStateToItem` (:301) returns early for `BookmarkGroupItem` so tab switches don't collapse groups (they have no expansion key and would otherwise be collapsed every time).
- `src/Files.App/ViewModels/UserControls/SidebarViewModel.FlatTree.cs` :50 — dim `BookmarkLocationItem { IsMissing: true }` like hidden folders.
- `src/Files.App/Views/MainPage.xaml` :58-70 — `ToggleMenuFlyoutItem` for `ShowBookmarksSection` after Pinned.
- `src/Files.App/Data/Factories/ContentPageContextFlyoutFactory.cs` :548 — `Commands.AddBookmark` entry right after `PinFolderToSidebar`, visible when executable and `ShowBookmarksSection`. (Siblings "Context-menu entry blocklist" / "Hide context-menu icon row" may touch this file too; my hunk is 4 lines.)
- `src/Files.App/Automation/AutomationHandlers.cs` — `static partial class`; 6 `Methods` entries and 6 switch cases routing to the new partial file.
- `src/Files.App/Strings/en-US/Resources.resw` — new keys inserted next to `Pinned` / `PinFolderToSidebar`: `Bookmarks`, `AddBookmark` ("Add to Bookmarks"), `AddBookmarkDescription`, `AddBookmarkFromSidebarDescription`, `NewBookmarkGroup` ("New group"), `NewBookmarkGroupDescription`, `BookmarkGroupNamePlaceholder` ("Group name"), `RenameBookmarkDescription`, `RenameBookmarkDialogTitle` ("Rename bookmark"), `RemoveBookmark` ("Remove from Bookmarks"), `RemoveBookmarkDescription`, `RemoveBookmarkGroupDialogTitle` ("Remove group?"), `RemoveBookmarkGroupDialogText` ("\"{0}\" and everything in it will be removed from Bookmarks. The folders themselves aren't affected."). Existing `Rename`, `Create`, `Cancel`, `Remove`, `MoveToFolderCaptionText` are reused.
- `.claude/CLAUDE.md` — add the `bookmarks.*` methods to the "Driving the app" method list.

Shared resources touched (for sequencing): `SidebarViewModel.cs`, `SidebarViewModel.TabExpansion.cs`, `GeneralSettingsService` + interface, `Resources.resw`, `AutomationHandlers.cs`, `ContentPageContextFlyoutFactory.cs`, `MainPage.xaml`, and upstream `Files.App.Controls/Sidebar/SidebarItem.cs`.

## Verification

Mine: the full Debug x64 build of `Files.slnx` is clean with `-clp:ErrorsOnly`, and no new IL2xxx/IL3xxx warnings in the added code (checked with a warnings-enabled build of `Files.App.csproj` filtered to the new files).

For the overseer on merged main (headless, `dev-deploy.ps1 -Hidden`, `ping` until ready). New automation methods: `bookmarks` (the tree with ids), `bookmarks.add` (path, name?, parent?, index?), `bookmarks.addGroup` (name, parent?, index?), `bookmarks.rename` (id, name), `bookmarks.move` (id, target?, position = before|after|into — runs the same `ResolveBookmarkDrop` the drag handlers use), `bookmarks.remove` (id). Each returns the tree.

```powershell
python .claude\scripts\files_auto.py sidebar                     # a "Bookmarks" section after Pinned, empty
python .claude\scripts\files_auto.py navigate path=C:\Windows
python .claude\scripts\files_auto.py commands filter=Bookmark    # AddBookmark executable, hotKeys Ctrl+Shift+D
python .claude\scripts\files_auto.py command code=AddBookmark    # adds C:\Windows at the root
python .claude\scripts\files_auto.py commands filter=AddBookmark # now not executable (already bookmarked)
python .claude\scripts\files_auto.py select 'names=["System32","Fonts"]'
python .claude\scripts\files_auto.py command code=AddBookmark    # adds both
python .claude\scripts\files_auto.py contextMenu 'names=["Web"]' shell=false   # "Add to Bookmarks" present
python .claude\scripts\files_auto.py bookmarks.addGroup name=Work            # -> id G1
python .claude\scripts\files_auto.py bookmarks.addGroup name=Deep parent=G1  # nested -> id G2
python .claude\scripts\files_auto.py bookmarks.move id=<System32> target=G2 position=into
python .claude\scripts\files_auto.py bookmarks.move id=<Fonts> target=<Windows> position=before
python .claude\scripts\files_auto.py bookmarks.move id=G1 target=G2 position=into   # error: into own subtree
python .claude\scripts\files_auto.py bookmarks.rename id=<System32> name=Sys
python .claude\scripts\files_auto.py sidebar      # Bookmarks: Fonts, Windows, Work > Deep > Sys
python .claude\scripts\files_auto.py bookmarks.add path=C:\DoesNotExist   # kept, shown dimmed
type %LOCALAPPDATA%\Packages\FilesDev_ykqwq8d6ps0ag\LocalState\settings\bookmarks.json   # persisted tree
python .claude\scripts\files_auto.py settings.set 'values={"ShowBookmarksSection":false}'; python .claude\scripts\files_auto.py sidebar   # section gone
python .claude\scripts\files_auto.py settings.set 'values={"ShowBookmarksSection":true}';  python .claude\scripts\files_auto.py sidebar   # back, same tree
python .claude\scripts\files_auto.py bookmarks.remove id=G1; python .claude\scripts\files_auto.py sidebar   # Work and its contents gone
python .claude\scripts\files_auto.py quit   # then relaunch -Hidden: sidebar shows the same tree (persistence across restart)
python .claude\scripts\files_auto.py tabs.open path=C:\  ;  python .claude\scripts\files_auto.py tabs.select index=0   # groups keep their expansion across tab switches
```

Visual, not automatable (needs `window.show` and a person or computer-use): group row styling and indentation, the section icon in light/dark/high contrast, the context-menu entries on the header/group/bookmark rows, the name dialogs, and real drag and drop (reorder, into group, out of group to the root via the header, folder from the file list onto a group and between rows, Center on a bookmark still moves files into the folder).

## Out of scope

- Importing from or syncing with Quick Access, browsers or Explorer favourites.
- Bookmarking files (folders only), changing a bookmark's target path, keyboard move up/down, multi-select drag.
- Bookmarks on the Home page or in the Open/Save dialogs.
- Live re-checking of missing folders (Decision 5).
- Expanding a bookmark into its filesystem subfolders (Decision 4) — easy follow-up if wanted.
