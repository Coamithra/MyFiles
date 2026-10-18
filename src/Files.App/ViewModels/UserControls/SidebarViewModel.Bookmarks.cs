// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Files.App.ViewModels.UserControls
{
	public sealed partial class SidebarViewModel
	{
		private readonly IBookmarksService bookmarksService = Ioc.Default.GetRequiredService<IBookmarksService>();

		// Rows are kept per bookmark id and reused, so icons and expansion survive edits.
		private readonly Dictionary<string, BookmarkLocationItem> bookmarkRows = [];
		private readonly Dictionary<string, BookmarkGroupItem> bookmarkGroupRows = [];
		private bool isSyncingBookmarks;
		private bool isBookmarksSyncPending;

		public bool ShowBookmarksSection
		{
			get => UserSettingsService.GeneralSettingsService.ShowBookmarksSection;
			set
			{
				if (value == UserSettingsService.GeneralSettingsService.ShowBookmarksSection)
					return;

				UserSettingsService.GeneralSettingsService.ShowBookmarksSection = value;
			}
		}

		private void InitializeBookmarks()
		{
			bookmarksService.BookmarksChanged += BookmarksService_BookmarksChanged;
			_ = SyncBookmarksSectionAsync();
		}

		private void DisposeBookmarks()
		{
			bookmarksService.BookmarksChanged -= BookmarksService_BookmarksChanged;
		}

		private void BookmarksService_BookmarksChanged(object? sender, EventArgs e)
		{
			_ = dispatcherQueue.EnqueueOrInvokeAsync(SyncBookmarksSectionAsync);
		}

		private static LocationItem BuildBookmarksSection()
		{
			return new BookmarksSectionItem()
			{
				Text = Strings.Bookmarks.GetLocalizedResource(),
				Section = SectionType.Bookmarks,
				MenuOptions = new ContextMenuOptions { ShowHideSection = true },
				SelectsOnInvoked = false,
				ChildItems = [],
				IsHeader = true,
			};
		}

		// A change arriving while a sync awaits the shell runs once more afterwards instead of interleaving.
		private async Task SyncBookmarksSectionAsync()
		{
			if (isSyncingBookmarks)
			{
				isBookmarksSyncPending = true;
				return;
			}

			isSyncingBookmarks = true;
			try
			{
				do
				{
					isBookmarksSyncPending = false;
					await SyncBookmarksSectionOnceAsync();
				}
				while (isBookmarksSyncPending);
			}
			finally
			{
				isSyncingBookmarks = false;
			}
		}

		private async Task SyncBookmarksSectionOnceAsync()
		{
			if (!ShowBookmarksSection)
			{
				if (GetSection(SectionType.Bookmarks) is { } hidden)
					sidebarItems.Remove(hidden);
				bookmarkRows.Clear();
				bookmarkGroupRows.Clear();
				return;
			}

			var roots = bookmarksService.Roots;

			// Resolve new bookmarks' folders before touching the visible tree.
			var newBookmarks = EnumerateBookmarkNodes(roots).Where(node => !node.IsGroup && !bookmarkRows.ContainsKey(node.Id)).ToList();
			if (newBookmarks.Count > 0)
			{
				foreach (var row in await Task.WhenAll(newBookmarks.Select(BookmarkLocationItem.CreateAsync)))
					bookmarkRows[row.BookmarkId] = row;
			}

			if (!ShowBookmarksSection)
				return;

			var isNewSection = GetSection(SectionType.Bookmarks) is null;
			if (await GetOrCreateSectionAsync(SectionType.Bookmarks) is not { } section)
				return;

			var targets = new Dictionary<LocationItem, List<INavigationControlItem>>(ReferenceEqualityComparer.Instance);
			var liveIds = new HashSet<string>();
			targets[section] = BuildTargets(roots);

			// Take every row out of the container it no longer belongs to before inserting anywhere: the flat tree skips inserting a row it still has registered, so a row moving between groups must leave its old group first.
			RemoveMisplaced(section);
			ApplyOrder(section);

			foreach (var id in bookmarkRows.Keys.Where(id => !liveIds.Contains(id)).ToList())
				bookmarkRows.Remove(id);
			foreach (var id in bookmarkGroupRows.Keys.Where(id => !liveIds.Contains(id)).ToList())
				bookmarkGroupRows.Remove(id);

			if (isNewSection)
			{
				TrackSidebarItem(section);
				ApplyTabStateToNewlyAddedItem(section);
			}

			List<INavigationControlItem> BuildTargets(IReadOnlyList<BookmarkNode> nodes)
			{
				var result = new List<INavigationControlItem>(nodes.Count);
				foreach (var node in nodes)
				{
					if (node.IsGroup)
					{
						if (!bookmarkGroupRows.TryGetValue(node.Id, out var group))
							bookmarkGroupRows[node.Id] = group = new BookmarkGroupItem(node.Id);

						group.SetName(node.Name ?? string.Empty);
						group.ApplyStoredExpansion(node.IsExpanded);
						targets[group] = BuildTargets(node.Children ?? []);
						liveIds.Add(node.Id);
						result.Add(group);
					}
					else if (bookmarkRows.TryGetValue(node.Id, out var row))
					{
						row.SetDisplayText(node.Name);
						liveIds.Add(node.Id);
						result.Add(row);
					}
				}

				return result;
			}

			void RemoveMisplaced(LocationItem container)
			{
				var children = container.ChildItems!;
				targets.TryGetValue(container, out var wanted);
				for (var i = children.Count - 1; i >= 0; i--)
				{
					var child = children[i];
					if (child is BookmarkGroupItem group)
						RemoveMisplaced(group);
					if (wanted is null || !wanted.Contains(child))
						children.RemoveAt(i);
				}
			}

			void ApplyOrder(LocationItem container)
			{
				var children = container.ChildItems!;
				var wanted = targets[container];
				for (var i = 0; i < wanted.Count; i++)
				{
					if (i < children.Count && ReferenceEquals(children[i], wanted[i]))
						continue;

					var current = children.IndexOf(wanted[i]);
					if (current >= 0)
						children.RemoveAt(current);
					children.Insert(Math.Min(i, children.Count), wanted[i]);
				}

				while (children.Count > wanted.Count)
					children.RemoveAt(children.Count - 1);

				foreach (var group in wanted.OfType<BookmarkGroupItem>())
					ApplyOrder(group);
			}
		}

		private static IEnumerable<BookmarkNode> EnumerateBookmarkNodes(IEnumerable<BookmarkNode> nodes)
		{
			foreach (var node in nodes)
			{
				yield return node;
				if (node.Children is { } children)
				{
					foreach (var child in EnumerateBookmarkNodes(children))
						yield return child;
				}
			}
		}

		#region Drag and drop

		/// <summary>
		/// Where a drop on the Bookmarks section lands: the group (null for the top level) and the index in it. Null when the drop isn't accepted, which for a non-bookmark drag onto the middle of a bookmark row means the folder itself takes the drop.
		/// </summary>
		/// <param name="targetId">The bookmark or group under the pointer; null for the section header.</param>
		/// <param name="draggedId">The bookmark or group being moved; null when folders are dragged in.</param>
		internal (string? ParentId, int Index)? ResolveBookmarkDrop(string? targetId, SidebarItemDropPosition position, string? draggedId)
		{
			(string? ParentId, int Index)? drop;
			if (targetId is null)
			{
				drop = (null, -1);
			}
			else
			{
				if (bookmarksService.Find(targetId) is not { } target || targetId == draggedId)
					return null;

				var parentId = bookmarksService.FindParent(targetId)?.Id;
				var siblings = parentId is null ? bookmarksService.Roots : bookmarksService.Find(parentId)?.Children ?? [];
				var index = IndexOf(siblings, target);
				drop = position switch
				{
					SidebarItemDropPosition.Top => (parentId, index),
					// The insert line under an open group sits above its first child.
					SidebarItemDropPosition.Bottom when target.IsGroup && target.IsExpanded && target.Children is { Count: > 0 } => (target.Id, 0),
					SidebarItemDropPosition.Bottom => (parentId, index + 1),
					_ when target.IsGroup => (target.Id, -1),
					_ => null,
				};
			}

			// A group can't go into itself or its own subtree.
			if (drop is { } resolved && draggedId is not null)
			{
				for (var ancestor = resolved.ParentId; ancestor is not null; ancestor = bookmarksService.FindParent(ancestor)?.Id)
				{
					if (ancestor == draggedId)
						return null;
				}
			}

			return drop;

			static int IndexOf(IReadOnlyList<BookmarkNode> list, BookmarkNode node)
			{
				for (var i = 0; i < list.Count; i++)
				{
					if (ReferenceEquals(list[i], node))
						return i;
				}

				return -1;
			}
		}

		private static bool TryGetBookmarkDropTarget(object dropTarget, out string? targetId)
		{
			switch (dropTarget)
			{
				case BookmarksSectionItem:
					targetId = null;
					return true;
				case IBookmarkSidebarItem bookmark:
					targetId = bookmark.BookmarkId;
					return true;
				default:
					targetId = null;
					return false;
			}
		}

		private static async Task<string?> GetDraggedBookmarkIdAsync(DataPackageView data)
		{
			if (!data.Contains(SidebarReorderData.Format))
				return null;

			try
			{
				return await data.GetDataAsync(SidebarReorderData.Format) as string;
			}
			// The drag source can go away mid-drag.
			catch (Exception)
			{
				return null;
			}
		}

		private async Task<List<string>> GetDraggedBookmarkableFoldersAsync(DataPackageView data)
		{
			if (!Utils.Storage.FilesystemHelpers.HasDraggedStorageItems(data))
				return [];

			var items = await Utils.Storage.FilesystemHelpers.GetDraggedStorageItems(data);
			return items
				.Where(item => item.ItemType == FilesystemItemType.Directory && BookmarksService.CanBookmark(item.Path))
				.Select(item => item.Path)
				.ToList();
		}

		private string? GetBookmarkDropTargetName(string? parentId)
			=> parentId is null ? Strings.Bookmarks.GetLocalizedResource() : bookmarksService.Find(parentId)?.Name;

		private async Task<bool> TryHandleBookmarkDragOverAsync(ItemDragOverEventArgs args)
		{
			if (!TryGetBookmarkDropTarget(args.DropTarget, out var targetId))
				return false;

			var rawEvent = args.RawEvent;
			var draggedId = await GetDraggedBookmarkIdAsync(args.DroppedItem);
			if (draggedId is not null)
			{
				rawEvent.Handled = true;
				if (ResolveBookmarkDrop(targetId, args.dropPosition, draggedId) is not { } move)
				{
					rawEvent.AcceptedOperation = DataPackageOperation.None;
				}
				else
				{
					// Name the group only when dropping into it; between rows the insert line says enough.
					var isInto = args.dropPosition == SidebarItemDropPosition.Center || targetId is null;
					rawEvent.DragUIOverride.IsCaptionVisible = isInto;
					if (isInto)
						rawEvent.DragUIOverride.Caption = string.Format(Strings.MoveToFolderCaptionText.GetLocalizedResource(), GetBookmarkDropTargetName(move.ParentId));
					rawEvent.AcceptedOperation = DataPackageOperation.Move;
				}

				return true;
			}

			if (ResolveBookmarkDrop(targetId, args.dropPosition, null) is null)
				return false;

			rawEvent.Handled = true;
			var folders = await GetDraggedBookmarkableFoldersAsync(args.DroppedItem);
			if (folders.Count == 0)
				rawEvent.AcceptedOperation = DataPackageOperation.None;
			else
				CompleteDragEventArgs(rawEvent, Strings.AddBookmark.GetLocalizedResource(), DataPackageOperation.Link);

			return true;
		}

		private async Task<bool> TryHandleBookmarkDropAsync(ItemDroppedEventArgs args)
		{
			if (!TryGetBookmarkDropTarget(args.DropTarget, out var targetId))
				return false;

			var draggedId = await GetDraggedBookmarkIdAsync(args.DroppedItem);
			if (draggedId is not null)
			{
				if (ResolveBookmarkDrop(targetId, args.dropPosition, draggedId) is { } move)
					bookmarksService.Move(draggedId, move.ParentId, move.Index);

				return true;
			}

			if (ResolveBookmarkDrop(targetId, args.dropPosition, null) is not { } drop)
				return false;

			var folders = await GetDraggedBookmarkableFoldersAsync(args.DroppedItem);
			if (folders.Count > 0)
				bookmarksService.AddBookmarks(folders, drop.ParentId, drop.Index);

			return true;
		}

		#endregion
	}
}
