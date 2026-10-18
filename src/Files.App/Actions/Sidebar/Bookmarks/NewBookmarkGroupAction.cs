// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class NewBookmarkGroupAction : ObservableObject, IAction
	{
		private readonly ISidebarContext sidebarContext;
		private readonly IBookmarksService service;

		public string Label
			=> Strings.NewBookmarkGroup.GetLocalizedResource();

		public string Description
			=> Strings.NewBookmarkGroupDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.New.Folder");

		public bool IsAccessibleGlobally
			=> false;

		// On the Bookmarks header (a top-level group) or on a group (a group inside it).
		public bool IsExecutable
			=> sidebarContext.RightClickedItem is BookmarksSectionItem or BookmarkGroupItem;

		public NewBookmarkGroupAction()
		{
			sidebarContext = Ioc.Default.GetRequiredService<ISidebarContext>();
			service = Ioc.Default.GetRequiredService<IBookmarksService>();
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			var parentId = (sidebarContext.RightClickedItem as BookmarkGroupItem)?.BookmarkId;
			var name = await BookmarkDialogHelper.ShowNameDialogAsync(
				Strings.NewBookmarkGroup.GetLocalizedResource(),
				string.Empty,
				Strings.BookmarkGroupNamePlaceholder.GetLocalizedResource(),
				Strings.Create.GetLocalizedResource(),
				allowEmpty: false);

			if (!string.IsNullOrWhiteSpace(name))
				service.AddGroup(name, parentId);
		}
	}
}
