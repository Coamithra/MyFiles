// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Parameters
{
	/// <summary>
	/// A pane's back/forward history as navigation paths, kept in the frame's own stack order.
	/// </summary>
	public sealed class PaneHistory
	{
		/// <summary>
		/// Back stack entries, oldest first; the last one is where Back goes.
		/// </summary>
		public List<string>? Back { get; set; }

		/// <summary>
		/// Forward stack entries in the frame's ForwardStack order.
		/// </summary>
		public List<string>? Forward { get; set; }
	}
}
