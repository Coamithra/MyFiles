// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.UserControls.TabBar;
using System.IO;
using System.Text.Json.Nodes;

namespace Files.App.Automation
{
	/// <summary>
	/// The methods <see cref="AutomationServer"/> exposes. All run on the UI thread.
	/// </summary>
	internal static class AutomationHandlers
	{
		private static readonly (string Name, string Help)[] Methods =
		[
			("help", "List the methods."),
			("ping", "Process id, build environment and whether the startup tabs have loaded."),
			("state", "Window, tabs and the active pane (folder, page type, layout, sort, selection)."),
			("items", "Items in the active pane's folder. Params: limit (default 500)."),
			("navigate", "Navigate the active pane. Params: path (a folder, or Home), wait (default true), timeoutMs."),
			("select", "Select items in the active pane by name. Params: names (array); empty clears the selection."),
			("commands", "List app commands with whether each can run now. Params: filter (substring of code or label)."),
			("command", "Run an app command. Params: code (e.g. NewTab, Refresh), parameter (optional string)."),
			("contextMenu", "Files' own and shell entries of the right-click menu. Params: names (items; omit for the background menu), shell (default true)."),
			("newMenu", "Entries of the New submenu that come from the shell (ShellNew)."),
			("sidebar", "Sidebar sections and their items."),
			("tabs.open", "Open a tab. Params: path (optional; Home when omitted)."),
			("tabs.select", "Select a tab. Params: index."),
			("tabs.close", "Close a tab. Params: index. Closing the last tab closes the window."),
			("settings.get", "User settings as JSON. Params: keys (array; all when omitted)."),
			("settings.set", "Change user settings. Params: values (object of key to value)."),
			("window.show", "Uncloak and activate the window, for debugging the UI."),
			("window.hide", "Cloak the window again. Keyboard focus stays on it until you click elsewhere."),
			("quit", "Close the window as a user would."),
		];

		public static async Task<JsonNode?> InvokeAsync(string method, JsonObject p)
		{
			if (!Methods.Any(m => m.Name == method))
				throw new AutomationException($"Unknown method \"{method}\". Call \"help\" for the list.");

			var dispatcher = App.UiDispatcher
				?? throw new AutomationException("The UI thread is not available yet.");

			return await dispatcher.EnqueueAsync(() => method switch
			{
				"help" => Done(Help()),
				"ping" => Done(Ping()),
				"state" => Done(State()),
				"items" => Done(Items(p)),
				"navigate" => NavigateAsync(p),
				"select" => Done(Select(p)),
				"commands" => Done(ListCommands(p)),
				"command" => RunCommandAsync(p),
				"contextMenu" => ContextMenuAsync(p),
				"newMenu" => Done(NewMenu()),
				"sidebar" => Done(Sidebar()),
				"tabs.open" => OpenTabAsync(p),
				"tabs.select" => Done(SelectTab(p)),
				"tabs.close" => Done(CloseTab(p)),
				"settings.get" => Done(GetSettings(p)),
				"settings.set" => Done(SetSettings(p)),
				"window.show" => Done(ShowWindow(true)),
				"window.hide" => Done(ShowWindow(false)),
				"quit" => Done(Quit()),
				_ => throw new AutomationException($"Method \"{method}\" has no handler."),
			});
		}

		private static Task<JsonNode?> Done(JsonNode? result) => Task.FromResult(result);

		private static JsonNode Help()
			=> new JsonArray([.. Methods.Select(m => (JsonNode)new JsonObject { ["method"] = m.Name, ["help"] = m.Help })]);

		private static JsonNode Ping()
			=> new JsonObject
			{
				["pid"] = Environment.ProcessId,
				["environment"] = AppLifecycleHelper.AppEnvironment.ToString(),
				["ready"] = MainPageViewModel.StartupTabsLoadedTask.IsCompleted,
				["hidden"] = AutomationServer.IsWindowCloaked,
			};

		private static JsonNode State()
		{
			var window = MainWindow.Instance;
			var multitasking = Ioc.Default.GetRequiredService<IMultitaskingContext>();

			var tabs = new JsonArray();
			for (var i = 0; i < MainPageViewModel.AppInstances.Count; i++)
			{
				var tab = MainPageViewModel.AppInstances[i];
				var panes = tab.TabItemContent as IShellPanesPage;
				tabs.Add(new JsonObject
				{
					["index"] = i,
					["header"] = tab.Header,
					["selected"] = i == multitasking.SelectedTabIndex,
					// Tabs not shown yet have no pane, only the path they will open.
					["path"] = panes?.ActivePane?.ShellViewModel?.WorkingDirectory
						?? (tab.NavigationParameter?.NavigationParameter as PaneNavigationArguments)?.LeftPaneNavPathParam
						?? tab.NavigationParameter?.NavigationParameter as string,
					["paneCount"] = panes?.GetPanes().Count(),
				});
			}

			return new JsonObject
			{
				["window"] = new JsonObject
				{
					["title"] = window.Title,
					["visible"] = window.AppWindow.IsVisible,
					["cloaked"] = AutomationServer.IsWindowCloaked,
					["x"] = window.AppWindow.Position.X,
					["y"] = window.AppWindow.Position.Y,
					["width"] = window.AppWindow.Size.Width,
					["height"] = window.AppWindow.Size.Height,
				},
				["tabs"] = tabs,
				["pane"] = PaneState(),
			};
		}

