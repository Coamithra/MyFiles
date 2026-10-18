// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Helpers
{
	/// <summary>
	/// The user's list of shell context menu entries to hide, matched against an entry's label or verb
	/// (case-insensitive, <c>*</c> matches any text). Also keeps the labels the menus have shown this session,
	/// which Settings offers as suggestions.
	/// </summary>
	public sealed class ShellMenuBlocklist
	{
		private const int MaxSeenEntries = 300;

		private static readonly Lock seenLock = new();
		private static readonly List<string> seenEntries = [];

		private readonly string[] patterns;

		private ShellMenuBlocklist(string[] patterns)
		{
			this.patterns = patterns;
		}

		public bool IsEmpty
			=> patterns.Length == 0;

		public static ShellMenuBlocklist FromSettings(IGeneralSettingsService settings)
			=> new(settings.HiddenShellMenuEntries?
				.Select(x => x?.Trim())
				.Where(x => !string.IsNullOrEmpty(x))
				.Select(x => x!)
				.ToArray() ?? []);

		/// <summary>
		/// Whether any pattern matches the verb or the label (the label without its access-key markers).
		/// </summary>
		public bool Matches(string? verb, string? label)
		{
			foreach (var pattern in patterns)
			{
				if ((!string.IsNullOrEmpty(verb) && IsMatch(pattern, verb)) || (!string.IsNullOrEmpty(label) && IsMatch(pattern, label.Trim())))
					return true;
			}

			return false;
		}

		public static void RecordSeen(string? label)
		{
			label = label?.Trim();
			if (string.IsNullOrEmpty(label))
				return;

			lock (seenLock)
			{
				if (seenEntries.Count >= MaxSeenEntries || seenEntries.Contains(label, StringComparer.OrdinalIgnoreCase))
					return;

				seenEntries.Add(label);
			}
		}

		public static IReadOnlyList<string> GetSeenEntries()
		{
			lock (seenLock)
				return [.. seenEntries];
		}

		/// <summary>
		/// Case-insensitive match of the whole text, where <c>*</c> in the pattern matches any run of characters.
		/// </summary>
		internal static bool IsMatch(string pattern, string text)
		{
			int p = 0, t = 0, star = -1, starText = 0;
			while (t < text.Length)
			{
				if (p < pattern.Length && pattern[p] == '*')
				{
					star = p++;
					starText = t;
				}
				else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))
				{
					p++;
					t++;
				}
				else if (star >= 0)
				{
					// Let the last * absorb one more character and retry from there
					p = star + 1;
					t = ++starText;
				}
				else
				{
					return false;
				}
			}

			while (p < pattern.Length && pattern[p] == '*')
				p++;

			return p == pattern.Length;
		}
	}
}
