// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Utils.CommandLine;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.System;
using WinRT;

namespace Files.App.ViewModels.UserControls
{
	// Fork: path mode doubles as search, like a browser's omnibox. Text that isn't a path, command,
	// registered URI, child folder or resolvable program is searched for instead of reported as invalid.
	public sealed partial class NavigationToolbarViewModel
	{
		private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";

		public bool IsOmnibarSearchModeVisible =>
			AppearanceSettingsService.ShowOmnibarSearchModeButton || _InstanceViewModel?.IsPageTypeSettings == true;

		[GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:")]
		private static partial Regex UriSchemePrefixRegex();

		private static bool IsPathShapedInput(string input, bool isFtp)
			=> isFtp || Path.IsPathRooted(input) || ShellStorageFolder.IsShellPath(input);

		private async Task<bool> HandleNonPathInputAsync(IShellPage shellPage, string input)
		{
			var text = input.Trim();
			if (text.Length == 0)
				return true;

			if (UriSchemePrefixRegex().IsMatch(text))
			{
				// Launching an unregistered scheme raises Windows' "get an app" prompt, and Everything
				// syntax such as ext:pdf looks like one.
				if (await TryLaunchRegisteredUriAsync(text))
					return true;
			}
			else
			{
				var workingDirectory = shellPage.ShellViewModel?.WorkingDirectory;
				if (string.IsNullOrEmpty(workingDirectory) || shellPage.CurrentPageType == typeof(HomePage))
					workingDirectory = Constants.UserEnvironmentPaths.HomePath;

				var folder = Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : null;
				if (folder is not null && GetExistingPath(folder, text, Directory.Exists) is { } childFolder)
				{
					SavePathToHistory(childFolder);
					shellPage.NavigateToPath(childFolder);
					return true;
				}

				// Resolve before launching so an unknown word never reaches the shell's error UI
				var args = CommandLineParser.SplitArguments(text);
				var name = args.FirstOrDefault("").Trim('"');
				if (ResolveLaunchTarget(name, folder) is { } target)
				{
					return await Win32Helper.InvokeWin32ComponentAsync(target, shellPage, string.Join(' ', args.Skip(1)),
						workingDirectory: folder ?? Constants.UserEnvironmentPaths.HomePath);
				}
			}

			SubmitOmniboxSearch(shellPage, text);
			return true;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		internal void SubmitOmniboxSearch(IShellPage shellPage, string query)
		{
			if (_InstanceViewModel?.IsPageTypeSettings == true)
			{
				(MainWindow.Instance.Content as FrameworkElement)?.FindDescendant<Views.SettingsPage>()?.ApplySearchQuery(query);
				return;
			}

			shellPage.SubmitSearch(query);
			SaveSearchQueryToList(query);
		}

		internal IRichCommand? FindPathModeCommand(string text)
		{
			if (text.Length == 0)
				return null;

			// Enum parsing also accepts numbers and lists, so only plain names count as codes
			if (text.All(char.IsLetter) && Commands[text] is { } byCode && byCode.Code is not CommandCodes.None)
				return byCode;

			var matches = Commands
				.Where(command => command.Code is not CommandCodes.None && command.IsAccessibleGlobally &&
					(string.Equals(command.Label, text, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(command.Description, text, StringComparison.OrdinalIgnoreCase)))
				.Take(2)
				.ToList();

			return matches.Count == 1 ? matches[0] : null;
		}

		private static async Task<bool> TryLaunchRegisteredUriAsync(string text)
		{
			if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.IsFile)
				return false;

			try
			{
				if (!IsRegisteredUrlProtocol(uri.Scheme) &&
					await Launcher.QueryUriSupportAsync(uri, LaunchQuerySupportType.Uri) is not LaunchQuerySupportStatus.Available)
					return false;

				return await Launcher.LaunchUriAsync(uri);
			}
			catch (Exception ex) when (ex is ArgumentException or UriFormatException or COMException)
			{
				return false;
			}
		}

		private static bool IsRegisteredUrlProtocol(string scheme)
		{
			try
			{
				using var key = Registry.ClassesRoot.OpenSubKey(scheme);
				return key?.GetValue("URL Protocol") is not null;
			}
			catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}

		/// <summary>
		/// Resolves a typed program or document name to an existing file without launching anything:
		/// the current folder, then PATH with PATHEXT, then App Paths.
		/// </summary>
		private static string? ResolveLaunchTarget(string name, string? folder)
		{
			if (string.IsNullOrWhiteSpace(name))
				return null;

			var extensions = GetExecutableExtensions();
			var hasExecutableExtension = extensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

			if (folder is not null)
			{
				if (GetExistingPath(folder, name, File.Exists) is { } file)
					return file;

				if (!Path.HasExtension(name))
				{
					foreach (var extension in extensions)
					{
						if (GetExistingPath(folder, name + extension, File.Exists) is { } program)
							return program;
					}
				}
			}

			if (name.IndexOfAny(['\\', '/']) >= 0)
				return null;

			foreach (var directory in GetSearchPathDirectories())
			{
				if (hasExecutableExtension)
				{
					if (GetExistingPath(directory, name, File.Exists) is { } program)
						return program;
				}
				else
				{
					foreach (var extension in extensions)
					{
						if (GetExistingPath(directory, name + extension, File.Exists) is { } program)
							return program;
					}
				}
			}

			var appPathsName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
			foreach (var hive in (RegistryKey[])[Registry.CurrentUser, Registry.LocalMachine])
			{
				try
				{
					using var key = hive.OpenSubKey(AppPathsKey + appPathsName);
					if (key?.GetValue(null) is string value &&
						Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')) is { Length: > 0 } path &&
						Path.IsPathFullyQualified(path) &&
						File.Exists(path))
						return path;
				}
				catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
				{
				}
			}

			return null;
		}

		private static string? GetExistingPath(string directory, string name, Func<string, bool> exists)
		{
			try
			{
				var path = Path.GetFullPath(Path.Combine(directory, name));
				return exists(path) ? path : null;
			}
			catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
			{
				return null;
			}
		}

		private static string[] GetExecutableExtensions()
			=> (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(extension => extension.StartsWith('.'))
				.ToArray();

		private static IEnumerable<string> GetSearchPathDirectories()
		{
			// The stored PATH too, so programs installed after the app started are found
			var path = string.Join(';',
				Environment.GetEnvironmentVariable("PATH"),
				Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
				Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));

			return path
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(directory => Environment.ExpandEnvironmentVariables(directory.Trim('"')))
				.Where(directory => Path.IsPathFullyQualified(directory))
				.Distinct(StringComparer.OrdinalIgnoreCase);
		}
	}
}
