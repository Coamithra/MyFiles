// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using WinRT;

namespace Files.App.Data.Items
{
	/// <summary>
	/// A row of the sidebar's Bookmarks section that stands for a node of the bookmarks tree.
	/// </summary>
	public interface IBookmarkSidebarItem
	{
		string BookmarkId { get; }
	}

	/// <summary>
	/// The Bookmarks section header. It shows a themed glyph because there is no bitmap asset for the section.
	/// </summary>
	public sealed partial class BookmarksSectionItem : LocationItem, ISidebarItemPresentationModel
	{
		FrameworkElement? ISidebarItemPresentationModel.IconElement => CreateIcon();

		[DynamicWindowsRuntimeCast(typeof(Style))]
		private static ThemedIcon CreateIcon() => new()
		{
			Width = 16,
			Height = 16,
			IconType = ThemedIconTypes.Layered,
			Style = (Style)Application.Current.Resources["App.ThemedIcons.Favorite"],
		};
	}

	/// <summary>
	/// A bookmark group: a named container that toggles on click and never navigates.
	/// </summary>
	public sealed partial class BookmarkGroupItem : LocationItem, IBookmarkSidebarItem, ISidebarReorderableItem
	{
		private bool isApplyingStoredExpansion;

		public string BookmarkId { get; }

		public string ReorderId => BookmarkId;

		public BookmarkGroupItem(string bookmarkId)
		{
			BookmarkId = bookmarkId;
			Section = SectionType.Bookmarks;
			MenuOptions = new ContextMenuOptions();
			ChildItems = [];
			PropertyChanged += OnPropertyChanged;
		}

		public void SetName(string name)
		{
			if (Text == name)
				return;

			Text = name;
			ToolTip = name;
			OnPropertyChanged(nameof(Text));
		}

		/// <summary>
		/// Shows the expansion stored in the tree without writing it back.
		/// </summary>
		public void ApplyStoredExpansion(bool isExpanded)
		{
			isApplyingStoredExpansion = true;
			try
			{
				IsExpanded = isExpanded;
			}
			finally
			{
				isApplyingStoredExpansion = false;
			}
		}

		// Group expansion belongs to the tree, not to the tab, so the user's toggles are persisted.
		private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(IsExpanded) && !isApplyingStoredExpansion)
				Ioc.Default.GetRequiredService<IBookmarksService>().SetExpanded(BookmarkId, IsExpanded);
		}
	}

	/// <summary>
	/// A folder bookmark. It opens the folder like any location row and doesn't expand into subfolders.
	/// </summary>
	public sealed partial class BookmarkLocationItem : LocationItem, IBookmarkSidebarItem, ISidebarReorderableItem
	{
		public string BookmarkId { get; }

		public string ReorderId => BookmarkId;

		/// <summary>
		/// The folder's own name, shown when the bookmark has no custom name.
		/// </summary>
		public string FolderName { get; private set; } = string.Empty;

		/// <summary>
		/// The folder couldn't be found when the row was built.
		/// </summary>
		public bool IsMissing { get; private set; }

		private BookmarkLocationItem(string bookmarkId)
		{
			BookmarkId = bookmarkId;
			Section = SectionType.Bookmarks;
			MenuOptions = new ContextMenuOptions
			{
				IsLocationItem = true,
				ShowProperties = true,
				ShowShellItems = true,
			};
		}

		public void SetDisplayText(string? customName)
		{
			var text = string.IsNullOrEmpty(customName) ? FolderName : customName;
			if (Text == text)
				return;

			Text = text;
			OnPropertyChanged(nameof(Text));
		}

		public static async Task<BookmarkLocationItem> CreateAsync(BookmarkNode node)
		{
			var path = node.Path
				?? throw new ArgumentException("A bookmark group has no folder.", nameof(node));
			var item = new BookmarkLocationItem(node.Id) { Path = path };

			var fallbackName = SystemIO.Path.GetFileName(SystemIO.Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } fileName ? fileName : path;
			var iconPath = path;
			var isFolderIcon = true;
			if (LibraryManager.IsLibraryPath(path))
			{
				item.FolderName = SystemIO.Path.GetFileNameWithoutExtension(path);
				item.IsMissing = !SystemIO.File.Exists(path);
				isFolderIcon = false;
			}
			else
			{
				var root = await FilesystemTasks.WrapNullable(() => DriveHelpers.GetRootFromPathAsync(path));
				var folder = await FilesystemTasks.WrapNullable(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path, root));
				item.IsMissing = !folder;
				item.FolderName = folder.Result?.DisplayName is { Length: > 0 } displayName ? displayName : fallbackName;
				if (folder.Result is { } resolved)
					iconPath = resolved.Path;
			}

			item.Text = string.IsNullOrEmpty(node.Name) ? item.FolderName : node.Name;
			item.Path = path;
			_ = item.IsMissing ? item.LoadDefaultIconAsync() : item.LoadIconAsync(iconPath, isFolderIcon);

			return item;
		}

		private async Task LoadIconAsync(string iconPath, bool isFolder)
		{
			try
			{
				var data = await FileThumbnailHelper.GetIconAsync(iconPath, Constants.ShellIconSizes.Small, isFolder, IconOptions.ReturnIconOnly | IconOptions.SkipSizeSnapping);
				IconData = data;

				// Icon raises PropertyChanged, which builds XAML in a realized row, so assign it on the UI thread.
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					if (await data.ToBitmapAsync() is { } bitmap)
						Icon = bitmap;
				});
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Bookmarks: icon load failed for {Path}", LogPathHelper.RedactPath(iconPath));
			}
		}

		private async Task LoadDefaultIconAsync()
		{
			try
			{
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					Icon = await UIHelpers.GetSidebarIconResource(Constants.ImageRes.Folder);
				});
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Bookmarks: default icon load failed");
			}
		}
	}
}
