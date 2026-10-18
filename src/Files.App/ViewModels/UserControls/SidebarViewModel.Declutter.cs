// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using System.Collections.Specialized;
using System.Windows.Input;

namespace Files.App.ViewModels.UserControls
{
	public sealed partial class SidebarViewModel
	{
		private ICommand? hideCloudDriveCommand;
		private ICommand HideCloudDriveCommand => hideCloudDriveCommand ??= new RelayCommand(HideCloudDrive);

		public bool ShowHomeSection
		{
			get => UserSettingsService.GeneralSettingsService.ShowHomeSection;
			set
			{
				if (value == UserSettingsService.GeneralSettingsService.ShowHomeSection)
					return;

				UserSettingsService.GeneralSettingsService.ShowHomeSection = value;
			}
		}

		// Home has no child items, so it can't go through UpdateSectionVisibilityAsync, whose Reset sync has no element source for it.
		private async Task SyncHomeSectionAsync()
		{
			var home = GetSection(SectionType.Home);
			if (ShowHomeSection && home is null)
				await CreateSectionAsync(SectionType.Home);
			else if (!ShowHomeSection && home is not null)
			{
				sidebarItems.Remove(home);
				if (SidebarSelectedItem == home)
					SidebarSelectedItem = null;
			}

			UpdateSidebarSelectedItemFromArgs(CurrentPath);
		}

		private bool IsCloudDriveHidden(INavigationControlItem item)
		{
			var hidden = UserSettingsService.GeneralSettingsService.HiddenSidebarCloudDrives;
			return hidden is not null && item.Path is not null && hidden.Contains(item.Path, StringComparer.OrdinalIgnoreCase);
		}

		private IReadOnlyList<INavigationControlItem> GetVisibleCloudDrives()
			=> CloudDrivesManager.Drives.Where(drive => !IsCloudDriveHidden(drive)).ToList().AsReadOnly();

		private void SyncCloudDriveVisibility()
		{
			if (!ShowCloudDrivesSection)
				return;

			// Drop the section rather than leave an empty header when every provider is hidden.
			if (!GetVisibleCloudDrives().Any())
			{
				if (GetSection(SectionType.CloudDrives) is { } section)
					sidebarItems.Remove(section);
				return;
			}

			Manager_DataChanged(SectionType.CloudDrives, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}

		private void HideCloudDrive()
		{
			if (rightClickedItem is not DriveItem { Type: Data.Items.DriveType.CloudDrive } drive || drive.Path is null)
				return;

			var hidden = UserSettingsService.GeneralSettingsService.HiddenSidebarCloudDrives ?? [];
			if (hidden.Contains(drive.Path, StringComparer.OrdinalIgnoreCase))
				return;

			UserSettingsService.GeneralSettingsService.HiddenSidebarCloudDrives = [.. hidden, drive.Path];
		}
	}
}
