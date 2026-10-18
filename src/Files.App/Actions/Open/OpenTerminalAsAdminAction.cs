// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class OpenTerminalAsAdminAction : OpenTerminalAction
	{
		public override string Description
			=> Strings.OpenTerminalAsAdminDescription.GetLocalizedResource();

		public override HotKey HotKey
			=> new(Keys.Oem3, KeyModifiers.CtrlShift);

		protected override bool IsAdmin
			=> true;

		protected override List<ProcessStartInfo> GetProcessStartInfos(string[] paths)
		{
			var startInfos = base.GetProcessStartInfos(paths);
			foreach (var startInfo in startInfos)
			{
				startInfo.Verb = "runas";
				startInfo.UseShellExecute = true;
			}

			return startInfos;
		}
	}
}
