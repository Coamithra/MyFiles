// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json.Nodes;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.System;

namespace Files.App.Helpers
{
	/// <summary>
	/// What a window restores when its main page first loads.
	/// </summary>
	/// <param name="Tabs">The window's own saved tabs; null when it restores none of its own.</param>
	/// <param name="SelectedIndex">The selected tab within <paramref name="Tabs"/>.</param>
	/// <param name="UseLastSessionTabList">Whether the shared LastSessionTabList startup behavior applies, as it did before per-window sessions.</param>
	internal readonly record struct WindowStartup(List<string>? Tabs, int SelectedIndex, bool UseLastSessionTabList);

	/// <summary>
	/// A saved window as the automation pipe reports it.
	/// </summary>
	/// <param name="Status">current, claimed (handed to a window that is starting), live (another running window) or stale (its process is gone).</param>
	internal sealed record WindowSessionInfo(string Id, int Pid, string Status, DateTimeOffset SavedAt, List<string> Tabs, int SelectedIndex, bool HasPlacement);

	/// <summary>
	/// The decision a launch with no window open would make right now.
	/// </summary>
	/// <param name="Decision">restore, fresh or legacy (restore the shared LastSessionTabList, before any window session was saved).</param>
	internal sealed record ColdStartDryRun(string Decision, string Reason, string? Adopt, List<string> Launch);

	/// <summary>
	/// Keeps one session file per window so that every window comes back after a restart, the way Chrome restores its windows.
	/// </summary>
	/// <remarks>
	/// Every Files window is its own process and the user settings file is last-writer-wins, so each window saves its tabs and placement
	/// to LocalState\sessions\&lt;id&gt;.json. Closing one of several windows forgets it; the last window keeps its file. Windows that end
	/// without closing (sign-out, reboot, crash, kill) keep theirs. A launch while no window is open adopts the newest saved window and
	/// relaunches the others with files-dev:?session=&lt;id&gt;. The hidden automation instance never takes part.
	/// </remarks>
	internal static class WindowSessionManager
	{
		// The literal scheme is rewritten per release channel by Configure-AppxManifest.ps1, like every other files-dev: launch.
		private const string SessionUriPrefix = "files-dev:?session=";
		private const string PlainRelaunchUri = "files-dev:";
		private const string SessionQueryKey = "session";
		private const string FolderName = "sessions";
		private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(2);

		private static readonly Lock _stateLock = new();
		private static Mutex? _mutex;
		private static long? _processStart;
		private static bool _isInitialized;
		private static bool _isForgotten;
		private static string? _lastWrittenState;
		private static List<string> _savedTabs = [];
		private static int _savedSelectedIndex;
		private static string? _savedPlacement;
		private static WindowStartup _pendingStartup = new(null, 0, true);
		private static List<string> _pendingSiblings = [];
		private static readonly List<string> _launchedSiblings = [];

		/// <summary>
		/// Gets this window's session id.
		/// </summary>
		public static string WindowId { get; private set; } = Guid.NewGuid().ToString("N");

		/// <summary>
		/// Gets whether this window saves its session; false for the hidden automation instance.
		/// </summary>
		public static bool IsEnabled { get; private set; }

		/// <summary>
		/// Gets how the window started: none, disabled, legacy, fresh, rejected or restored.
		/// </summary>
		public static string StartupKind { get; private set; } = "none";

		/// <summary>
		/// Gets the id of the saved window this one adopted, if any.
		/// </summary>
		public static string? AdoptedId { get; private set; }

		/// <summary>
		/// Gets the saved windows this one relaunched as the cold-start orchestrator.
		/// </summary>
		public static IReadOnlyList<string> LaunchedSiblings => _launchedSiblings;

		/// <summary>
		/// Gets the adopted window's placement blob, applied when the window is first shown.
		/// </summary>
		public static string? AdoptedPlacement { get; private set; }

		/// <summary>
		/// Gets whether the startup restores tabs of this window's own session.
		/// </summary>
		public static bool HasStartupTabs => _pendingStartup.Tabs is not null;

		/// <summary>
		/// Gets the folder that holds the session files.
		/// </summary>
		public static string FolderPath { get; } = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);

		/// <summary>
		/// Decides this window's identity and what it restores. Runs once, before the window is first shown.
		/// </summary>
		public static void EnsureInitialized(object? activationData)
		{
			if (_isInitialized)
				return;

			_isInitialized = true;

			// Never part of the user's session; keeps the shared LastSessionTabList behavior
			if (Automation.AutomationServer.StartHidden)
			{
				StartupKind = "disabled";
				return;
			}

			IsEnabled = true;

			try
			{
				var (key, value) = ParseProtocolQuery(activationData);
				if (key == SessionQueryKey)
					InitializeExplicit(value);
				else if (key is "window" or "tab" or "folder")
					StartFresh("fresh");
				else
					InitializeColdStart();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Window sessions: failed to read the saved windows; starting a fresh window");
				if (StartupKind == "none")
					StartFresh("fresh");
			}
		}

