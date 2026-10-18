// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Models
{
	/// <summary>
	/// A node of the sidebar's Bookmarks tree, as stored in bookmarks.json: a folder bookmark when <see cref="Path"/> is set, a group otherwise.
	/// </summary>
	public sealed class BookmarkNode
	{
		public string Id { get; set; } = NewId();

		/// <summary>
		/// The group's name, or the bookmark's custom name (null shows the folder's own name).
		/// </summary>
		public string? Name { get; set; }

		public string? Path { get; set; }

		public bool IsExpanded { get; set; }

		public List<BookmarkNode>? Children { get; set; }

		[JsonIgnore]
		public bool IsGroup => Path is null;

		public static string NewId() => Guid.NewGuid().ToString("N");
	}
}
