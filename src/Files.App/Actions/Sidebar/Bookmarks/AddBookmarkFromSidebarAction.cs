// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class AddBookmarkFromSidebarAction : ObservableObject, IAction
	{
		private readonly ISidebarContext sidebarContext;
		private readonly IBookmarksService service;

		public string Label
			=> Strings.AddBookmark.GetLocalizedResource();

		public string Description
			=> Strings.AddBookmarkFromSidebarDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.FavoriteAdd");

		public bool IsAccessibleGlobally
			=> false;

		// Any folder row outside the Bookmarks section: Pinned items, drives and their subfolders, libraries.
		public bool IsExecutable
			=> sidebarContext.RightClickedItem is { } item &&
				item.Section is not SectionType.Bookmarks &&
				item is LocationItem { IsHeader: false } or DriveItem &&
				BookmarksService.CanBookmark(item.Path) &&
				!service.IsBookmarked(item.Path);

		public AddBookmarkFromSidebarAction()
		{
			sidebarContext = Ioc.Default.GetRequiredService<ISidebarContext>();
			service = Ioc.Default.GetRequiredService<IBookmarksService>();
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			if (sidebarContext.RightClickedItem?.Path is { } path)
				service.AddBookmarks((string[])[path], skipExisting: true);

			return Task.CompletedTask;
		}
	}
}