		/// <summary>
		/// Returns what the main page restores, once. Later navigations get a fresh start until the window parks again.
		/// </summary>
		public static WindowStartup TakeStartupSession()
		{
			var startup = _pendingStartup;
			_pendingStartup = new(null, 0, !IsEnabled);

			if (_pendingSiblings.Count > 0)
			{
				var siblings = _pendingSiblings;
				_pendingSiblings = [];
				_ = LaunchSiblingsAsync(siblings);
			}

			return startup;
		}

		/// <summary>
		/// Saves this window's tabs and placement to its session file.
		/// </summary>
		public static void Save(List<string> tabs, int selectedIndex)
		{
			if (!IsEnabled || !_isInitialized || _isForgotten)
				return;

			// A restore in progress would save a partial tab list
			if (!MainPageViewModel.StartupTabsLoadedTask.IsCompleted)
				return;

			// A parked window keeps the session it parked with
			if ((App.AppModel?.IsMainWindowClosed ?? false) && MainPageViewModel.AppInstances.Count == 0)
				return;

			string? placement = null;
			try
			{
				placement = MainWindow.Instance.CapturePlacementData();
			}
			catch (Exception)
			{
				// The crash path may get here off the UI thread; keep the last known placement
			}

			lock (_stateLock)
			{
				_savedTabs = tabs;
				_savedSelectedIndex = selectedIndex;
				_savedPlacement = placement ?? _savedPlacement;

				try
				{
					WriteOwnFile(handoff: false);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.LogWarning(ex, "Window sessions: failed to save this window");
				}
			}
		}

		/// <summary>
		/// Called when the window closes: one of several windows is forgotten, the last one keeps its session.
		/// </summary>
		public static void OnWindowClosed()
		{
			if (!IsEnabled || _isForgotten || SessionEndHelper.IsSessionEnding)
				return;

			RunLocked(() =>
			{
				var others = ReadAll().Where(file => file.Id != WindowId).ToList();
				if (others.Any(IsLive))
				{
					DeleteFile(WindowId);
					_isForgotten = true;
					return;
				}

				// The session is now exactly this window
				foreach (var stale in others)
					DeleteFile(stale.Id);
			});

			if (_isForgotten)
				return;

			// What a parked window restores when it is resumed
			var continueSession = Ioc.Default.GetRequiredService<IGeneralSettingsService>().ContinueLastSessionOnStartUp;
			lock (_stateLock)
				_pendingStartup = continueSession && HasContent(_savedTabs) ? new(_savedTabs, _savedSelectedIndex, false) : new(null, 0, false);
		}

		/// <summary>
		/// Deletes this window's session so it is not restored again.
		/// </summary>
		public static void Forget()
		{
			if (!IsEnabled)
				return;

			_isForgotten = true;
			DeleteFile(WindowId);
		}

		/// <summary>
		/// Hands this window's session to the process about to be launched and returns the URI that relaunches this window as itself.
		/// </summary>
		public static string PrepareRelaunchUri()
		{
			if (!IsEnabled || _isForgotten)
				return PlainRelaunchUri;

			lock (_stateLock)
			{
				try
				{
					WriteOwnFile(handoff: true);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.LogWarning(ex, "Window sessions: failed to hand this window over for a relaunch");
				}
			}

			return SessionUriPrefix + WindowId;
		}

		/// <summary>
		/// Lists the saved windows, for the automation pipe.
		/// </summary>
		public static List<WindowSessionInfo> ListSessions()
		{
			return [.. ReadAll()
				.OrderByDescending(file => file.SavedAt)
				.Select(file => new WindowSessionInfo(
					file.Id,
					file.Pid,
					file.Id == WindowId && IsEnabled ? "current"
						: file.Handoff && (file.Pid == Environment.ProcessId || ProcessMatches(file.Pid, file.ProcessStart)) ? "claimed"
						: IsLive(file) ? "live"
						: "stale",
					file.SavedAt,
					file.Tabs,
					file.SelectedIndex,
					file.Placement is not null))];
		}

