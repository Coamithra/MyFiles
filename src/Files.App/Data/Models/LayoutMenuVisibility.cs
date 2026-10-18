// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models
{
	/// <summary>
	/// Which entries of the toolbar's Layout menu show, from <see cref="ILayoutSettingsService.HiddenLayoutMenuEntries"/>.
	/// The flyout binds to it and the right-click Layout submenu reads it, so both follow one set of rules.
	/// </summary>
	public sealed partial class LayoutMenuVisibility : ObservableObject
	{
		public const string SizeEntryId = "Size";

		/// <summary>
		/// The entries that can be hidden, in flyout order. Command entries use their command code.
		/// </summary>
		public static IReadOnlyList<string> EntryIds { get; } =
		[
			nameof(CommandCodes.LayoutDetails),
			nameof(CommandCodes.LayoutList),
			nameof(CommandCodes.LayoutCards),
			nameof(CommandCodes.LayoutGrid),
			nameof(CommandCodes.LayoutColumns),
			SizeEntryId,
			nameof(CommandCodes.ToggleShowHiddenItems),
			nameof(CommandCodes.ToggleShowFileExtensions),
			nameof(CommandCodes.LayoutAdaptive),
		];

		private static LayoutMenuVisibility? instance;

		/// <summary>
		/// The app-wide instance. It only listens to app-lifetime singletons, so it never unsubscribes.
		/// </summary>
		public static LayoutMenuVisibility Instance => instance ??= new();

		private readonly ILayoutSettingsService layoutSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().LayoutSettingsService;
		private readonly ICommandManager commands = Ioc.Default.GetRequiredService<ICommandManager>();

		private HashSet<string> hidden;

		private LayoutMenuVisibility()
		{
			hidden = ReadHidden();
			layoutSettings.PropertyChanged += LayoutSettings_PropertyChanged;
			commands.LayoutAdaptive.PropertyChanged += LayoutAdaptive_PropertyChanged;
		}

		public static string GetLabel(string id)
		{
			var commands = Ioc.Default.GetRequiredService<ICommandManager>();
			return id switch
			{
				nameof(CommandCodes.LayoutDetails) => commands.LayoutDetails.Label,
				nameof(CommandCodes.LayoutList) => commands.LayoutList.Label,
				nameof(CommandCodes.LayoutCards) => commands.LayoutCards.Label,
				nameof(CommandCodes.LayoutGrid) => commands.LayoutGrid.Label,
				nameof(CommandCodes.LayoutColumns) => commands.LayoutColumns.Label,
				nameof(CommandCodes.ToggleShowHiddenItems) => commands.ToggleShowHiddenItems.Label,
				nameof(CommandCodes.ToggleShowFileExtensions) => commands.ToggleShowFileExtensions.Label,
				nameof(CommandCodes.LayoutAdaptive) => commands.LayoutAdaptive.Label,
				SizeEntryId => Strings.Size.GetLocalizedResource(),
				_ => id,
			};
		}

		/// <summary>
		/// Whether the entry is hidden in Settings.
		/// </summary>
		public bool IsEntryHidden(string id)
			=> hidden.Contains(id);

		/// <summary>
		/// Whether the entry shows in the flyout now: not hidden, and for Adaptive also applicable.
		/// </summary>
		public bool IsEntryShown(string id)
			=> !IsEntryHidden(id) && (id is not nameof(CommandCodes.LayoutAdaptive) || commands.LayoutAdaptive.IsExecutable);

		public bool ShowDetails => IsEntryShown(nameof(CommandCodes.LayoutDetails));

		public bool ShowList => IsEntryShown(nameof(CommandCodes.LayoutList));

		public bool ShowCards => IsEntryShown(nameof(CommandCodes.LayoutCards));

		public bool ShowGrid => IsEntryShown(nameof(CommandCodes.LayoutGrid));

		public bool ShowColumns => IsEntryShown(nameof(CommandCodes.LayoutColumns));

		public bool ShowHiddenItemsToggle => IsEntryShown(nameof(CommandCodes.ToggleShowHiddenItems));

		public bool ShowFileExtensionsToggle => IsEntryShown(nameof(CommandCodes.ToggleShowFileExtensions));

		public bool ShowAdaptiveToggle => IsEntryShown(nameof(CommandCodes.LayoutAdaptive));

		public bool ShowLayoutSection => ShowDetails || ShowList || ShowCards || ShowGrid || ShowColumns;

		public bool ShowSizeSection => IsEntryShown(SizeEntryId);

		public bool ShowTogglesSection => ShowHiddenItemsToggle || ShowFileExtensionsToggle || ShowAdaptiveToggle;

		// A divider shows only between two visible sections.
		public bool ShowSizeDivider => ShowSizeSection && ShowLayoutSection;

		public bool ShowTogglesDivider => ShowTogglesSection && (ShowLayoutSection || ShowSizeSection);

		/// <summary>
		/// False when every entry is hidden; the Layout button then collapses instead of opening an empty flyout.
		/// </summary>
		public bool IsAnyEntryShown => ShowLayoutSection || ShowSizeSection || ShowTogglesSection;

		private HashSet<string> ReadHidden()
			=> new(layoutSettings.HiddenLayoutMenuEntries ?? [], StringComparer.Ordinal);

		private void LayoutSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(ILayoutSettingsService.HiddenLayoutMenuEntries))
			{
				hidden = ReadHidden();
				OnPropertyChanged(string.Empty);
			}
		}

		private void LayoutAdaptive_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IRichCommand.IsExecutable))
				OnPropertyChanged(string.Empty);
		}
	}
}
