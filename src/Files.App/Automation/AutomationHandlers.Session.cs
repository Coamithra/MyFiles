// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Files.App.Automation
{
	internal static partial class AutomationHandlers
	{
		private static JsonNode SessionWindows()
		{
			var dryRun = WindowSessionManager.DryRunColdStart();

			return new JsonObject
			{
				["windowId"] = WindowSessionManager.WindowId,
				["pid"] = Environment.ProcessId,
				["enabled"] = WindowSessionManager.IsEnabled,
				["startup"] = new JsonObject
				{
					["kind"] = WindowSessionManager.StartupKind,
					["adopted"] = WindowSessionManager.AdoptedId,
					["siblings"] = new JsonArray([.. WindowSessionManager.LaunchedSiblings.Select(id => (JsonNode?)JsonValue.Create(id))]),
				},
				["folder"] = WindowSessionManager.FolderPath,
				["windows"] = new JsonArray([.. WindowSessionManager.ListSessions().Select(session => (JsonNode)new JsonObject
				{
					["id"] = session.Id,
					["pid"] = session.Pid,
					["status"] = session.Status,
					["savedAt"] = session.SavedAt.ToString("O"),
					["tabCount"] = session.Tabs.Count(tab => !string.IsNullOrEmpty(tab)),
					["selectedIndex"] = session.SelectedIndex,
					["hasPlacement"] = session.HasPlacement,
					["tabs"] = new JsonArray([.. session.Tabs.Select(TabPaths)]),
				})]),
				["coldStart"] = new JsonObject
				{
					["decision"] = dryRun.Decision,
					["reason"] = dryRun.Reason,
					["adopt"] = dryRun.Adopt,
					["launch"] = new JsonArray([.. dryRun.Launch.Select(id => (JsonNode?)JsonValue.Create(id))]),
				},
			};

			static JsonNode TabPaths(string tab)
			{
				if (string.IsNullOrEmpty(tab))
					return new JsonObject { ["empty"] = true };

				try
				{
					return TabBarItemParameter.Deserialize(tab).NavigationParameter switch
					{
						PaneNavigationArguments panes => new JsonObject { ["left"] = panes.LeftPaneNavPathParam, ["right"] = panes.RightPaneNavPathParam },
						string path => new JsonObject { ["left"] = path },
						_ => new JsonObject(),
					};
				}
				catch (Exception ex) when (ex is JsonException or InvalidOperationException)
				{
					return new JsonObject { ["error"] = ex.Message };
				}
			}
		}
	}
}
