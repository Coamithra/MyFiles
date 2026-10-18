// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class RenameBookmarkAction : ObservableObject, IAction
	{
		private readonly ISidebarContext sidebarContext;
		private readonly IBookmarksService service;

		public string Label
			=> Strings.Rename.GetLocalizedResource();

		public string Description
			=> Strings.RenameBookmarkDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Rename");

		public bool IsAccessibleGlobally
			=> false;

		public bool IsExecutable
			=> sidebarContext.RightClickedItem is IBookmarkSidebarItem;

		public RenameBookmarkAction()
		{
			sidebarContext = Ioc.Default.GetRequiredService<ISidebarContext>();
			service = Ioc.Default.GetRequiredService<IBookmarksService>();
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			if (sidebarContext.RightClickedItem is not IBookmarkSidebarItem bookmark || service.Find(bookmark.BookmarkId) is not { } node)
				return;

			// A bookmark's empty name falls back to its folder's name, shown as the placeholder.
			var name = await BookmarkDialogHelper.ShowNameDialogAsync(
				node.IsGroup ? Strings.Rename.GetLocalizedResource() : Strings.RenameBookmarkDialogTitle.GetLocalizedResource(),
				node.Name ?? string.Empty,
				node.IsGroup ? Strings.BookmarkGroupNamePlaceholder.GetLocalizedResource() : (bookmark as BookmarkLocationItem)?.FolderName ?? string.Empty,
				Strings.Rename.GetLocalizedResource(),
				allowEmpty: !node.IsGroup);

			if (name is not null)
				service.Rename(node.Id, name);
		}
	}
}
