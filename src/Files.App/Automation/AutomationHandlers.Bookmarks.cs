// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using System.Text.Json.Nodes;

namespace Files.App.Automation
{
	internal static partial class AutomationHandlers
	{
		private static IBookmarksService BookmarkStore()
			=> Ioc.Default.GetRequiredService<IBookmarksService>();

		private static JsonNode Bookmarks()
		{
			return BookmarkTree(BookmarkStore().Roots);

			static JsonArray BookmarkTree(IEnumerable<BookmarkNode> nodes)
				=> new([.. nodes.Select(node => (JsonNode)new JsonObject
				{
					["id"] = node.Id,
					["name"] = node.Name,
					["path"] = node.Path,
					["group"] = node.IsGroup,
					["expanded"] = node.IsGroup ? node.IsExpanded : null,
					["items"] = node.Children is { } children ? BookmarkTree(children) : null,
				})]);
		}

		private static JsonNode AddBookmark(JsonObject p)
		{
			var path = RequiredString(p, "path");
			if (!BookmarksService.CanBookmark(path))
				throw new AutomationException($"\"{path}\" can't be bookmarked.");

			var parent = OptionalGroupId(p);
			var added = BookmarkStore().AddBookmarks((string[])[path], parent, p["index"]?.GetValue<int>() ?? -1);
			if (added.Count == 0)
				throw new AutomationException("The bookmark was not added.");

			var name = p["name"]?.GetValue<string>();
			if (!string.IsNullOrEmpty(name))
				BookmarkStore().Rename(added[0], name);

			return new JsonObject { ["id"] = added[0], ["bookmarks"] = Bookmarks() };
		}

		private static JsonNode AddBookmarkGroup(JsonObject p)
		{
			var name = RequiredString(p, "name");
			var id = BookmarkStore().AddGroup(name, OptionalGroupId(p), p["index"]?.GetValue<int>() ?? -1)
				?? throw new AutomationException("The group was not added.");

			return new JsonObject { ["id"] = id, ["bookmarks"] = Bookmarks() };
		}

		private static JsonNode RenameBookmark(JsonObject p)
		{
			var id = RequiredBookmarkId(p, "id");
			var renamed = BookmarkStore().Rename(id, p["name"]?.GetValue<string>());
			return new JsonObject { ["renamed"] = renamed, ["bookmarks"] = Bookmarks() };
		}

		private static JsonNode MoveBookmark(JsonObject p)
		{
			var id = RequiredBookmarkId(p, "id");
			var target = p["target"] is null ? null : RequiredBookmarkId(p, "target");
			var position = (p["position"]?.GetValue<string>() ?? "into") switch
			{
				"before" => SidebarItemDropPosition.Top,
				"after" => SidebarItemDropPosition.Bottom,
				"into" => SidebarItemDropPosition.Center,
				var other => throw new AutomationException($"\"position\" must be before, after or into, not \"{other}\"."),
			};

			// The same placement rules as a drop in the sidebar.
			var drop = Ioc.Default.GetRequiredService<SidebarViewModel>().ResolveBookmarkDrop(target, position, id)
				?? throw new AutomationException("The sidebar would not accept that drop.");
			var moved = BookmarkStore().Move(id, drop.ParentId, drop.Index);

			return new JsonObject { ["moved"] = moved, ["bookmarks"] = Bookmarks() };
		}

		private static JsonNode RemoveBookmark(JsonObject p)
		{
			var removed = BookmarkStore().Remove(RequiredBookmarkId(p, "id"));
			return new JsonObject { ["removed"] = removed, ["bookmarks"] = Bookmarks() };
		}

		private static string RequiredBookmarkId(JsonObject p, string name)
		{
			var id = RequiredString(p, name);
			return BookmarkStore().Find(id) is null
				? throw new AutomationException($"No bookmark or group has the id \"{id}\".")
				: id;
		}

		private static string? OptionalGroupId(JsonObject p)
		{
			if (p["parent"] is null)
				return null;

			var id = RequiredBookmarkId(p, "parent");
			return BookmarkStore().Find(id) is { IsGroup: true }
				? id
				: throw new AutomationException($"\"{id}\" is a bookmark, not a group.");
		}
	}
}
