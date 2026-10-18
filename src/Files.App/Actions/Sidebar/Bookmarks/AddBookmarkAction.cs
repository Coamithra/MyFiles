// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class AddBookmarkAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;
		private readonly IBookmarksService service;

		public string Label
			=> Strings.AddBookmark.GetLocalizedResource();

		public string Description
			=> Strings.AddBookmarkDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.FavoriteAdd");

		public HotKey HotKey
			=> new(Keys.D, KeyModifiers.CtrlShift);

		// Only offered while it would add something, so repeated use never duplicates a bookmark.
		public bool IsExecutable
			=> GetFolderPaths().Any(path => !service.IsBookmarked(path));

		public AddBookmarkAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();
			service = Ioc.Default.GetRequiredService<IBookmarksService>();

			context.PropertyChanged += Context_PropertyChanged;
			service.BookmarksChanged += (s, e) => OnPropertyChanged(nameof(IsExecutable));
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			service.AddBookmarks(GetFolderPaths(), skipExisting: true);
			return Task.CompletedTask;
		}

		// The selected folders, or the current folder when nothing is selected.
		private IEnumerable<string> GetFolderPaths()
		{
			if (context.PageType is ContentPageTypes.RecycleBin or ContentPageTypes.Home)
				return [];

			IEnumerable<ListedItem> items = context.HasSelection
				? context.SelectedItems
				: context.Folder is { } folder ? (ListedItem[])[folder] : (ListedItem[])[];

			return items
				.Where(item => item.PrimaryItemAttribute is StorageItemTypes.Folder && !item.IsArchive && BookmarksService.CanBookmark(item.ItemPath))
				.Select(item => item.ItemPath!);
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.Folder):
				case nameof(IContentPageContext.SelectedItems):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
