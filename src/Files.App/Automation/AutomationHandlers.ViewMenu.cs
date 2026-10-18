// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Files.App.Automation
{
	internal static partial class AutomationHandlers
	{
		// Reads the same LayoutMenuVisibility the Layout flyout binds to, so the report can't drift from the XAML.
		private static JsonNode LayoutMenu()
		{
			var menu = LayoutMenuVisibility.Instance;
			return new JsonObject
			{
				["button"] = menu.IsAnyEntryShown,
				["activeLayout"] = Ioc.Default.GetRequiredService<IDisplayPageContext>().LayoutType.ToString(),
				["entries"] = new JsonArray([.. LayoutMenuVisibility.EntryIds.Select(id => (JsonNode)new JsonObject
				{
					["id"] = id,
					["label"] = LayoutMenuVisibility.GetLabel(id),
					["hidden"] = menu.IsEntryHidden(id),
					["shown"] = menu.IsEntryShown(id),
				})]),
				["sections"] = new JsonObject
				{
					["layout"] = menu.ShowLayoutSection,
					["sizeDivider"] = menu.ShowSizeDivider,
					["size"] = menu.ShowSizeSection,
					["togglesDivider"] = menu.ShowTogglesDivider,
					["toggles"] = menu.ShowTogglesSection,
				},
			};
		}
	}
}
