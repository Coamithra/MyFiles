// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Contracts
{
	/// <summary>
	/// The sidebar's Bookmarks tree, shared by every Files window through its own file. Call members on the UI thread.
	/// </summary>
	public interface IBookmarksService
	{
		/// <summary>
		/// Raised on the UI thread after the tree changed, here or in another window.
		/// </summary>
		event EventHandler? BookmarksChanged;

		/// <summary>
		/// The top level of the tree. Replaced, never mutated, when the tree changes.
		/// </summary>
		IReadOnlyList<BookmarkNode> Roots { get; }

		BookmarkNode? Find(string id);

		/// <summary>
		/// The group holding the node, or null when the node is at the top level or unknown.
		/// </summary>
		BookmarkNode? FindParent(string id);

		bool IsBookmarked(string path);

		/// <summary>
		/// Adds folder bookmarks to a group (null for the top level) at an index (-1 appends). Returns the ids of the added bookmarks.
		/// </summary>
		IReadOnlyList<string> AddBookmarks(IEnumerable<string> paths, string? parentId = null, int index = -1, bool skipExisting = false);

		string? AddGroup(string name, string? parentId = null, int index = -1);

		/// <summary>
		/// Renames a group or bookmark; an empty name resets a bookmark to its folder's name.
		/// </summary>
		bool Rename(string id, string? name);

		bool Remove(string id);

		/// <summary>
		/// Moves a node into a group (null for the top level). The index is in the target list before the node is taken out of it; -1 appends.
		/// </summary>
		bool Move(string id, string? parentId, int index);

		void SetExpanded(string id, bool isExpanded);
	}
}
