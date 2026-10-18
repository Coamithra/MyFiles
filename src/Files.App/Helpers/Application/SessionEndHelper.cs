// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.System.Recovery;

namespace Files.App.Helpers
{
	/// <summary>
	/// Handles Windows ending the user session (sign-out, restart, update) so the app comes back with its session.
	/// </summary>
	internal static class SessionEndHelper
	{
		// Windows relaunches every registered process, and every Files window is its own process
		private static Mutex? _restartOwner;

		/// <summary>
		/// Gets whether Windows has told this process that the session is ending.
		/// </summary>
		public static bool IsSessionEnding { get; private set; }

		/// <summary>
		/// Gets the outcome of the last restart registration attempt, for diagnostics.
		/// </summary>
		public static string RestartRegistration { get; private set; } = "none";

		/// <summary>
		/// Handles WM_QUERYENDSESSION: one shown window registers the app to be relaunched after sign-in.
		/// </summary>
		/// <param name="ignoreHidden">Lets the automation pipe test registration from a hidden instance.</param>
		public static void OnQueryEndSession(bool ignoreHidden = false)
		{
			try
			{
				if (_restartOwner is not null)
					return;

				if (Automation.AutomationServer.StartHidden && !ignoreHidden)
				{
					RestartRegistration = "skippedHidden";
					return;
				}

				// A window parked by "leave app running" is not something to bring back on screen
				if (App.AppModel?.IsMainWindowClosed ?? true)
				{
					RestartRegistration = "skippedParked";
					return;
				}

				var mutex = new Mutex(false, $"Files-{AppLifecycleHelper.AppEnvironment}-RestartOwner", out var createdNew);
				if (!createdNew)
				{
					mutex.Dispose();
					RestartRegistration = "notOwner";
					return;
				}

				_restartOwner = mutex;

				// The app relaunches itself after a crash, so Windows must not do it too
				var result = PInvoke.RegisterApplicationRestart(
					null,
					REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_CRASH | REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_HANG);

				RestartRegistration = result.Succeeded ? "registered" : $"0x{result.Value:X8}";
			}
			catch (Exception ex)
			{
				RestartRegistration = ex.GetType().Name;
			}
		}

		/// <summary>
		/// Undoes this process's restart registration and gives up the owner slot. Used by the automation pipe's dry run.
		/// </summary>
		public static void ResetRestartRegistration()
		{
			if (_restartOwner is null)
				return;

			PInvoke.UnregisterApplicationRestart();
			_restartOwner.Dispose();
			_restartOwner = null;
		}

		/// <summary>
		/// Handles WM_ENDSESSION: the process is terminated after this without the window closing, so save the session now.
		/// </summary>
		public static void OnEndSession(bool isEnding)
		{
			if (!isEnding)
				return;

			IsSessionEnding = true;

			SafetyExtensions.IgnoreExceptions(() =>
			{
				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				if (userSettingsService.GeneralSettingsService.ContinueLastSessionOnStartUp || userSettingsService.AppSettingsService.RestoreTabsOnStartup)
					AppLifecycleHelper.SaveSessionTabs();
			});
		}
	}
}
