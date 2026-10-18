// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Enums
{
	/// <summary>
	/// The backends that served a <see cref="FolderSearch"/>. Home and library searches can mix them per folder.
	/// </summary>
	[Flags]
	public enum FolderSearchBackends
	{
		None = 0,
		Everything = 1,
		WindowsSearch = 2,
		FileTags = 4,
	}
}
