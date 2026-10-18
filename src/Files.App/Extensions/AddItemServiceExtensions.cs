// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Extensions
{
	public static class AddItemServiceExtensions
	{
		/// <summary>
		/// The ShellNew entries the New menus offer: every entry except those whose extension the user hid in
		/// Settings. Entries without an extension can't be hidden and always show.
		/// </summary>
		public static List<ShellNewEntry> GetVisibleEntries(this IAddItemService service)
		{
			var entries = service.GetEntries() ?? [];
			var hidden = Ioc.Default.GetRequiredService<IUserSettingsService>().GeneralSettingsService.HiddenNewMenuEntries;
			if (hidden is not { Count: > 0 })
				return entries;

			var hiddenSet = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
			return [.. entries.Where(entry => string.IsNullOrEmpty(entry.Extension) || !hiddenSet.Contains(entry.Extension))];
		}
	}
}