		/// <summary>
		/// Computes what a launch with no window open would do now, as a new process would see it. Reads only.
		/// </summary>
		public static ColdStartDryRun DryRunColdStart()
		{
			if (!Ioc.Default.GetRequiredService<IGeneralSettingsService>().ContinueLastSessionOnStartUp)
				return new("legacy", "ContinueLastSessionOnStartUp is off", null, []);

			if (!Directory.Exists(FolderPath))
				return new("legacy", "no window session was saved yet; LastSessionTabList would be restored", null, []);

			var files = ReadAll();
			if (files.FirstOrDefault(file => ProcessMatches(file.Pid, file.ProcessStart)) is { } live)
				return new("fresh", $"window {live.Id} is live", null, []);

			var restorable = files.Where(file => HasContent(file.Tabs)).OrderByDescending(file => file.SavedAt).ToList();
			if (restorable.Count == 0)
				return new("fresh", "no saved window has tabs", null, []);

			return new("restore", $"{restorable.Count} saved window(s)", restorable[0].Id, [.. restorable.Skip(1).Select(file => file.Id)]);
		}

		private static void InitializeExplicit(string? id)
		{
			var adopted = false;
			if (IsValidId(id))
			{
				RunLocked(() =>
				{
					// Only a window that has no process, or one handed over for this launch
					if (ReadFile(PathFor(id!)) is { } file && (file.Handoff || !IsLive(file)))
					{
						Adopt(file);
						adopted = true;
					}
				});
			}

			if (!adopted)
			{
				App.Logger.LogInformation("Window sessions: no saved window to restore for this relaunch; opening a fresh window");
				StartFresh("rejected");
			}
		}

		private static void InitializeColdStart()
		{
			if (!Ioc.Default.GetRequiredService<IGeneralSettingsService>().ContinueLastSessionOnStartUp)
			{
				StartFresh("legacy");
				return;
			}

			RunLocked(() =>
			{
				// The first launch after per-window sessions arrived restores the shared tab list once
				if (!Directory.Exists(FolderPath))
				{
					StartFresh("legacy");
					return;
				}

				var files = ReadAll();
				if (files.Any(IsLive))
				{
					StartFresh("fresh");
					return;
				}

				foreach (var empty in files.Where(file => !HasContent(file.Tabs)))
					DeleteFile(empty.Id);

				var restorable = files.Where(file => HasContent(file.Tabs)).OrderByDescending(file => file.SavedAt).ToList();
				if (restorable.Count == 0)
				{
					StartFresh("fresh");
					return;
				}

				Adopt(restorable[0]);

				// Claim the others so a concurrent launch doesn't restore them too
				var start = GetProcessStart();
				foreach (var sibling in restorable.Skip(1))
				{
					WriteFile(sibling with { Pid = Environment.ProcessId, ProcessStart = start, Handoff = true });
					_pendingSiblings.Add(sibling.Id);
				}
			});

			if (StartupKind == "none")
				StartFresh("fresh");
		}

		private static void StartFresh(string kind)
		{
			StartupKind = kind;
			_pendingStartup = new(null, 0, kind == "legacy");

			// Stamp the window so other launches see it as open
			lock (_stateLock)
			{
				try
				{
					WriteOwnFile(handoff: false);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.LogWarning(ex, "Window sessions: failed to save this window");
				}
			}
		}

		private static void Adopt(SessionFile file)
		{
			WindowId = file.Id;
			AdoptedId = file.Id;
			AdoptedPlacement = file.Placement;
			StartupKind = "restored";

			lock (_stateLock)
			{
				_savedTabs = file.Tabs;
				_savedSelectedIndex = file.SelectedIndex;
				_savedPlacement = file.Placement;
				_pendingStartup = new(HasContent(file.Tabs) ? file.Tabs : null, file.SelectedIndex, false);

				// Take the file over: this process owns it now
				try
				{
					WriteOwnFile(handoff: false);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					App.Logger.LogWarning(ex, "Window sessions: failed to take over the restored window's session");
				}
			}
		}

