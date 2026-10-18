// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Controls
{
	/// <summary>
	/// A sidebar item that can be rearranged by drag and drop inside the sidebar. Its rows report Top/Center/Bottom drop positions and can be dragged even without a filesystem path.
	/// </summary>
	public interface ISidebarReorderableItem
	{
		/// <summary>
		/// Identifies the item in the drag data, under <see cref="SidebarReorderData.Format"/>.
		/// </summary>
		string ReorderId { get; }
	}

	public static class SidebarReorderData
	{
		public const string Format = "Files.App.SidebarReorderId";
	}
}
