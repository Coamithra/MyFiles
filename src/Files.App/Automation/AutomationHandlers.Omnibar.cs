// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT;

namespace Files.App.Automation
{
	internal static partial class AutomationHandlers
	{
		[DynamicWindowsRuntimeCast(typeof(Frame))]
		private static NavigationToolbar AddressBar()
			=> (MainWindow.Instance.Content as Frame)?.FindDescendant<NavigationToolbar>()
				?? throw new AutomationException("The address bar is not loaded.");

		private static JsonNode OmnibarState()
		{
			var toolbar = AddressBar();
			var viewModel = toolbar.ViewModel
				?? throw new AutomationException("The address bar has no view model yet.");
			var omnibar = toolbar.FindDescendant<Omnibar>();

			return new JsonObject
			{
				["mode"] = viewModel.OmnibarCurrentSelectedModeName,
				["focused"] = omnibar?.IsFocused,
				["pathText"] = viewModel.PathText,
				["searchButtonVisible"] = viewModel.IsOmnibarSearchModeVisible,
			};
		}

		private static async Task<JsonNode?> SuggestOmnibarAsync(JsonObject p)
		{
			// Empty text is valid here: it shows the path history
			var text = p["text"]?.GetValue<string>()
				?? throw new AutomationException("\"text\" is required.");
			var timeout = TimeSpan.FromMilliseconds(p["timeoutMs"]?.GetValue<int>() ?? 10000);

			var viewModel = AddressBar().ViewModel
				?? throw new AutomationException("The address bar has no view model yet.");

			// As typing does through the TwoWay binding, then the populate call Omnibar_TextChanged makes
			var started = Stopwatch.GetTimestamp();
			viewModel.PathText = text;
			var populate = viewModel.PopulateOmnibarSuggestionsForPathMode();
			var settled = await Task.WhenAny(populate, Task.Delay(timeout)) == populate;
			if (settled)
				await populate;

			return new JsonObject
			{
				["pending"] = !settled,
				["source"] = viewModel.PathModeSuggestionsSource,
				["elapsedMs"] = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
				["items"] = new JsonArray([.. viewModel.PathModeSuggestionItems.Select(item => (JsonNode)new JsonObject
				{
					["path"] = item.Path,
					["displayName"] = item.DisplayName,
				})]),
				["omnibar"] = OmnibarState(),
			};
		}

		private static async Task<JsonNode?> SubmitOmnibarAsync(JsonObject p)
		{
			var text = RequiredString(p, "text");
			var timeout = TimeSpan.FromMilliseconds(p["timeoutMs"]?.GetValue<int>() ?? 60000);
			var limit = p["limit"]?.GetValue<int>() ?? 20;

			var toolbar = AddressBar();
			var viewModel = toolbar.ViewModel
				?? throw new AutomationException("The address bar has no view model yet.");
			var pane = ActivePane();
			var shellViewModel = pane.ShellViewModel;
			var windowsBefore = OwnTopLevelWindows();
			var wasSearchResults = shellViewModel?.IsSearchResults ?? false;
			var previousQuery = pane.InstanceViewModel.CurrentSearchQuery;

			var searchCompleted = new TaskCompletionSource();
			void OnItemLoadStatusChanged(object? sender, ItemLoadStatusChangedEventArgs e)
			{
				if (e.Status is ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete && shellViewModel is { IsSearchResults: true })
					searchCompleted.TrySetResult();
			}

			if (shellViewModel is not null)
				shellViewModel.ItemLoadStatusChanged += OnItemLoadStatusChanged;

			string? result = null;
			try
			{
				// As typing does through the TwoWay binding, then Enter
				viewModel.PathText = text;
				var submit = toolbar.SubmitPathModeQueryAsync(text);
				if (await Task.WhenAny(submit, Task.Delay(timeout)) == submit)
					result = await submit;

				if (result is "handled" || result?.StartsWith("command:", StringComparison.Ordinal) == true)
				{
					// A search streams results, so wait for it to complete; anything else settles once loading stops
					var started = DateTime.UtcNow;
					await WaitUntilAsync(timeout, () =>
						searchCompleted.Task.IsCompleted ||
						DateTime.UtcNow - started > TimeSpan.FromMilliseconds(500) &&
						ActivePane().ShellViewModel is { IsLoadingItems: false } current &&
						(!current.IsSearchResults || current.IsSearchResults == wasSearchResults && ActivePane().InstanceViewModel.CurrentSearchQuery == previousQuery),
						$"Submitting \"{text}\" did not settle");
				}
			}
			finally
			{
				if (shellViewModel is not null)
					shellViewModel.ItemLoadStatusChanged -= OnItemLoadStatusChanged;
			}

			var activePane = ActivePane();
			JsonNode? search = null;
			if (activePane.ShellViewModel is { IsSearchResults: true } results)
			{
				search = new JsonObject
				{
					["query"] = activePane.InstanceViewModel.CurrentSearchQuery,
					["backends"] = FolderSearch.LastResultsPageBackends.ToString(),
					["count"] = results.FilesAndFolders.Count,
					["paths"] = new JsonArray([.. results.FilesAndFolders.ToList().Take(limit).Select(item => (JsonNode?)item.ItemPath)]),
				};
			}

			return new JsonObject
			{
				["result"] = result,
				["pending"] = result is null,
				["dialogs"] = new JsonObject
				{
					["app"] = new JsonArray([.. OpenDialogTitles().Select(title => (JsonNode?)title)]),
					["ownWindows"] = OwnTopLevelWindows().Except(windowsBefore).Count(),
				},
				["omnibar"] = OmnibarState(),
				["pane"] = PaneState(),
				["search"] = search,
			};
		}

		[DynamicWindowsRuntimeCast(typeof(ContentDialog))]
		private static IEnumerable<string> OpenDialogTitles()
			=> VisualTreeHelper.GetOpenPopupsForXamlRoot(MainWindow.Instance.Content.XamlRoot)
				.Select(popup => popup.Child)
				.OfType<ContentDialog>()
				.Select(dialog => dialog.Title?.ToString() ?? string.Empty)
				.ToList();

		// Visible top-level windows of this process other than the main window, which is where
		// in-process shell error boxes appear. Prompts from other processes are not seen.
		private static unsafe List<HWND> OwnTopLevelWindows()
		{
			var mainWindow = new HWND(MainWindow.Instance.WindowHandle);
			var processId = (uint)Environment.ProcessId;
			var windows = new List<HWND>();
			foreach (var hwnd in Win32Helper.GetDesktopWindows())
			{
				uint windowProcessId = 0;
				PInvoke.GetWindowThreadProcessId(hwnd, &windowProcessId);
				if (windowProcessId == processId && hwnd != mainWindow && PInvoke.IsWindowVisible(hwnd))
					windows.Add(hwnd);
			}

			return windows;
		}
	}
}
