// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using System.Text.RegularExpressions;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Everything matches for the address bar's live suggestions: direct children of each scope folder first, then the rest of its subtree.
	/// </summary>
	internal static partial class EverythingSuggestions
	{
		// Pages larger than the shown count so enough items survive the visibility filter
		private const uint DirectChildrenPageSize = 16;
		private const uint SubtreePageSize = 48;

		// Everything syntax or a path; such queries are ranked by depth only
		private static readonly char[] QuerySyntaxChars = [':', '*', '?', '"', '<', '>', '|', '\\', '/'];

		// Content functions read file contents, far too slow to run per keystroke
		[GeneratedRegex(@"(?:^|[\s(!<|-])(?:ansi|utf8|utf16|utf16be)?content:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex ContentFunctionRegex();

		public static bool IsSuggestableQuery(string query)
			=> FolderSearch.IsEverythingQuery(query) && !ContentFunctionRegex().IsMatch(query);

		/// <summary>
		/// Runs the queries on a dedicated thread. <paramref name="scopeFolders"/> null searches the whole index.
		/// Returns null when Everything isn't available, a query fails or is canceled, or a scope folder can't be served.
		/// </summary>
		public static Task<IReadOnlyList<EverythingSuggestion>?> QueryAsync(string query, IReadOnlyList<string>? scopeFolders, int maxCount, CancellationToken token)
		{
			var settings = Ioc.Default.GetRequiredService<IUserSettingsService>().FoldersSettingsService;
			var filter = new VisibilityFilter(settings.ShowHiddenItems, settings.ShowProtectedSystemFiles, settings.ShowDotFiles);

			return Task.Factory.StartNew(
				() => Query(query, scopeFolders, maxCount, filter, token),
				token,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default);
		}

		private static IReadOnlyList<EverythingSuggestion>? Query(string query, IReadOnlyList<string>? scopeFolders, int maxCount, VisibilityFilter filter, CancellationToken token)
		{
			string?[] scopes = scopeFolders is null ? [null] : [.. scopeFolders.Select(folder => Path.TrimEndingDirectorySeparator(folder))];
			if (scopes.Any(scope => scope is not null && FolderSearch.RequiresFindTimeout(scope)))
				return null;

			using var client = EverythingIpcClient.TryCreate();
			if (client is null)
				return null;

			var term = GetRankingTerm(query);
			var direct = new List<Candidate>();
			var prefixed = new List<Candidate>();
			var subtree = new List<Candidate>();
			foreach (var scope in scopes)
			{
				if (scope is not null && !TryAdd(direct, $"parent:\"{scope}\" {query}", DirectChildrenPageSize, scope))
					return null;

				// The pages come sorted by name, so with many matches the obvious ones can fall outside them
				if (term is not null && !TryAdd(prefixed, EverythingSearch.BuildQuery($"startwith:\"{term}\"", scope), SubtreePageSize, scope))
					return null;

				if (!TryAdd(subtree, EverythingSearch.BuildQuery(query, scope), SubtreePageSize, scope))
					return null;
			}

			var ranked = direct
				.Select((candidate, index) => (candidate, index))
				.OrderBy(x => GetMatchRank(x.candidate.Path, term))
				.ThenBy(x => x.index)
				.Concat(RankByDepth(prefixed))
				.Concat(RankByDepth(subtree))
				.Select(x => x.candidate);

			var results = new List<EverythingSuggestion>(maxCount);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var candidate in ranked)
			{
				if (token.IsCancellationRequested || results.Count >= maxCount)
					break;

				if (seen.Add(candidate.Path) && filter.IsVisible(candidate, out var isFolder))
					results.Add(new(candidate.Path, isFolder, candidate.Scope));
			}

			return token.IsCancellationRequested ? null : results;

			bool TryAdd(List<Candidate> candidates, string search, uint pageSize, string? scope)
			{
				if (token.IsCancellationRequested || !client.TryQuery(search, 0, pageSize, token, out var page))
					return false;

				candidates.AddRange(page.Items.Where(item => !item.IsRoot).Select(item => CreateCandidate(item.Path, scope)));
				return true;
			}

			IEnumerable<(Candidate candidate, int index)> RankByDepth(List<Candidate> candidates)
				=> candidates
					.Select((candidate, index) => (candidate, index))
					.OrderBy(x => GetMatchRank(x.candidate.Path, term))
					.ThenBy(x => GetDepth(x.candidate))
					.ThenBy(x => x.index);
		}

		private static string? GetRankingTerm(string query)
		{
			var term = query.Trim();
			return term.Length == 0 || term.Any(char.IsWhiteSpace) || term.IndexOfAny(QuerySyntaxChars) >= 0 ? null : term;
		}

		private static int GetMatchRank(string path, string? term)
		{
			if (term is null)
				return 0;

			var name = Path.GetFileName(path);
			if (name.Equals(term, StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileNameWithoutExtension(name).Equals(term, StringComparison.OrdinalIgnoreCase))
				return 0;

			return name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
		}

		private static int GetDepth(Candidate candidate)
			=> candidate.Path.AsSpan(candidate.Scope?.Length ?? 0).Count(Path.DirectorySeparatorChar);

		// Everything reports indexed paths, which differ from the scope when it was reached through a link
		private static Candidate CreateCandidate(string path, string? scope)
			=> scope is not null &&
				path.Length > scope.Length &&
				path.StartsWith(scope, StringComparison.OrdinalIgnoreCase) &&
				(Path.EndsInDirectorySeparator(scope) || path[scope.Length] == Path.DirectorySeparatorChar)
					? new(path, scope)
					: new(path, null);

		private readonly record struct Candidate(string Path, string? Scope);

		/// <summary>
		/// Applies the hidden, protected system and dot file settings to an item and to its folders below the scope,
		/// since Everything doesn't index attributes. Stat'ing also drops items deleted since indexing.
		/// </summary>
		private sealed class VisibilityFilter(bool showHiddenItems, bool showProtectedSystemFiles, bool showDotFiles)
		{
			private readonly Dictionary<string, bool> folderVisibility = new(StringComparer.OrdinalIgnoreCase);
			private readonly Dictionary<string, bool> servableRoots = new(StringComparer.OrdinalIgnoreCase);

			public bool IsVisible(Candidate candidate, out bool isFolder)
			{
				isFolder = false;
				var path = candidate.Path;
				var root = Path.GetPathRoot(path);
				if (string.IsNullOrEmpty(root))
					return false;

				// Drives that would need a find timeout are skipped rather than waited on
				if (!servableRoots.TryGetValue(root, out var servable))
					servableRoots[root] = servable = !FolderSearch.RequiresFindTimeout(root);

				if (!servable || !TryStat(path, out var attributes))
					return false;

				isFolder = attributes.HasFlag(FileAttributes.Directory);
				if (!IsListed(Path.GetFileName(path), attributes))
					return false;

				var start = candidate.Scope is { } scope ? scope.Length + 1 : root.Length;
				for (var index = path.IndexOf(Path.DirectorySeparatorChar, start); index > 0; index = path.IndexOf(Path.DirectorySeparatorChar, index + 1))
				{
					var folder = path[..index];
					if (!folderVisibility.TryGetValue(folder, out var visible))
						folderVisibility[folder] = visible = TryStat(folder, out var folderAttributes) && IsListed(Path.GetFileName(folder), folderAttributes);

					if (!visible)
						return false;
				}

				return true;
			}

			private bool IsListed(string name, FileAttributes attributes)
			{
				var isHidden = attributes.HasFlag(FileAttributes.Hidden);
				var isSystem = attributes.HasFlag(FileAttributes.System);

				return (!isHidden || (showHiddenItems && (!isSystem || showProtectedSystemFiles))) &&
					(!name.StartsWith('.') || showDotFiles);
			}

			private static bool TryStat(string path, out FileAttributes attributes)
			{
				using var findHandle = FolderSearch.StatPath(path, out var findData);
				attributes = (FileAttributes)findData.dwFileAttributes;
				return !findHandle.IsInvalid;
			}
		}
	}

	internal readonly record struct EverythingSuggestion(string Path, bool IsFolder, string? ScopeFolder);
}
