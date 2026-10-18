// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.ViewModels.Settings
{
	/// <summary>
	/// The terminal the Open in terminal commands launch.
	/// </summary>
	public sealed partial class TerminalSettingsViewModel : ObservableObject
	{
		private readonly IGeneralSettingsService generalSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().GeneralSettingsService;

		/// <summary>
		/// Index into the terminal ComboBox, whose items follow <see cref="TerminalType"/>'s order.
		/// </summary>
		public int SelectedTerminalIndex
		{
			get => (int)generalSettings.TerminalType;
			set
			{
				if (value < 0 || value == (int)generalSettings.TerminalType)
					return;

				generalSettings.TerminalType = (TerminalType)value;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsCustomTerminal));
			}
		}

		public bool IsCustomTerminal
			=> generalSettings.TerminalType is TerminalType.Custom;

		public string CustomTerminalPath
		{
			get => generalSettings.CustomTerminalPath;
			set
			{
				if (value != generalSettings.CustomTerminalPath)
				{
					generalSettings.CustomTerminalPath = value;
					OnPropertyChanged();
				}
			}
		}

		public string CustomTerminalArguments
		{
			get => generalSettings.CustomTerminalArguments;
			set
			{
				if (value != generalSettings.CustomTerminalArguments)
				{
					generalSettings.CustomTerminalArguments = value;
					OnPropertyChanged();
				}
			}
		}
	}
}