		private static async Task LaunchSiblingsAsync(List<string> ids)
		{
			try
			{
				// This window first, so it is usable before the others start
				await MainPageViewModel.StartupTabsLoadedTask;

				foreach (var id in ids)
				{
					if (await Launcher.LaunchUriAsync(new Uri(SessionUriPrefix + id)))
						_launchedSiblings.Add(id);
					else
						App.Logger.LogWarning("Window sessions: failed to relaunch a saved window");
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Window sessions: failed to relaunch the saved windows");
			}
		}

		// Callers hold _stateLock
		private static void WriteOwnFile(bool handoff)
		{
			var file = new SessionFile(WindowId, Environment.ProcessId, GetProcessStart(), DateTimeOffset.UtcNow, _savedTabs, _savedSelectedIndex, _savedPlacement, handoff);

			// savedAt alone changing is not worth a write
			var state = ToJson(file with { SavedAt = default }).ToJsonString();
			if (state == _lastWrittenState)
				return;

			WriteFile(file);
			_lastWrittenState = state;
		}

		private static void WriteFile(SessionFile file)
		{
			Directory.CreateDirectory(FolderPath);
			var tempPath = Path.Combine(FolderPath, $"{file.Id}.{Environment.ProcessId}.tmp");
			File.WriteAllText(tempPath, ToJson(file).ToJsonString());
			File.Move(tempPath, PathFor(file.Id), overwrite: true);
		}

		private static void DeleteFile(string id)
		{
			try
			{
				File.Delete(PathFor(id));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.LogDebug(ex, "Window sessions: failed to delete a session file");
			}

			if (id == WindowId)
				_lastWrittenState = null;
		}

		private static JsonObject ToJson(SessionFile file)
		{
			var json = new JsonObject
			{
				["id"] = file.Id,
				["pid"] = file.Pid,
				["processStart"] = file.ProcessStart,
				["savedAt"] = file.SavedAt.ToString("O"),
				["tabs"] = new JsonArray([.. file.Tabs.Select(tab => (JsonNode?)JsonValue.Create(tab))]),
				["selectedIndex"] = file.SelectedIndex,
				["placement"] = file.Placement,
			};

			if (file.Handoff)
				json["handoff"] = true;

			return json;
		}

		private static List<SessionFile> ReadAll()
		{
			if (!Directory.Exists(FolderPath))
				return [];

			return [.. Directory.EnumerateFiles(FolderPath, "*.json")
				.Select(ReadFile)
				.OfType<SessionFile>()];
		}

		// Null when the file is missing, unreadable or not a window session
		private static SessionFile? ReadFile(string path)
		{
			try
			{
				if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
					return null;

				var id = json["id"]?.GetValue<string>();
				if (!IsValidId(id) || !string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase))
					return null;

				return new SessionFile(
					id!,
					json["pid"]?.GetValue<int>() ?? 0,
					json["processStart"]?.GetValue<long>() ?? 0,
					DateTimeOffset.TryParse(json["savedAt"]?.GetValue<string>(), out var savedAt) ? savedAt : default,
					json["tabs"] is JsonArray tabs ? [.. tabs.Select(tab => tab?.GetValue<string>() ?? string.Empty)] : [],
					json["selectedIndex"]?.GetValue<int>() ?? 0,
					json["placement"]?.GetValue<string>(),
					json["handoff"]?.GetValue<bool>() ?? false);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
			{
				App.Logger.LogDebug(ex, "Window sessions: skipped an unreadable session file");
				return null;
			}
		}

		private static void RunLocked(Action action)
		{
			_mutex ??= new Mutex(false, $@"Local\{GetPackageFamilyName()}.WindowSessions");

			bool owned;
			try
			{
				owned = _mutex.WaitOne(MutexTimeout);
			}
			// The previous owner died mid-change; files are only ever replaced by a move, so they are whole
			catch (AbandonedMutexException)
			{
				owned = true;
			}

			if (!owned)
			{
				App.Logger.LogWarning("Window sessions: another window held the session lock for too long");
				return;
			}

			try
			{
				action();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger.LogWarning(ex, "Window sessions: failed to update the saved windows");
			}
			finally
			{
				_mutex.ReleaseMutex();
			}
		}

		private static bool IsLive(SessionFile file)
			=> file.Pid != Environment.ProcessId && ProcessMatches(file.Pid, file.ProcessStart);

		// The start time guards against a reused pid
		private static bool ProcessMatches(int pid, long processStart)
		{
			if (pid <= 0)
				return false;

			try
			{
				using var process = Process.GetProcessById(pid);
				return process.StartTime.ToFileTimeUtc() == processStart;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static long GetProcessStart()
		{
			if (_processStart is null)
			{
				using var process = Process.GetCurrentProcess();
				_processStart = process.StartTime.ToFileTimeUtc();
			}

			return _processStart.Value;
		}

		private static (string? Key, string? Value) ParseProtocolQuery(object? activationData)
		{
			if (activationData is not IProtocolActivatedEventArgs protocolArgs)
				return (null, null);

			var parts = protocolArgs.Uri.Query.TrimStart('?').Split('=');
			return (parts[0], parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Split('&')[0]) : null);
		}

		// Exactly 32 hex characters, so an id from a launch URI can't name a path outside the folder
		private static bool IsValidId(string? id)
			=> id is { Length: 32 } && id.All(char.IsAsciiHexDigit);

		private static bool HasContent(List<string> tabs)
			=> tabs.Any(tab => !string.IsNullOrEmpty(tab));

		private static string PathFor(string id)
			=> Path.Combine(FolderPath, id + ".json");

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

		private sealed record SessionFile(string Id, int Pid, long ProcessStart, DateTimeOffset SavedAt, List<string> Tabs, int SelectedIndex, string? Placement, bool Handoff);
	}
}