		private static JsonNode? PaneState()
		{
			var pane = Ioc.Default.GetRequiredService<IMultiPanesContext>().ActivePane;
			if (pane is null)
				return null;

			var content = Ioc.Default.GetRequiredService<IContentPageContext>();
			var display = Ioc.Default.GetRequiredService<IDisplayPageContext>();

			return new JsonObject
			{
				["path"] = pane.ShellViewModel?.WorkingDirectory,
				["pageType"] = content.PageType.ToString(),
				["loading"] = pane.ShellViewModel?.IsLoadingItems,
				["itemCount"] = pane.ShellViewModel?.FilesAndFolders.Count,
				["selection"] = new JsonArray([.. content.SelectedItems.Select(item => (JsonNode?)item.ItemNameRaw)]),
				["layout"] = display.LayoutType.ToString(),
				["sortOption"] = display.SortOption.ToString(),
				["sortDirection"] = display.SortDirection.ToString(),
				["canGoBack"] = content.CanGoBack,
				["canGoForward"] = content.CanGoForward,
			};
		}

		private static JsonNode Items(JsonObject p)
		{
			var shellViewModel = ActivePane().ShellViewModel
				?? throw new AutomationException("The active pane has no folder view.");
			var limit = p["limit"]?.GetValue<int>() ?? 500;

			var items = new JsonArray();
			foreach (var item in shellViewModel.FilesAndFolders.ToList().Take(limit))
			{
				items.Add(new JsonObject
				{
					["name"] = item.ItemNameRaw,
					["path"] = item.ItemPath,
					["folder"] = item.IsFolder,
					["size"] = item.IsFolder ? null : item.FileSizeBytes,
					["modified"] = item.ItemDateModifiedReal.ToString("o"),
				});
			}

			return new JsonObject { ["total"] = shellViewModel.FilesAndFolders.Count, ["items"] = items };
		}

		private static async Task<JsonNode?> NavigateAsync(JsonObject p)
		{
			var path = RequiredString(p, "path");
			var wait = p["wait"]?.GetValue<bool>() ?? true;
			var timeout = TimeSpan.FromMilliseconds(p["timeoutMs"]?.GetValue<int>() ?? 15000);

			var pane = ActivePane();
			var isHome = path.Equals("Home", StringComparison.OrdinalIgnoreCase);
			if (isHome)
			{
				pane.NavigateHome();
				if (wait)
				{
					var content = Ioc.Default.GetRequiredService<IContentPageContext>();
					await WaitUntilAsync(timeout, () => content.PageType is ContentPageTypes.Home, "Navigation to Home did not finish");
				}

				return PaneState();
			}

			// Loading starts a moment after NavigateToPath returns, so wait for the view model to report it complete.
			var shellViewModel = pane.ShellViewModel;
			var loaded = new TaskCompletionSource();
			void OnItemLoadStatusChanged(object? sender, ItemLoadStatusChangedEventArgs e)
			{
				if (e.Status is ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete && (e.Path is null || PathEquals(e.Path, path)))
					loaded.TrySetResult();
			}

			if (shellViewModel is not null)
				shellViewModel.ItemLoadStatusChanged += OnItemLoadStatusChanged;
			try
			{
				var started = DateTime.UtcNow;
				pane.NavigateToPath(path);
				if (wait)
				{
					// The polled fallback covers re-navigating to the folder already shown, which may not reload.
					await WaitUntilAsync(timeout, () => loaded.Task.IsCompleted ||
						DateTime.UtcNow - started > TimeSpan.FromSeconds(1) &&
						pane.ShellViewModel is { IsLoadingItems: false } current &&
						PathEquals(current.WorkingDirectory, path),
						$"Navigation to \"{path}\" did not finish");
				}
			}
			finally
			{
				if (shellViewModel is not null)
					shellViewModel.ItemLoadStatusChanged -= OnItemLoadStatusChanged;
			}

			return PaneState();
		}

