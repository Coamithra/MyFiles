// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// The picker for which entries the toolbar's Layout menu shows.
	/// </summary>
	public sealed partial class LayoutMenuSettingsViewModel : ObservableObject
	{
		private readonly ILayoutSettingsService layoutSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().LayoutSettingsService;

		public ObservableCollection<LayoutMenuEntryItem> Entries { get; }

		public LayoutMenuSettingsViewModel()
		{
			var hidden = new HashSet<string>(layoutSettings.HiddenLayoutMenuEntries ?? [], StringComparer.Ordinal);
			Entries = new(LayoutMenuVisibility.EntryIds.Select(id => new LayoutMenuEntryItem(
				id,
				LayoutMenuVisibility.GetLabel(id),
				!hidden.Contains(id),
				SaveHiddenEntries)));
		}

		// Nothing hidden is stored as null, the same as never changed.
		private void SaveHiddenEntries()
		{
			List<string> hidden = [.. Entries.Where(entry => !entry.IsShown).Select(entry => entry.Id)];
			layoutSettings.HiddenLayoutMenuEntries = hidden.Count == 0 ? null : hidden;
		}
	}

	public sealed partial class LayoutMenuEntryItem : ObservableObject
	{
		private readonly Action onShownChanged;

		public string Id { get; }

		public string Label { get; }

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

		public LayoutMenuEntryItem(string id, string label, bool isShown, Action onShownChanged)
		{
			Id = id;
			Label = label;
			this.isShown = isShown;
			this.onShownChanged = onShownChanged;
		}
	}
}
