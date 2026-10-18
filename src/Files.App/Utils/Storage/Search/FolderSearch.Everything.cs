// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Utils.Storage
{
	public sealed partial class FolderSearch
	{
		private const uint EverythingFirstPageSize = 500;
		private const uint EverythingPageSize = 5000;

		/// <summary>
		/// The backends that have served this search so far.
		/// </summary>
		public FolderSearchBackends UsedBackends { get; private set; }

		/// <summary>
		/// The backends of the latest unlimited (results page) search, for automation checks.
		/// </summary>
		internal static FolderSearchBackends LastResultsPageBackends { get; private set; }

		// Windows property terms such as System.Kind:music are AQS, which only Windows Search understands
		[GeneratedRegex(@"(?:^|[\s(!-])System(?:\.\w+)+:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
		private static partial Regex WindowsPropertyTermRegex();

		private async Task<bool> TrySearchWithEverythingAsync(string folder, IList<ListedItem> results, CancellationToken token)
		{
			var served = false;
			if (CanSearchWithEverything(folder))
			{
				var countBefore = results.Count;
				try
				{
					served = await Task.Factory.StartNew(
						() => SearchWithEverything(folder, results, token),
						token,
						TaskCreationOptions.LongRunning,
						TaskScheduler.Default)
						.WaitAsync(token);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogWarning(ex, "Everything search failed for \"{Folder}\"", folder);
					served = results.Count != countBefore;
				}
			}

			UsedBackends |= served
				? FolderSearchBackends.Everything
				: IsTagQuery(AQSQuery) ? FolderSearchBackends.FileTags : FolderSearchBackends.WindowsSearch;

			if (MaxItemCount == 0)
				LastResultsPageBackends = UsedBackends;

			return served;
		}

		private bool CanSearchWithEverything(string folder)
		{
			if (!UserSettingsService.GeneralSettingsService.UseEverythingForSearch)
				return false;

			// Tags live in Files' own database; a leading $ explicitly asks for Windows Search AQS
			var query = Query ?? string.Empty;
			if (query.StartsWith('$') || IsTagQuery(AQSQuery) || WindowsPropertyTermRegex().IsMatch(query))
				return false;

			return Path.IsPathFullyQualified(folder)
				&& !DriveHelpers.IsMtpPath(folder)
				&& !FtpHelpers.IsFtpPath(folder)
				&& !ZipStorageFolder.IsZipPath(folder);
		}

		/// <summary>
		/// Returns false, so the caller falls back, when Everything is unavailable, doesn't index the folder,
		/// or fails before any result was added.
		/// </summary>
		private bool SearchWithEverything(string folder, IList<ListedItem> results, CancellationToken token)
		{
			using var client = EverythingIpcClient.TryCreate();
			if (client is null)
				return false;

			var search = EverythingSearch.BuildQuery(Query ?? string.Empty, folder);
			var useFindTimeout = RequiresFindTimeout(folder);
			var showHiddenItems = UserSettingsService.FoldersSettingsService.ShowHiddenItems;
			var showProtectedSystemFiles = UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles;
			var showDotFiles = UserSettingsService.FoldersSettingsService.ShowDotFiles;

			var addedCount = 0;
			uint offset = 0;
			while (!token.IsCancellationRequested)
			{
				var pageSize = Math.Min(offset == 0 ? EverythingFirstPageSize : EverythingPageSize, GetRemainingItemCount(results));
				if (pageSize == 0)
					break;

				if (!client.TryQuery(search, offset, pageSize, token, out var page))
				{
					if (token.IsCancellationRequested)
						return true;

					// Falling back after results were shown would list them twice
					logger.LogWarning("Everything search failed for \"{Folder}\" after {Count} results", folder, addedCount);
					return addedCount > 0;
				}

				// An empty answer is only trusted when Everything indexes this folder at all
				if (offset == 0 && page.Items.Count == 0)
					return client.TryQuery(EverythingSearch.BuildQuery(string.Empty, folder), 0, 1, token, out var probe) && probe.TotalCount > 0;

				foreach (var item in page.Items)
				{
					if (token.IsCancellationRequested || results.Count >= UsedMaxItemCount)
						return true;

					if (item.IsRoot)
						continue;

					try
					{
						if (GetEverythingListedItem(item.Path) is { } listedItem)
						{
							AddResult(results, listedItem, token);
							addedCount++;
						}
					}
					catch (Exception ex)
					{
						App.Logger.LogWarning(ex, "Error creating ListedItem from an Everything result");
					}
				}

				RaiseSearchTickIfDue(token);

				offset += (uint)page.Items.Count;
				if (page.Items.Count == 0 || offset >= page.TotalCount)
					break;
			}

			return true;

			// Everything doesn't index attributes, so stat each result; this also drops items deleted since indexing
			ListedItem? GetEverythingListedItem(string path)
			{
				using var findHandle = StatPath(path, useFindTimeout, out var findData, token);
				if (findHandle is null || findHandle.IsInvalid)
					return null;

				var fileName = findData.cFileName.ToString();
				var attributes = (FileAttributes)findData.dwFileAttributes;
				var isHidden = attributes.HasFlag(FileAttributes.Hidden);
				var isSystem = attributes.HasFlag(FileAttributes.System);

				var shouldBeListed = (!isHidden || (showHiddenItems && (!isSystem || showProtectedSystemFiles))) &&
					(!fileName.StartsWith('.') || showDotFiles);
				if (!shouldBeListed)
					return null;

				return !attributes.HasFlag(FileAttributes.Directory) && FileExtensionHelpers.IsShortcutOrUrlFile(fileName)
					? GetShortcutItem(path, findData)
					: GetListedItemAsync(path, findData);
			}
		}

		private static FindCloseSafeHandle? StatPath(string path, bool useTimeout, out WIN32_FIND_DATAW findData, CancellationToken token)
		{
			if (!useTimeout)
				return StatPath(path, out findData);

			var findTask = Task.Run(() => (Handle: StatPath(path, out var data), Data: data));
			try
			{
				if (findTask.Wait(TimeSpan.FromSeconds(5), token))
				{
					(var handle, findData) = findTask.Result;
					return handle;
				}
			}
			catch (OperationCanceledException)
			{
			}

			_ = findTask.ContinueWith(t => t.Result.Handle.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
			findData = default;
			return null;
		}

		private static unsafe FindCloseSafeHandle StatPath(string path, out WIN32_FIND_DATAW findData)
		{
			WIN32_FIND_DATAW data = default;
			var handle = PInvoke.FindFirstFileEx(path, FINDEX_INFO_LEVELS.FindExInfoBasic,
				&data, FINDEX_SEARCH_OPS.FindExSearchNameMatch, FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
			findData = data;
			return handle;
		}
	}
}
