// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class RemoveBookmarkAction : ObservableObject, IAction
	{
		private readonly ISidebarContext sidebarContext;
		private readonly IBookmarksService service;

		public string Label
			=> Strings.RemoveBookmark.GetLocalizedResource();

		public string Description
			=> Strings.RemoveBookmarkDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Delete");

		public bool IsAccessibleGlobally
			=> false;

		public bool IsExecutable
			=> sidebarContext.RightClickedItem is IBookmarkSidebarItem;

		public RemoveBookmarkAction()
		{
			sidebarContext = Ioc.Default.GetRequiredService<ISidebarContext>();
			service = Ioc.Default.GetRequiredService<IBookmarksService>();
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			if (sidebarContext.RightClickedItem is not IBookmarkSidebarItem bookmark || service.Find(bookmark.BookmarkId) is not { } node)
				return;

			// Only the bookmarks go; the folders on disk are never touched. A group with content asks first.
			if (node.IsGroup && node.Children is { Count: > 0 } && !await BookmarkDialogHelper.ShowRemoveGroupConfirmationAsync(node.Name ?? string.Empty))
				return;

			service.Remove(node.Id);
		}
	}
}