		private static JsonNode? Select(JsonObject p)
		{
			var page = ActivePane().SlimContentPage
				?? throw new AutomationException("The active pane has no item view.");
			var items = FindItems(p);

			page.ItemManipulationModel.ClearSelection();
			if (items.Count > 0)
				page.ItemManipulationModel.AddSelectedItems(items);

			return PaneState();
		}

		private static JsonNode ListCommands(JsonObject p)
		{
			var filter = p["filter"]?.GetValue<string>();
			var commands = new JsonArray();
			foreach (var command in Ioc.Default.GetRequiredService<ICommandManager>())
			{
				if (command.Code is CommandCodes.None)
					continue;

				var code = command.Code.ToString();
				if (filter is not null &&
					!code.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
					!command.Label.Contains(filter, StringComparison.OrdinalIgnoreCase))
					continue;

				commands.Add(new JsonObject
				{
					["code"] = code,
					["label"] = command.Label,
					["executable"] = command.IsExecutable,
					["hotKeys"] = command.HotKeyText,
				});
			}

			return commands;
		}

		private static async Task<JsonNode?> RunCommandAsync(JsonObject p)
		{
			var code = RequiredString(p, "code");
			var command = Ioc.Default.GetRequiredService<ICommandManager>()[code];
			if (command.Code is CommandCodes.None)
				throw new AutomationException($"Unknown command \"{code}\". Call \"commands\" for the list.");

			if (!command.IsExecutable)
				throw new AutomationException($"Command \"{command.Code}\" can't run in the current state.");

			await command.ExecuteAsync(p["parameter"]?.GetValue<string>());
			return new JsonObject { ["executed"] = command.Code.ToString() };
		}

		private static async Task<JsonNode?> ContextMenuAsync(JsonObject p)
		{
			var pane = ActivePane();
			var shellViewModel = pane.ShellViewModel
				?? throw new AutomationException("The active pane has no folder view.");
			var commandsViewModel = pane.SlimContentPage?.CommandsViewModel
				?? throw new AutomationException("The active pane has no item view.");
			var background = p["names"] is null;
			var items = background ? [] : FindItems(p);
			if (!background && items.Count == 0)
				throw new AutomationException("\"names\" matched no items.");

			List<ContextMenuFlyoutItemViewModel> filesItems;
			if (background)
			{
				// As the right-click on empty space does.
				pane.SlimContentPage!.ItemManipulationModel.ClearSelection();
				var currentFolder = shellViewModel.CurrentFolder
					?? throw new AutomationException("The current folder is not available.");
				filesItems = ContentPageContextFlyoutFactory.GetItemContextCommandsWithoutShellItems(
					pane.InstanceViewModel, [currentFolder], commandsViewModel, false, null, shellViewModel);
			}
			else
			{
				var properties = pane.SlimContentPage!.SelectedItemsPropertiesViewModel;
				properties.CheckAllFileExtensions(items.Select(x => x.FileExtension).ToList());
				filesItems = ContentPageContextFlyoutFactory.GetItemContextCommandsWithoutShellItems(
					pane.InstanceViewModel, items, commandsViewModel, false, properties, null);
			}

			var result = new JsonObject { ["files"] = MenuTree(filesItems) };
			if (p["shell"]?.GetValue<bool>() ?? true)
			{
				var shellItems = await ContentPageContextFlyoutFactory.GetItemContextShellCommandsAsync(
					shellViewModel.WorkingDirectory, items, false, false, CancellationToken.None);
				result["shell"] = MenuTree(shellItems);
			}

			return result;
		}

		private static JsonArray MenuTree(IEnumerable<ContextMenuFlyoutItemViewModel> items)
			=> new([.. items.Where(item => item.ShowItem).Select(item => (JsonNode)new JsonObject
			{
				["text"] = item.Text,
				["id"] = item.ID,
				["type"] = item.ItemType.ToString(),
				["primary"] = item.IsPrimary,
				["enabled"] = item.IsEnabled,
				["hidden"] = item.IsHidden,
				["items"] = item.Items is { Count: > 0 } children ? MenuTree(children) : null,
			})]);

