// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Files.App.ViewModels.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Settings
{
	public sealed partial class LayoutPage : Page
	{
		public LayoutMenuSettingsViewModel LayoutMenu { get; } = new();

		public LayoutPage()
		{
			InitializeComponent();
		}
	}
}
