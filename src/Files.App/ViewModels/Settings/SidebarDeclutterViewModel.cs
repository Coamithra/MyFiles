// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// Settings for which sidebar sections and cloud drives show. Settings opens as a tab next to the sidebar, whose menus
	/// change the same settings, so changes from either side are mirrored here while attached.
	/// </summary>
	public sealed partial class SidebarDeclutterViewModel : ObservableObject
	{
		private readonly IUserSettingsService userSettings = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private bool isAttached;
		private IGeneralSettingsService GeneralSettings => userSettings.GeneralSettingsService;

		// Property names match the setting names, so a setting change raises the property of the same name.
		private static readonly HashSet<string> SectionSettings =
		[
			nameof(IGeneralSettingsService.ShowHomeSection),
			nameof(IGeneralSettingsService.ShowPinnedSection),
			nameof(IGeneralSettingsService.ShowBookmarksSection),
			nameof(IGeneralSettingsService.ShowLibrarySection),
			nameof(IGeneralSettingsService.ShowDrivesSection),
			nameof(IGeneralSettingsService.ShowCloudDrivesSection),
			nameof(IGeneralSettingsService.ShowNetworkSection),
			nameof(IGeneralSettingsService.ShowWslSection),
			nameof(IGeneralSettingsService.ShowFileTagsSection),
		];

		/// <summary>
		/// One checkbox per cloud drive detected this session.
		/// </summary>
		public ObservableCollection<SidebarCloudDriveEntry> CloudDrives { get; }

		public SidebarDeclutterViewModel()
		{
			var hidden = HiddenPaths();
			CloudDrives = new(CloudDrivesManager.Drives
				.Where(drive => !string.IsNullOrEmpty(drive.Path))
				.Select(drive => new SidebarCloudDriveEntry(drive.Text, drive.Path!, !hidden.Contains(drive.Path!), SaveHiddenCloudDrives)));
		}

		/// <summary>
		/// Starts mirroring setting changes, catching up on any made while detached.
		/// </summary>
		public void Attach()
		{
			if (isAttached)
				return;

			isAttached = true;
			userSettings.OnSettingChangedEvent += UserSettings_OnSettingChangedEvent;
			OnPropertyChanged(string.Empty);
			SyncCloudDrives();
		}

		public void Detach()
		{
			if (!isAttached)
				return;

			isAttached = false;
			userSettings.OnSettingChangedEvent -= UserSettings_OnSettingChangedEvent;
		}

		public bool ShowHomeSection
		{
			get => GeneralSettings.ShowHomeSection;
			set => GeneralSettings.ShowHomeSection = value;
		}

		public bool ShowPinnedSection
		{
			get => GeneralSettings.ShowPinnedSection;
			set => GeneralSettings.ShowPinnedSection = value;
		}

		public bool ShowBookmarksSection
		{
			get => GeneralSettings.ShowBookmarksSection;
			set => GeneralSettings.ShowBookmarksSection = value;
		}

		public bool ShowLibrarySection
		{
			get => GeneralSettings.ShowLibrarySection;
			set => GeneralSettings.ShowLibrarySection = value;
		}

		public bool ShowDrivesSection
		{
			get => GeneralSettings.ShowDrivesSection;
			set => GeneralSettings.ShowDrivesSection = value;
		}

		public bool ShowCloudDrivesSection
		{
			get => GeneralSettings.ShowCloudDrivesSection;
			set => GeneralSettings.ShowCloudDrivesSection = value;
		}

		public bool ShowNetworkSection
		{
			get => GeneralSettings.ShowNetworkSection;
			set => GeneralSettings.ShowNetworkSection = value;
		}

		public bool ShowWslSection
		{
			get => GeneralSettings.ShowWslSection;
			set => GeneralSettings.ShowWslSection = value;
		}

		public bool ShowFileTagsSection
		{
			get => GeneralSettings.ShowFileTagsSection;
			set => GeneralSettings.ShowFileTagsSection = value;
		}

		private HashSet<string> HiddenPaths()
			=> new(GeneralSettings.HiddenSidebarCloudDrives ?? [], StringComparer.OrdinalIgnoreCase);

		// Hidden drives that aren't detected right now (signed out, not running) stay hidden.
		private void SaveHiddenCloudDrives()
		{
			var listed = new HashSet<string>(CloudDrives.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);
			GeneralSettings.HiddenSidebarCloudDrives =
			[
				.. HiddenPaths().Where(path => !listed.Contains(path)),
				.. CloudDrives.Where(entry => !entry.IsShown).Select(entry => entry.Path),
			];
		}

		private void UserSettings_OnSettingChangedEvent(object? sender, SettingChangedEventArgs e)
		{
			if (SectionSettings.Contains(e.SettingName))
			{
				OnPropertyChanged(e.SettingName);
			}
			else if (e.SettingName == nameof(IGeneralSettingsService.HiddenSidebarCloudDrives))
			{
				SyncCloudDrives();
			}
		}

		private void SyncCloudDrives()
		{
			var hidden = HiddenPaths();
			foreach (var entry in CloudDrives)
				entry.SyncShown(!hidden.Contains(entry.Path));
		}
	}

	public sealed partial class SidebarCloudDriveEntry : ObservableObject
	{
		private readonly Action onShownChanged;

		public string Name { get; }

		public string Path { get; }

		private bool isShown;
		public bool IsShown
		{
			get => isShown;
			set
			{
				if (SetProperty(ref isShown, value))
					onShownChanged();
			}
		}

		public SidebarCloudDriveEntry(string? name, string path, bool isShown, Action onShownChanged)
		{
			Name = string.IsNullOrEmpty(name) ? path : name;
			Path = path;
			this.isShown = isShown;
			this.onShownChanged = onShownChanged;
		}

		/// <summary>
		/// Mirrors a change saved elsewhere without saving it again.
		/// </summary>
		public void SyncShown(bool value)
			=> SetProperty(ref isShown, value, nameof(IsShown));
	}
}
