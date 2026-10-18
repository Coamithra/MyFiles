// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Helpers.ContextFlyouts;
using Files.App.UserControls.TabBar;
using System.IO;
using System.Text.Json;
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
			("contextMenu", "Files' own and shell entries of the right-click menu, as rendered: primary marks the icon row (only when it is enabled), verb is the shell verb. Params: names (items; omit for the background menu), shell (default true)."),
			("newMenu", "Entries of the New submenu that come from the shell (ShellNew); hidden marks the ones hidden in Settings."),
			("sidebar", "Sidebar sections and their items."),
			("search", "Search and report which backends served it. Params: query, path (default: the active pane's folder; Home allowed), max (default 100; 0 = no limit), limit (paths returned, default 20), ui (default false: run the search through the active pane's results page)."),
			("tabs.open", "Open a tab. Params: path (optional; Home when omitted)."),
			("tabs.select", "Select a tab. Params: index."),
			("tabs.close", "Close a tab. Params: index. Closing the last tab closes the window."),
			("history", "Back/forward stacks of every pane, in frame stack order (last back entry = where Back goes). Params: index (tab; all when omitted). pending = restored history not applied yet because the pane has not navigated."),
			("session.endSession", "Run the sign-out handler as if Windows sent WM_QUERYENDSESSION, even in a hidden instance. Returns the restart registration outcome; the registration is undone right after (a dry run)."),
			("settings.get", "Stored user settings, including session keys. Params: keys (array; all when omitted). Never-changed settings are absent (null)."),
			("settings.set", "Change user settings as their setters would. Params: values (object of key to value). Returns which changed."),
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
				"search" => SearchAsync(p),
				"tabs.open" => OpenTabAsync(p),
				"tabs.select" => Done(SelectTab(p)),
				"tabs.close" => Done(CloseTab(p)),
				"history" => Done(History(p)),
				"session.endSession" => Done(EndSession()),
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
				tabs.Add((JsonNode)new JsonObject
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
				items.Add((JsonNode)new JsonObject
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
			if (Path.IsPathFullyQualified(path))
			{
				path = Path.GetFullPath(path);

				// Files reports a missing folder with a dialog, which a hidden window never shows. Archives are files.
				if (!Directory.Exists(path) && !File.Exists(path))
					throw new AutomationException($"\"{path}\" does not exist.");
			}

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
				// Some completions (refreshes) carry no path.
				if (e.Status is ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete && PathEquals(e.Path ?? shellViewModel?.WorkingDirectory, path))
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

				commands.Add((JsonNode)new JsonObject
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
				// Entries read the selection to decide what applies, so select the items as a right-click does.
				pane.SlimContentPage!.ItemManipulationModel.ClearSelection();
				pane.SlimContentPage.ItemManipulationModel.AddSelectedItems(items);
				var properties = pane.SlimContentPage.SelectedItemsPropertiesViewModel;
				properties.CheckAllFileExtensions(items.Select(x => x.FileExtension).ToList());
				filesItems = ContentPageContextFlyoutFactory.GetItemContextCommandsWithoutShellItems(
					pane.InstanceViewModel, items, commandsViewModel, false, properties, null);
			}

			// The real menus skip shell entries on these pages.
			var instance = pane.InstanceViewModel;
			var shellApplies = !instance.IsPageTypeZipFolder && !instance.IsPageTypeFtp && !(background && instance.IsPageTypeSearchResults);

			var result = new JsonObject { ["files"] = MenuTree(filesItems) };
			if (shellApplies && (p["shell"]?.GetValue<bool>() ?? true))
			{
				var shellItems = await ContentPageContextFlyoutFactory.GetItemContextShellCommandsAsync(
					shellViewModel.WorkingDirectory, items, false, false, CancellationToken.None);
				result["shell"] = MenuTree(shellItems);
			}

			return result;
		}

		private static JsonArray MenuTree(IEnumerable<ContextMenuFlyoutItemViewModel> items)
		{
			// Mirror FastContextFlyout.Build: primary commands are icon-row buttons only while the row is enabled.
			var showsIconRow = FastContextFlyout.ShowsIconRow;
			return new([.. items.Where(item => item.ShowItem && !FastContextFlyout.IsOmittedPrimary(item, showsIconRow)).Select(item => (JsonNode)new JsonObject
			{
				["text"] = item.Text,
				["id"] = item.ID,
				["type"] = item.ItemType.ToString(),
				["primary"] = item.IsPrimary && showsIconRow,
				["enabled"] = item.IsEnabled,
				["hidden"] = item.IsHidden,
				["verb"] = (item.Tag as Win32ContextMenuItem)?.CommandString,
				["items"] = item.Items is { Count: > 0 } children ? MenuTree(children) : null,
			})]);
		}

		private static JsonNode NewMenu()
		{
			var entries = Ioc.Default.GetRequiredService<IAddItemService>().GetEntries() ?? [];
			var hidden = UserSettings().GeneralSettingsService.HiddenNewMenuEntries ?? [];
			return new JsonArray([.. entries.Select(entry => (JsonNode)new JsonObject
			{
				["name"] = entry.Name,
				["extension"] = entry.Extension,
				["hidden"] = !string.IsNullOrEmpty(entry.Extension) && hidden.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase),
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

		private static async Task<JsonNode?> SearchAsync(JsonObject p)
		{
			var query = RequiredString(p, "query");
			var path = p["path"]?.GetValue<string>();
			var limit = p["limit"]?.GetValue<int>() ?? 20;
			var everything = await Task.Run(EverythingSearch.GetStatus);
			var stopwatch = Stopwatch.StartNew();

			FolderSearchBackends backends;
			List<ListedItem> results;
			if (p["ui"]?.GetValue<bool>() ?? false)
			{
				if (path is not null)
					await NavigateAsync(new JsonObject { ["path"] = path });

				var pane = ActivePane();
				var shellViewModel = pane.ShellViewModel
					?? throw new AutomationException("The active pane has no folder view.");

				var completed = new TaskCompletionSource();
				void OnItemLoadStatusChanged(object? sender, ItemLoadStatusChangedEventArgs e)
				{
					if (e.Status is ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete && shellViewModel.IsSearchResults)
						completed.TrySetResult();
				}

				shellViewModel.ItemLoadStatusChanged += OnItemLoadStatusChanged;
				try
				{
					pane.SubmitSearch(query);
					await WaitUntilAsync(TimeSpan.FromSeconds(60), () => completed.Task.IsCompleted, $"The search for \"{query}\" did not finish");
				}
				finally
				{
					shellViewModel.ItemLoadStatusChanged -= OnItemLoadStatusChanged;
				}

				backends = FolderSearch.LastResultsPageBackends;
				results = shellViewModel.FilesAndFolders.ToList();
			}
			else
			{
				var folder = path ?? ActivePane().ShellViewModel?.WorkingDirectory
					?? throw new AutomationException("The active pane has no folder; pass \"path\".");
				if (Path.IsPathFullyQualified(folder))
					folder = Path.GetFullPath(folder);

				var search = new FolderSearch
				{
					Query = query,
					Folder = folder,
					MaxItemCount = (uint)(p["max"]?.GetValue<int>() ?? 100),
				};

				results = [];
				await search.SearchAsync(results, CancellationToken.None);
				backends = search.UsedBackends;
			}

			return new JsonObject
			{
				["backends"] = backends.ToString(),
				["count"] = results.Count,
				["elapsedMs"] = stopwatch.ElapsedMilliseconds,
				["everything"] = new JsonObject
				{
					["running"] = everything.IsRunning,
					["databaseLoaded"] = everything.IsDatabaseLoaded,
					["version"] = everything.Version,
				},
				["paths"] = new JsonArray([.. results.Take(limit).Select(item => (JsonNode?)item.ItemPath)]),
			};
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
			var tab = TabAt(p).Tab;

			// Closing the last tab closes the window, so do it after this response goes out.
			if (MainPageViewModel.AppInstances.Count == 1)
			{
				MainWindow.Instance.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => control.CloseTab(tab));
				return new JsonObject { ["closing"] = true };
			}

			control.CloseTab(tab);
			return State();
		}

		private static JsonNode History(JsonObject p)
		{
			var tabs = new JsonArray();
			for (var i = 0; i < MainPageViewModel.AppInstances.Count; i++)
			{
				if (p["index"] is not null && TabAt(p).Index != i)
					continue;

				var panes = new JsonArray();
				if (MainPageViewModel.AppInstances[i].TabItemContent is IShellPanesPage panesPage)
				{
					foreach (var pane in panesPage.GetPanes())
					{
						var (history, isPending) = PaneHistoryHelper.Snapshot(pane);
						panes.Add((JsonNode)new JsonObject
						{
							["path"] = pane.TabBarItemParameter?.NavigationParameter as string ?? pane.NavParams?.NavPath,
							["pending"] = isPending,
							["back"] = new JsonArray([.. (history?.Back ?? []).Select(path => (JsonNode?)path)]),
							["forward"] = new JsonArray([.. (history?.Forward ?? []).Select(path => (JsonNode?)path)]),
						});
					}
				}

				tabs.Add((JsonNode)new JsonObject { ["index"] = i, ["panes"] = panes });
			}

			return tabs;
		}

		private static JsonNode EndSession()
		{
			SessionEndHelper.OnQueryEndSession(ignoreHidden: true);
			var outcome = SessionEndHelper.RestartRegistration;
			SessionEndHelper.ResetRestartRegistration();
			return new JsonObject { ["restartRegistration"] = outcome };
		}

		private static JsonNode GetSettings(JsonObject p)
		{
			// Only settings that were ever changed are stored; the rest read as their defaults and are absent here.
			var all = UserSettings().GetAllValues();
			var keys = OptionalStrings(p, "keys");

			var settings = new JsonObject();
			foreach (var (key, value) in all.OrderBy(kv => kv.Key, StringComparer.Ordinal))
			{
				if (keys is null || keys.Contains(key, StringComparer.Ordinal))
					settings[key] = JsonNode.Parse(value.GetRawText());
			}

			foreach (var missing in keys?.Where(key => !all.ContainsKey(key)) ?? [])
				settings[missing] = null;

			return settings;
		}

		private static JsonNode SetSettings(JsonObject p)
		{
			if (p["values"] is not JsonObject values || values.Count == 0)
				throw new AutomationException("\"values\" must be an object of setting names to values.");

			var service = UserSettings();
			var changed = new JsonArray();
			foreach (var (key, value) in values)
			{
				using var document = JsonDocument.Parse(value?.ToJsonString() ?? "null");
				if (service.SetValueByName(key, document.RootElement.Clone()))
					changed.Add((JsonNode?)JsonValue.Create(key));
			}

			var result = GetSettings(new JsonObject { ["keys"] = new JsonArray([.. values.Select(v => (JsonNode)v.Key)]) });
			return new JsonObject { ["changed"] = changed, ["values"] = result };
		}

		private static UserSettingsService UserSettings()
			=> (UserSettingsService)Ioc.Default.GetRequiredService<IUserSettingsService>();

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
			var names = OptionalStrings(p, "names") ?? [];
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

		/// <summary>
		/// An array of strings, or a single string as a one-item list; null when absent.
		/// </summary>
		private static List<string>? OptionalStrings(JsonObject p, string name)
			=> p[name] switch
			{
				null => null,
				JsonArray array => [.. array.Select(n => n?.GetValue<string>() ?? throw new AutomationException($"\"{name}\" must not contain null."))],
				JsonValue value when value.TryGetValue<string>(out var single) => [single],
				_ => throw new AutomationException($"\"{name}\" must be a string or an array of strings."),
			};

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
