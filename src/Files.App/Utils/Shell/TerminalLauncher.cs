// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.Utils.Shell
{
	/// <summary>
	/// Builds the processes and labels of the Open in terminal commands for a resolved <see cref="TerminalType"/>.
	/// </summary>
	internal static class TerminalLauncher
	{
		private const string PathPlaceholder = "{path}";

		public static string GetLabel(TerminalType type, bool asAdmin)
			=> (type, asAdmin) switch
			{
				(TerminalType.PowerShell, false) => Strings.OpenInPowerShell.GetLocalizedResource(),
				(TerminalType.PowerShell, true) => Strings.OpenInPowerShellAsAdmin.GetLocalizedResource(),
				(TerminalType.WindowsTerminal, false) => Strings.OpenTerminal.GetLocalizedResource(),
				(TerminalType.WindowsTerminal, true) => Strings.OpenTerminalAsAdmin.GetLocalizedResource(),
				(TerminalType.Custom, false) => Strings.OpenInCustomTerminal.GetLocalizedResource(),
				(TerminalType.Custom, true) => Strings.OpenInCustomTerminalAsAdmin.GetLocalizedResource(),
				(_, false) => Strings.OpenInCommandPrompt.GetLocalizedResource(),
				(_, true) => Strings.OpenInCommandPromptAsAdmin.GetLocalizedResource(),
			};

		/// <summary>
		/// One process per folder, except Windows Terminal, which opens every folder as a tab of one window.
		/// The built-in terminals get the folder in their arguments too, because Windows starts an elevated
		/// System32 executable in System32 whatever working directory it is given.
		/// </summary>
		public static List<ProcessStartInfo> Build(TerminalType type, string customPath, string customArguments, string[] paths)
		{
			if (paths.Length is 0)
				return [];

			return type switch
			{
				TerminalType.PowerShell => [.. paths.Select(BuildPowerShell)],
				TerminalType.WindowsTerminal => [BuildWindowsTerminal(paths)],
				TerminalType.Custom => [.. paths.Select(path => BuildCustom(customPath, customArguments, path))],
				_ => [.. paths.Select(BuildCommandPrompt)],
			};
		}

		private static ProcessStartInfo BuildCommandPrompt(string path)
			=> new()
			{
				FileName = "cmd.exe",
				// Explorer's own "Open command window here" verb, with an outer quote pair for /s to strip so the
				// quotes around the path survive and keep characters such as & literal. pushd also maps UNC paths.
				Arguments = $"/s /k \"pushd \"{path}\"\"",
				WorkingDirectory = path,
				UseShellExecute = true,
			};

		private static ProcessStartInfo BuildPowerShell(string path)
			=> new()
			{
				FileName = FindOnPath("pwsh.exe") ?? "powershell.exe",
				ArgumentList = { "-NoExit", "-Command", $"Set-Location -LiteralPath {Win32Helper.ToPowerShellStringLiteral(path)}" },
				WorkingDirectory = path,
				UseShellExecute = true,
			};

		private static ProcessStartInfo BuildWindowsTerminal(string[] paths)
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = "wt.exe",
				UseShellExecute = false,
				ArgumentList = { "-d", paths[0] }
			};

			for (int i = 1; i < paths.Length; i++)
			{
				startInfo.ArgumentList.Add(";");
				startInfo.ArgumentList.Add("nt");
				startInfo.ArgumentList.Add("-d");
				startInfo.ArgumentList.Add(paths[i]);
			}

			return startInfo;
		}

		private static ProcessStartInfo BuildCustom(string customPath, string customArguments, string path)
			=> new()
			{
				FileName = Environment.ExpandEnvironmentVariables(customPath.Trim()),
				Arguments = ReplacePathPlaceholder(customArguments, path),
				WorkingDirectory = path,
				UseShellExecute = true,
			};

		private static string ReplacePathPlaceholder(string arguments, string path)
		{
			// In "{path}", a path ending in a backslash (C:\) would escape the closing quote.
			var quotedPath = path.EndsWith('\\') ? path + "\\" : path;
			return arguments
				.Replace(PathPlaceholder + "\"", quotedPath + "\"", StringComparison.Ordinal)
				.Replace(PathPlaceholder, path, StringComparison.Ordinal);
		}

		private static string? FindOnPath(string fileName)
		{
			foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				try
				{
					var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory), fileName);
					if (File.Exists(candidate))
						return candidate;
				}
				catch (ArgumentException)
				{
				}
			}

			return null;
		}
	}
}
