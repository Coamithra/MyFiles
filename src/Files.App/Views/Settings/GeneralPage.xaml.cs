// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Settings
{
	public sealed partial class GeneralPage : Page
	{
		public ContextMenuDeclutterViewModel ContextMenuDeclutter { get; } = new();

		public GeneralPage()
		{
			InitializeComponent();
		}

		private void RemoveStartupPage(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: PageOnStartupViewModel page })
				ViewModel.RemovePageCommand.Execute(page);
		}

		private void ShowHiddenShellEntrySuggestions()
		{
			var suggestions = ContextMenuDeclutter.GetShellEntrySuggestions(HiddenShellEntryBox.Text);
			HiddenShellEntryBox.ItemsSource = suggestions;
			HiddenShellEntryBox.IsSuggestionListOpen = suggestions.Count > 0;
		}

		private void SubmitHiddenShellEntry(string? text)
		{
			ContextMenuDeclutter.AddHiddenShellEntry(text);
			HiddenShellEntryBox.Text = string.Empty;
			HiddenShellEntryBox.IsSuggestionListOpen = false;
		}

		private void HiddenShellEntryBox_GotFocus(object sender, RoutedEventArgs e)
			=> ShowHiddenShellEntrySuggestions();

		private void HiddenShellEntryBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
		{
			if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
				ShowHiddenShellEntrySuggestions();
		}

		private void HiddenShellEntryBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
			=> SubmitHiddenShellEntry(args.ChosenSuggestion as string ?? args.QueryText);

		private void AddHiddenShellEntry(object sender, RoutedEventArgs e)
			=> SubmitHiddenShellEntry(HiddenShellEntryBox.Text);

		private void RemoveHiddenShellEntry(object sender, RoutedEventArgs e)
		{
			if (sender is FrameworkElement { DataContext: string entry })
				ContextMenuDeclutter.RemoveHiddenShellEntry(entry);
		}
	}
}
