// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Files.App.Helpers
{
	/// <summary>
	/// Dialogs for naming and removing bookmarks and bookmark groups.
	/// </summary>
	internal static class BookmarkDialogHelper
	{
		/// <summary>
		/// Asks for a name. Returns null when cancelled; the entered text (possibly empty) otherwise.
		/// </summary>
		/// <param name="allowEmpty">Whether an empty name may be confirmed (it resets a bookmark to its folder's name).</param>
		public static async Task<string?> ShowNameDialogAsync(string title, string initialText, string placeholder, string primaryButtonText, bool allowEmpty)
		{
			DynamicDialog? dialog = null;
			var inputText = new TextBox()
			{
				Text = initialText,
				PlaceholderText = placeholder,
			};

			void UpdateButtons()
				=> dialog!.ViewModel.DynamicButtonsEnabled = allowEmpty || !string.IsNullOrWhiteSpace(inputText.Text)
					? DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel
					: DynamicDialogButtons.Cancel;

			inputText.TextChanged += (s, e) => UpdateButtons();

			dialog = new DynamicDialog(new DynamicDialogViewModel()
			{
				TitleText = title,
				DisplayControl = new Grid()
				{
					MinWidth = 300d,
					Children =
					{
						inputText
					}
				},
				DisplayControlOnLoaded = (vm, e) =>
				{
					inputText.Focus(FocusState.Programmatic);
					inputText.SelectAll();
				},
				PrimaryButtonAction = (vm, e) =>
				{
					vm.Hide();
				},
				KeyDownAction = (vm, e) =>
				{
					if (e.Key == VirtualKey.Enter && (allowEmpty || !string.IsNullOrWhiteSpace(inputText.Text)))
					{
						vm.DynamicResult = DynamicDialogResult.Primary;
						vm.Hide();
					}
					else if (e.Key == VirtualKey.Escape)
					{
						vm.Hide();
					}
				},
				PrimaryButtonText = primaryButtonText,
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DynamicButtons = DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel,
			});

			UpdateButtons();
			await dialog.TryShowAsync();

			return dialog.DynamicResult == DynamicDialogResult.Primary ? inputText.Text.Trim() : null;
		}

		public static async Task<bool> ShowRemoveGroupConfirmationAsync(string groupName)
		{
			var dialog = new DynamicDialog(new DynamicDialogViewModel()
			{
				TitleText = Strings.RemoveBookmarkGroupDialogTitle.GetLocalizedResource(),
				SubtitleText = string.Format(Strings.RemoveBookmarkGroupDialogText.GetLocalizedResource(), groupName),
				PrimaryButtonText = Strings.Remove.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DynamicButtons = DynamicDialogButtons.Primary | DynamicDialogButtons.Cancel,
			});

			await dialog.TryShowAsync();

			return dialog.DynamicResult == DynamicDialogResult.Primary;
		}
	}
}
