// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// Settings for trimming the right-click menu: the primary-command icon row, the hidden shell entries and the
	/// hidden New menu entries.
	/// </summary>
	public sealed partial class ContextMenuDeclutterViewModel : ObservableObject
	{
		private readonly IGeneralSettingsService generalSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().GeneralSettingsService;

		public ObservableCollection<string> HiddenShellEntries { get; }

		/// <summary>
		/// One checkbox per extension, so two ShellNew entries sharing one show and hide together. Entries without
		/// an extension can't be hidden and aren't listed.
		/// </summary>
		public ObservableCollection<NewMenuEntryItem> NewMenuEntries { get; }

		public ContextMenuDeclutterViewModel()
		{
			HiddenShellEntries = new(generalSettings.HiddenShellMenuEntries ?? []);

			var hidden = new HashSet<string>(generalSettings.HiddenNewMenuEntries ?? [], StringComparer.OrdinalIgnoreCase);
			var entries = Ioc.Default.GetRequiredService<IAddItemService>().GetEntries() ?? [];
			NewMenuEntries = new(entries
				.Where(entry => !string.IsNullOrEmpty(entry.Extension))
				.GroupBy(entry => entry.Extension!, StringComparer.OrdinalIgnoreCase)
				.Select(group => new NewMenuEntryItem(
					string.Join(" / ", group.Select(entry => entry.Name).Where(name => !string.IsNullOrEmpty(name)).Distinct()),
					group.Key,
					!hidden.Contains(group.Key),
					SaveHiddenNewMenuEntries)));
		}

		public bool ShowContextMenuIconRow
		{
			get => generalSettings.ShowContextMenuIconRow;
			set
			{
				if (value != generalSettings.ShowContextMenuIconRow)
				{
					generalSettings.ShowContextMenuIconRow = value;
					OnPropertyChanged();
				}
			}
		}

		public void AddHiddenShellEntry(string? text)
		{
			text = text?.Trim();
			if (string.IsNullOrEmpty(text) || HiddenShellEntries.Contains(text, StringComparer.OrdinalIgnoreCase))
				return;

			HiddenShellEntries.Add(text);
			SaveHiddenShellEntries();
		}

		public void RemoveHiddenShellEntry(string text)
		{
			if (HiddenShellEntries.Remove(text))
				SaveHiddenShellEntries();
		}

		/// <summary>
		/// Shell entries the menus have shown this session that aren't hidden yet, narrowed to those containing the query.
		/// </summary>
		public List<string> GetShellEntrySuggestions(string? query)
		{
			query = query?.Trim();
			return [.. ShellMenuBlocklist.GetSeenEntries()
				.Where(entry => !HiddenShellEntries.Contains(entry, StringComparer.OrdinalIgnoreCase))
				.Where(entry => string.IsNullOrEmpty(query) || entry.Contains(query, StringComparison.OrdinalIgnoreCase))
				.Order(StringComparer.CurrentCultureIgnoreCase)];
		}

		private void SaveHiddenShellEntries()
			=> generalSettings.HiddenShellMenuEntries = [.. HiddenShellEntries];

		private void SaveHiddenNewMenuEntries()
			=> generalSettings.HiddenNewMenuEntries = [.. NewMenuEntries.Where(entry => !entry.IsShown).Select(entry => entry.Extension)];
	}

	public sealed partial class NewMenuEntryItem : ObservableObject
	{
		private readonly Action onShownChanged;

		public string Name { get; }

		public string Extension { get; }

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

		public NewMenuEntryItem(string name, string extension, bool isShown, Action onShownChanged)
		{
			Name = string.IsNullOrEmpty(name) ? extension : name;
			Extension = extension;
			this.isShown = isShown;
			this.onShownChanged = onShownChanged;
		}
	}
}