		private static JsonNode NewMenu()
		{
			var entries = Ioc.Default.GetRequiredService<IAddItemService>().GetEntries() ?? [];
			return new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
			{
				["name"] = entry.Name,
				["extension"] = entry.Extension,
			})]);
		}

		private static JsonNode Sidebar()
		{
			var sidebar = Ioc.Default.GetRequiredService<SidebarViewModel>();
			return SidebarTree(sidebar.SidebarItems as IEnumerable<INavigationControlItem> ?? []);

			static JsonArray SidebarTree(IEnumerable<INavigationControlItem> items)
				=> new([.. items.ToList().Select(item => (JsonNode)new JsonObject
				{
					["text"] = item.Text,
					["path"] = item.Path,
					["section"] = item.Section.ToString(),
					["type"] = item.ItemType.ToString(),
					["items"] = item.Children is IEnumerable<INavigationControlItem> children ? SidebarTree(children) : null,
				})]);
		}

		private static async Task<JsonNode?> OpenTabAsync(JsonObject p)
		{
			var path = p["path"]?.GetValue<string>();
			if (path is null)
				await NavigationHelpers.AddNewTabAsync();
			else
				await NavigationHelpers.OpenPathInNewTab(path, true);

			return State();
		}

		private static JsonNode SelectTab(JsonObject p)
		{
			App.AppModel.TabStripSelectedIndex = TabAt(p).Index;
			return State();
		}

		private static JsonNode CloseTab(JsonObject p)
		{
			var control = Ioc.Default.GetRequiredService<IMultitaskingContext>().Control
				?? throw new AutomationException("The tab bar is not available.");
			control.CloseTab(TabAt(p).Tab);
			return State();
		}

		private static JsonNode GetSettings(JsonObject p)
		{
			var settings = JsonNode.Parse(Ioc.Default.GetRequiredService<IUserSettingsService>().ExportSettings() as string ?? "{}") as JsonObject ?? [];
			if (p["keys"] is not JsonArray keys)
				return settings;

			var selected = new JsonObject();
			foreach (var key in keys.Select(k => k!.GetValue<string>()))
				selected[key] = settings[key]?.DeepClone();

			return selected;
		}

		private static JsonNode SetSettings(JsonObject p)
		{
			if (p["values"] is not JsonObject values)
				throw new AutomationException("\"values\" must be an object of setting names to values.");

			// ImportSettings replaces the whole file, so merge the changes into the current settings first.
			var service = Ioc.Default.GetRequiredService<IUserSettingsService>();
			var settings = JsonNode.Parse(service.ExportSettings() as string ?? "{}") as JsonObject ?? [];
			foreach (var (key, value) in values)
				settings[key] = value?.DeepClone();

			if (!service.ImportSettings(settings.ToJsonString()))
				throw new AutomationException("The settings could not be imported.");

			return GetSettings(new JsonObject { ["keys"] = new JsonArray([.. values.Select(v => (JsonNode)v.Key)]) });
		}

		private static JsonNode ShowWindow(bool show)
		{
			var window = MainWindow.Instance;
			AutomationServer.SetWindowCloaked(window, !show);
			if (show)
				window.Activate();

			return State();
		}

		private static JsonNode? Quit()
		{
			// Close after this response goes out.
			MainWindow.Instance.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, MainWindow.Instance.Close);
			return new JsonObject { ["closing"] = true };
		}

		private static IShellPage ActivePane()
			=> Ioc.Default.GetRequiredService<IMultiPanesContext>().ActivePane
				?? throw new AutomationException("There is no active pane yet; call \"ping\" until ready is true.");

		private static List<ListedItem> FindItems(JsonObject p)
		{
			var shellViewModel = ActivePane().ShellViewModel
				?? throw new AutomationException("The active pane has no folder view.");
			var names = (p["names"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList();
			var items = shellViewModel.FilesAndFolders.ToList()
				.Where(item => names.Contains(item.ItemNameRaw ?? string.Empty, StringComparer.OrdinalIgnoreCase))
				.ToList();

			var missing = names.Where(name => !items.Any(item => string.Equals(item.ItemNameRaw, name, StringComparison.OrdinalIgnoreCase))).ToList();
			if (missing.Count > 0)
				throw new AutomationException($"Not in the current folder: {string.Join(", ", missing)}");

			return items;
		}

		private static (int Index, TabBarItem Tab) TabAt(JsonObject p)
		{
			var index = p["index"]?.GetValue<int>()
				?? throw new AutomationException("\"index\" is required.");
			if (index < 0 || index >= MainPageViewModel.AppInstances.Count)
				throw new AutomationException($"No tab at index {index}; there are {MainPageViewModel.AppInstances.Count}.");

			return (index, MainPageViewModel.AppInstances[index]);
		}

		private static string RequiredString(JsonObject p, string name)
			=> p[name]?.GetValue<string>() is { Length: > 0 } value
				? value
				: throw new AutomationException($"\"{name}\" is required.");

		private static bool PathEquals(string? a, string b)
			=> a is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

		private static async Task WaitUntilAsync(TimeSpan timeout, Func<bool> condition, string timeoutMessage)
		{
			var deadline = DateTime.UtcNow + timeout;
			while (!condition())
			{
				if (DateTime.UtcNow > deadline)
					throw new AutomationException($"{timeoutMessage} within {timeout.TotalSeconds:0.#} s.");

				await Task.Delay(100);
			}
		}
	}
}
