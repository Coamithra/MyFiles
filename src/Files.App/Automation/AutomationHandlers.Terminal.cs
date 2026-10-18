// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Actions;
using System.Text.Json.Nodes;

namespace Files.App.Automation
{
	internal static partial class AutomationHandlers
	{
		private static JsonNode TerminalPreview(JsonObject p)
		{
			var code = p["code"]?.GetValue<string>() ?? nameof(CommandCodes.OpenTerminal);
			var command = Ioc.Default.GetRequiredService<ICommandManager>()[code];
			if (command is not ActionCommand { Action: OpenTerminalAction action })
				throw new AutomationException($"\"{code}\" is not an Open in terminal command.");

			var generalSettings = Ioc.Default.GetRequiredService<IUserSettingsService>().GeneralSettingsService;
			return new JsonObject
			{
				["code"] = command.Code.ToString(),
				["label"] = command.Label,
				["executable"] = command.IsExecutable,
				["terminalType"] = generalSettings.TerminalType.ToString(),
				["effectiveType"] = action.GetEffectiveTerminalType().ToString(),
				["launches"] = new JsonArray([.. action.GetStartInfos().Select(startInfo => (JsonNode)new JsonObject
				{
					["fileName"] = startInfo.FileName,
					["arguments"] = startInfo.Arguments,
					["argumentList"] = new JsonArray([.. startInfo.ArgumentList.Select(argument => (JsonNode?)JsonValue.Create(argument))]),
					["workingDirectory"] = startInfo.WorkingDirectory,
					["verb"] = startInfo.Verb,
					["useShellExecute"] = startInfo.UseShellExecute,
				})]),
			};
		}
	}
}
