// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Enums
{
	/// <summary>
	/// Defines the terminals the Open in terminal commands can launch. The values are stored in the settings, so only append.
	/// </summary>
	public enum TerminalType
	{
		/// <summary>
		/// Command Prompt (cmd.exe).
		/// </summary>
		CommandPrompt,

		/// <summary>
		/// PowerShell 7 (pwsh.exe) when it is on PATH, else Windows PowerShell.
		/// </summary>
		PowerShell,

		/// <summary>
		/// Windows Terminal (wt.exe), with one tab per folder.
		/// </summary>
		WindowsTerminal,

		/// <summary>
		/// A user-chosen executable and arguments.
		/// </summary>
		Custom,

		/// <summary>
		/// Windows Terminal when it is the default console host, else Command Prompt.
		/// </summary>
		WindowsDefault,
	}
}
