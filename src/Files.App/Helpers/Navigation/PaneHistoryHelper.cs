// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Files.App.Helpers
{
	/// <summary>
	/// Captures and restores the back/forward history of a tab's panes, so saved sessions and reopened tabs keep it.
	/// </summary>
	internal static class PaneHistoryHelper
	{
		// Bounds the session string, which is rewritten into the settings file on every navigation
		public const int MaxDepth = 20;

		/// <summary>
		/// Returns the tab's parameter with each pane's current history attached.
		/// </summary>
		public static TabBarItemParameter? CaptureTab(TabBarItem tab)
		{
			var parameter = tab.NavigationParameter;
			if (parameter?.NavigationParameter is not PaneNavigationArguments args ||
				tab.TabItemContent is not IShellPanesPage panesPage)
				return parameter;

			var panes = panesPage.GetPanes().ToList();

			return new TabBarItemParameter
			{
				InitialPageType = parameter.InitialPageType,
				NavigationParameter = new PaneNavigationArguments
				{
					LeftPaneNavPathParam = args.LeftPaneNavPathParam,
					LeftPaneSelectItemParam = args.LeftPaneSelectItemParam,
					RightPaneNavPathParam = args.RightPaneNavPathParam,
					RightPaneSelectItemParam = args.RightPaneSelectItemParam,
					ShellPaneArrangement = args.ShellPaneArrangement,
					LeftPaneHistory = panes.Count > 0 ? Capture(panes[0], MaxDepth) : args.LeftPaneHistory,
					RightPaneHistory = args.RightPaneNavPathParam is not null && panes.Count > 1 ? Capture(panes[1], MaxDepth) : null,
				},
			};
		}

		/// <summary>
		/// Gets a pane's history, uncapped, and whether it is still waiting for the pane's first navigation.
		/// </summary>
		public static (PaneHistory? History, bool IsPending) Snapshot(ModernShellPage pane)
			=> (Capture(pane, int.MaxValue), pane.PendingHistory is not null);

		/// <summary>
		/// Puts saved history entries on a pane's frame after its first navigation.
		/// </summary>
		public static void Apply(Frame frame, PaneHistory history, IShellPage owner, LayoutPreferencesManager folderSettings)
		{
			if (history.Back is { } back)
			{
				var index = 0;
				foreach (var path in back)
					frame.BackStack.Insert(index++, CreateEntry(path, owner, folderSettings));
			}

			if (history.Forward is { } forward)
			{
				frame.ForwardStack.Clear();
				foreach (var path in forward)
					frame.ForwardStack.Add(CreateEntry(path, owner, folderSettings));
			}
		}

		private static PaneHistory? Capture(ModernShellPage pane, int maxDepth)
		{
			// A restored pane that was never shown has not navigated yet, so its frame stacks are still empty
			if (pane.PendingHistory is { } pending)
				return pending;

			var back = ToPaths(pane.BackwardStack, maxDepth);
			var forward = ToPaths(pane.ForwardStack, maxDepth);

			return back.Count == 0 && forward.Count == 0
				? null
				: new PaneHistory { Back = back, Forward = forward };
		}

		// Keeps the entries nearest the current page, which are last in both stacks
		private static List<string> ToPaths(IList<PageStackEntry> stack, int maxDepth)
		{
			var paths = new List<string>(Math.Min(stack.Count, maxDepth));
			foreach (var entry in stack)
			{
				if (entry.Parameter is not NavigationArguments { NavPathParam: { } path } args)
					continue;

				// A search page's path is the search root, not the results
				if (args.IsSearchResultPage && !path.StartsWith("tag:", StringComparison.Ordinal))
					continue;

				paths.Add(path);
			}

			return paths.Count > maxDepth ? paths[^maxDepth..] : paths;
		}

		// Mirrors how ModernShellPage.OnNavigationParamsChanged picks the page for a path
		private static PageStackEntry CreateEntry(string path, IShellPage owner, LayoutPreferencesManager folderSettings)
		{
			var transition = new SuppressNavigationTransitionInfo();

			switch (path)
			{
				case "Home":
					return new(typeof(HomePage), new NavigationArguments { NavPathParam = path, AssociatedTabInstance = owner }, transition);
				case "ReleaseNotes":
					return new(typeof(ReleaseNotesPage), new NavigationArguments { NavPathParam = path, AssociatedTabInstance = owner }, transition);
				case "Settings":
					return new(typeof(SettingsPage), new NavigationArguments { NavPathParam = path, AssociatedTabInstance = owner }, transition);
			}

			var isTagSearch = path.StartsWith("tag:", StringComparison.Ordinal);

			return new(
				folderSettings.GetLayoutType(path, false),
				new NavigationArguments
				{
					NavPathParam = path,
					IsSearchResultPage = isTagSearch,
					SearchPathParam = isTagSearch ? "Home" : null,
					SearchQuery = isTagSearch ? path : null,
					AssociatedTabInstance = owner,
				},
				transition);
		}
	}
}
