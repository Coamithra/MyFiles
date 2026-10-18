// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Files.App.Automation
{
	/// <summary>
	/// Local named-pipe endpoint that lets agents drive and inspect the running app. Debug builds only.
	/// </summary>
	/// <remarks>
	/// Each request is one line of JSON, <c>{"id":1,"method":"navigate","params":{"path":"C:\\"}}</c>,
	/// answered by one line, <c>{"id":1,"result":...}</c> or <c>{"id":1,"error":"..."}</c>.
	/// The pipe only accepts the current user. Client: <c>.claude/scripts/files_auto.py</c>.
	/// </remarks>
	internal static class AutomationServer
	{
		public static string PipeName => $"Files.Automation.{AppLifecycleHelper.AppEnvironment}";

		/// <summary>
		/// Set by the <c>-AutomationHidden</c> launch flag: the window stays cloaked and never takes focus.
		/// </summary>
		public static bool StartHidden { get; } =
#if DEBUG
			Environment.GetCommandLineArgs().Any(IsLaunchFlag);
#else
			false;
#endif

		/// <summary>
		/// Whether a command-line argument is the automation launch flag, which navigation parsing must skip.
		/// </summary>
		public static bool IsLaunchFlag(string arg)
			=> arg.Equals("-AutomationHidden", StringComparison.OrdinalIgnoreCase);

		public static bool IsWindowCloaked { get; private set; }

#if DEBUG
		private static bool _started;
#endif

		public static void Start()
		{
#if DEBUG
			if (_started)
				return;

			_started = true;
			_ = Task.Run(AcceptConnectionsAsync);
#endif
		}

		/// <summary>
		/// Cloaked windows keep rendering, so the app runs normally while nothing shows on screen or in Alt+Tab.
		/// </summary>
		public static unsafe void SetWindowCloaked(MainWindow window, bool cloaked)
		{
			BOOL value = cloaked;
			PInvoke.DwmSetWindowAttribute(new(window.WindowHandle), DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
			window.AppWindow.IsShownInSwitchers = !cloaked;
			IsWindowCloaked = cloaked;
		}

		private static async Task AcceptConnectionsAsync()
		{
			while (true)
			{
				NamedPipeServerStream? pipe = null;
				try
				{
					pipe = CreatePipe();
					await pipe.WaitForConnectionAsync();
					_ = Task.Run(() => ServeAsync(pipe));
				}
				catch (Exception ex)
				{
					pipe?.Dispose();
					App.Logger.LogWarning(ex, "Automation pipe failed to accept a connection");
					await Task.Delay(1000);
				}
			}
		}

		private static NamedPipeServerStream CreatePipe()
		{
			var security = new PipeSecurity();
			security.AddAccessRule(new PipeAccessRule(
				WindowsIdentity.GetCurrent().User!,
				PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
				AccessControlType.Allow));

			return NamedPipeServerStreamAcl.Create(
				PipeName,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous,
				0,
				0,
				security);
		}

		// Must never throw: the app treats unobserved task exceptions as fatal.
		private static async Task ServeAsync(NamedPipeServerStream pipe)
		{
			try
			{
				using (pipe)
				{
					// Not disposed: they leave the pipe open and would flush into a pipe the client may have closed.
					var encoding = new UTF8Encoding(false);
					var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);
					var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { AutoFlush = true };

					while (await reader.ReadLineAsync() is { } line)
					{
						if (string.IsNullOrWhiteSpace(line))
							continue;

						var response = await HandleAsync(line);
						await writer.WriteLineAsync(response.ToJsonString());
					}
				}
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				// The client disconnected.
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Automation connection failed");
			}
		}

		private static async Task<JsonObject> HandleAsync(string line)
		{
			JsonNode? id = null;
			try
			{
				var request = JsonNode.Parse(line) as JsonObject
					?? throw new AutomationException("The request must be a JSON object.");

				id = request["id"]?.DeepClone();
				var method = request["method"]?.GetValue<string>()
					?? throw new AutomationException("The request has no \"method\".");
				var parameters = request["params"] as JsonObject ?? new JsonObject();

				var result = await AutomationHandlers.InvokeAsync(method, parameters);
				return new JsonObject { ["id"] = id, ["result"] = result };
			}
			catch (Exception ex)
			{
				return new JsonObject { ["id"] = id?.DeepClone(), ["error"] = ex is AutomationException ? ex.Message : ex.ToString() };
			}
		}
	}

	/// <summary>
	/// An error caused by the request, reported to the client without a stack trace.
	/// </summary>
	internal sealed class AutomationException(string message) : Exception(message)
	{
	}
}
