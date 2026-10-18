// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Windows.Storage;

namespace Files.App.Services
{
	[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
	[JsonSerializable(typeof(List<BookmarkNode>))]
	internal sealed partial class BookmarksJsonSerializationContext : JsonSerializerContext
	{
	}

	/// <inheritdoc cref="IBookmarksService"/>
	/// <remarks>
	/// Every Files window is its own process, so the tree lives in its own file rather than in the per-process cached user settings. Each change re-reads the file under a package-wide mutex, applies itself and replaces the file; a watcher picks up other windows' changes.
	/// </remarks>
	internal sealed class BookmarksService : IBookmarksService
	{
		private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(2);
		private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

		private readonly string folderPath;
		private readonly string filePath;
		private readonly Mutex mutex;

		private List<BookmarkNode> roots = [];
		// The file text this process last read or wrote; the watcher ignores changes that leave the file at this text, such as our own writes.
		private string? lastText;
		private FileSystemWatcher? watcher;
		private DispatcherQueueTimer? reloadTimer;

		public event EventHandler? BookmarksChanged;

		public IReadOnlyList<BookmarkNode> Roots => roots;

		public BookmarksService()
		{
			folderPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.LocalSettings.SettingsFolderName);
			filePath = Path.Combine(folderPath, Constants.LocalSettings.BookmarksFileName);
			mutex = new Mutex(false, $@"Local\{GetPackageFamilyName()}.Bookmarks");

			if (ReadFile() is { } snapshot)
			{
				roots = snapshot.Roots;
				lastText = snapshot.Text;
			}

			StartWatching();
		}

		/// <summary>
		/// Whether the path can be bookmarked: a rooted filesystem path outside archives and FTP.
		/// </summary>
		public static bool CanBookmark([NotNullWhen(true)] string? path)
			=> !string.IsNullOrWhiteSpace(path) &&
				Path.IsPathRooted(path) &&
				!ZipStorageFolder.IsZipPath(path) &&
				!FtpHelpers.IsFtpPath(path);

		public BookmarkNode? Find(string id)
			=> Locate(roots, id)?.Node;

		public BookmarkNode? FindParent(string id)
			=> Locate(roots, id)?.Parent;

		public bool IsBookmarked(string path)
		{
			var normalized = NormalizePath(path);
			return Enumerate(roots).Any(node => node.Path is not null && string.Equals(NormalizePath(node.Path), normalized, StringComparison.OrdinalIgnoreCase));
		}

		public IReadOnlyList<string> AddBookmarks(IEnumerable<string> paths, string? parentId = null, int index = -1, bool skipExisting = false)
		{
			var added = new List<string>();
			var requested = paths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			if (requested.Count == 0)
				return added;

			Mutate(tree =>
			{
				added.Clear();
				if (TargetList(tree, parentId) is not { } target)
					return false;

				var existing = skipExisting
					? Enumerate(tree).Where(node => node.Path is not null).Select(node => NormalizePath(node.Path!)).ToHashSet(StringComparer.OrdinalIgnoreCase)
					: [];
				var insertAt = ClampIndex(index, target.Count);
				foreach (var path in requested)
				{
					if (existing.Contains(path))
						continue;

					var node = new BookmarkNode { Path = path };
					target.Insert(insertAt++, node);
					added.Add(node.Id);
				}

				return added.Count > 0;
			});

			return added;
		}

		public string? AddGroup(string name, string? parentId = null, int index = -1)
		{
			string? id = null;
			Mutate(tree =>
			{
				if (TargetList(tree, parentId) is not { } target)
					return false;

				var node = new BookmarkNode { Name = name, IsExpanded = true, Children = [] };
				target.Insert(ClampIndex(index, target.Count), node);
				id = node.Id;
				return true;
			});

			return id;
		}

		public bool Rename(string id, string? name)
		{
			return Mutate(tree =>
			{
				if (Locate(tree, id) is not { } found)
					return false;

				var node = found.Node;
				var newName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
				// Groups always keep a name.
				if ((node.IsGroup && newName is null) || node.Name == newName)
					return false;

				node.Name = newName;
				return true;
			});
		}

		public bool Remove(string id)
		{
			return Mutate(tree => Locate(tree, id) is { } found && found.List.Remove(found.Node));
		}

		public bool Move(string id, string? parentId, int index)
		{
			return Mutate(tree =>
			{
				if (Locate(tree, id) is not { } found || TargetList(tree, parentId) is not { } target)
					return false;

				// A group can't go into itself or its own subtree.
				if (parentId is not null && (parentId == id || Locate(found.Node.Children ?? [], parentId) is not null))
					return false;

				var oldIndex = found.List.IndexOf(found.Node);
				var newIndex = ClampIndex(index, target.Count);
				if (ReferenceEquals(found.List, target))
				{
					if (newIndex > oldIndex)
						newIndex--;
					if (newIndex == oldIndex)
						return false;
				}

				found.List.RemoveAt(oldIndex);
				target.Insert(Math.Min(newIndex, target.Count), found.Node);
				return true;
			});
		}

		public void SetExpanded(string id, bool isExpanded)
		{
			Mutate(tree =>
			{
				if (Locate(tree, id) is not { Node.IsGroup: true } found || found.Node.IsExpanded == isExpanded)
					return false;

				found.Node.IsExpanded = isExpanded;
				return true;
			});
		}

		// Applies a change to the file's current content, so concurrent windows never lose each other's edits. Returns whether the change was written.
		private bool Mutate(Func<List<BookmarkNode>, bool> change)
		{
			Snapshot? adopted = null;
			var written = false;
			var owned = false;
			try
			{
				try
				{
					owned = mutex.WaitOne(MutexTimeout);
				}
				// The previous owner died mid-change; the file is still whole because it is only ever replaced by a move.
				catch (AbandonedMutexException)
				{
					owned = true;
				}

				if (!owned)
				{
					App.Logger.LogWarning("Bookmarks: another window held the bookmarks lock for too long; the change was dropped");
					return false;
				}

				if (ReadFile() is not { } snapshot)
					return false;

				if (!change(snapshot.Roots))
				{
					adopted = snapshot.Text != lastText ? snapshot : null;
				}
				else
				{
					if (snapshot.IsCorrupt && !BackUpCorruptFile())
						return false;

					var text = JsonSerializer.Serialize(snapshot.Roots, BookmarksJsonSerializationContext.Default.ListBookmarkNode);
					WriteFile(text);
					adopted = snapshot with { Text = text };
					written = true;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.LogWarning(ex, "Bookmarks: failed to save {Path}", LogPathHelper.RedactPath(filePath));
				return false;
			}
			finally
			{
				if (owned)
					mutex.ReleaseMutex();
			}

			if (adopted is not null)
				Adopt(adopted);

			return written;
		}

		private void Adopt(Snapshot snapshot)
		{
			roots = snapshot.Roots;
			lastText = snapshot.Text;
			BookmarksChanged?.Invoke(this, EventArgs.Empty);
		}

		private sealed record Snapshot(List<BookmarkNode> Roots, string? Text, bool IsCorrupt);

		// Null when the file exists but can't be read right now; a missing file is an empty tree, a corrupt one an empty tree flagged for backup.
		private Snapshot? ReadFile()
		{
			string text;
			try
			{
				if (!File.Exists(filePath))
					return new([], null, false);

				text = File.ReadAllText(filePath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.LogDebug(ex, "Bookmarks: couldn't read {Path}", LogPathHelper.RedactPath(filePath));
				return null;
			}

			if (string.IsNullOrWhiteSpace(text))
				return new([], text, false);

			try
			{
				var list = JsonSerializer.Deserialize(text, BookmarksJsonSerializationContext.Default.ListBookmarkNode) ?? [];
				return new(Sanitize(list, []), text, false);
			}
			catch (JsonException ex)
			{
				if (text != lastText)
					App.Logger.LogWarning(ex, "Bookmarks: {Path} is not valid; showing no bookmarks until it is fixed", LogPathHelper.RedactPath(filePath));
				return new([], text, true);
			}
		}

		private void WriteFile(string text)
		{
			Directory.CreateDirectory(folderPath);
			var tempPath = filePath + ".tmp";
			File.WriteAllText(tempPath, text);
			File.Move(tempPath, filePath, overwrite: true);
		}

		private bool BackUpCorruptFile()
		{
			try
			{
				File.Copy(filePath, filePath + ".bak", overwrite: true);
				App.Logger.LogWarning("Bookmarks: kept the unreadable file as {Path}.bak before replacing it", LogPathHelper.RedactPath(filePath));
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.LogWarning(ex, "Bookmarks: couldn't back up the unreadable {Path}; leaving it untouched", LogPathHelper.RedactPath(filePath));
				return false;
			}
		}

		// Drops null entries, gives every node a unique id and makes groups and bookmarks well-formed, so hand edits can't break the sidebar.
		private static List<BookmarkNode> Sanitize(IEnumerable<BookmarkNode?> list, HashSet<string> seenIds)
		{
			var result = new List<BookmarkNode>();
			foreach (var node in list)
			{
				if (node is null)
					continue;

				if (string.IsNullOrEmpty(node.Id) || !seenIds.Add(node.Id))
				{
					node.Id = BookmarkNode.NewId();
					seenIds.Add(node.Id);
				}

				if (string.IsNullOrWhiteSpace(node.Path))
				{
					node.Path = null;
					node.Name ??= string.Empty;
					node.Children = Sanitize(node.Children ?? [], seenIds);
				}
				else
				{
					node.Children = null;
					node.IsExpanded = false;
				}

				result.Add(node);
			}

			return result;
		}

		private void StartWatching()
		{
			try
			{
				Directory.CreateDirectory(folderPath);
				watcher = new FileSystemWatcher(folderPath, Constants.LocalSettings.BookmarksFileName)
				{
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
				};
				watcher.Created += OnFileEvent;
				watcher.Changed += OnFileEvent;
				watcher.Deleted += OnFileEvent;
				watcher.Renamed += OnFileEvent;
				watcher.EnableRaisingEvents = true;
			}
			// Without a watcher this window only sees other windows' changes on its next own change.
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Bookmarks: couldn't watch {Path}", LogPathHelper.RedactPath(filePath));
				watcher?.Dispose();
				watcher = null;
			}
		}

		// Fires on a thread-pool thread, several times per write; debounce on the UI thread.
		private void OnFileEvent(object sender, FileSystemEventArgs e)
		{
			var dispatcher = MainWindow.Instance?.DispatcherQueue;
			dispatcher?.TryEnqueue(() =>
			{
				if (reloadTimer is null)
				{
					reloadTimer = dispatcher.CreateTimer();
					reloadTimer.Interval = ReloadDebounce;
					reloadTimer.IsRepeating = false;
					reloadTimer.Tick += (_, _) => Reload();
				}

				reloadTimer.Stop();
				reloadTimer.Start();
			});
		}

		private void Reload()
		{
			if (ReadFile() is { } snapshot && snapshot.Text != lastText)
				Adopt(snapshot);
		}

		private static string GetPackageFamilyName()
		{
			try
			{
				return Windows.ApplicationModel.Package.Current.Id.FamilyName;
			}
			// Unpackaged runs have no package identity.
			catch (InvalidOperationException)
			{
				return "Files";
			}
		}

		private static string NormalizePath(string path)
			=> Path.TrimEndingDirectorySeparator(path.Trim());

		private static int ClampIndex(int index, int count)
			=> index < 0 || index > count ? count : index;

		private static List<BookmarkNode>? TargetList(List<BookmarkNode> tree, string? parentId)
		{
			if (parentId is null)
				return tree;

			return Locate(tree, parentId) is { Node.IsGroup: true } found ? found.Node.Children ??= [] : null;
		}

		private static (BookmarkNode Node, List<BookmarkNode> List, BookmarkNode? Parent)? Locate(List<BookmarkNode> list, string id, BookmarkNode? parent = null)
		{
			foreach (var node in list)
			{
				if (node.Id == id)
					return (node, list, parent);

				if (node.Children is { } children && Locate(children, id, node) is { } found)
					return found;
			}

			return null;
		}

		private static IEnumerable<BookmarkNode> Enumerate(IEnumerable<BookmarkNode> list)
		{
			foreach (var node in list)
			{
				yield return node;
				if (node.Children is { } children)
				{
					foreach (var child in Enumerate(children))
						yield return child;
				}
			}
		}
	}
}
