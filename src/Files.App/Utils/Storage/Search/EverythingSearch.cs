// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Entry point to voidtools Everything for features that want raw paths. Use <see cref="FolderSearch"/> for <see cref="ListedItem"/> results.
	/// </summary>
	internal static class EverythingSearch
	{
		public static EverythingStatus GetStatus()
			=> EverythingIpcClient.GetStatus();

		/// <summary>
		/// Limits <paramref name="query"/> to <paramref name="scopeFolder"/> and its subfolders.
		/// </summary>
		public static string BuildQuery(string query, string? scopeFolder)
		{
			if (string.IsNullOrEmpty(scopeFolder))
				return query;

			// A quoted term containing a backslash matches the full path; the trailing one excludes the folder itself
			// and siblings sharing its name as a prefix.
			var scope = $"\"{scopeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\\\"";
			return string.IsNullOrWhiteSpace(query) ? scope : $"{scope} {query}";
		}

		/// <summary>
		/// Runs one query on a dedicated thread. Returns null when Everything isn't available or the query fails.
		/// </summary>
		public static Task<EverythingQueryResult?> QueryAsync(string search, uint maxResults, CancellationToken token, uint offset = 0)
		{
			return Task.Factory.StartNew(
				() =>
				{
					using var client = EverythingIpcClient.TryCreate();
					return client is not null && client.TryQuery(search, offset, maxResults, token, out var result) ? result : null;
				},
				token,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default);
		}
	}
}
