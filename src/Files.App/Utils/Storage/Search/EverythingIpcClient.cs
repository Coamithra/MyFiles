// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.DataExchange;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Queries voidtools Everything through its WM_COPYDATA IPC (QUERY2, Everything 1.4 and later).
	/// Thread-affine: create, query and dispose on one thread, which pumps the reply messages.
	/// </summary>
	internal sealed unsafe partial class EverythingIpcClient : IDisposable
	{
		// The second is Everything 1.5 alpha's default instance.
		private static readonly string[] EverythingWindowClassNames = ["EVERYTHING_TASKBAR_NOTIFICATION", "EVERYTHING_TASKBAR_NOTIFICATION_(1.5a)"];
		private const string ReplyWindowClassName = "FilesEverythingIpcReply";

		private const nuint IpcGetMajorVersion = 0;
		private const nuint IpcGetMinorVersion = 1;
		private const nuint IpcGetRevision = 2;
		private const nuint IpcGetBuildNumber = 3;
		private const nuint IpcIsDbLoaded = 401;
		private const nuint CopyDataQuery2W = 18;
		private const uint RequestFullPathAndName = 0x4;
		private const uint SortNameAscending = 1;
		private const uint ItemFlagFolder = 0x1;
		private const uint ItemFlagRoot = 0x2;

		private const int Query2HeaderSize = 7 * sizeof(uint);
		private const int List2HeaderSize = 5 * sizeof(uint);
		private const int Item2Size = 2 * sizeof(uint);

		private const uint SendTimeoutMs = 1000;
		private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

		private static readonly Lock windowClassLock = new();
		private static bool isWindowClassRegistered;
		private static int lastReplyId;

		// The reply arrives through WndProc on the thread that owns the reply window.
		[ThreadStatic] private static nint t_expectedSender;
		[ThreadStatic] private static nuint t_expectedReplyId;
		[ThreadStatic] private static byte[]? t_reply;

		private readonly HWND everythingWindow;
		private HWND replyWindow;

		private EverythingIpcClient(HWND everythingWindow, HWND replyWindow)
		{
			this.everythingWindow = everythingWindow;
			this.replyWindow = replyWindow;
		}

		public static EverythingStatus GetStatus()
			=> GetStatus(out _);

		private static EverythingStatus GetStatus(out HWND window)
		{
			window = FindEverythingWindow();
			if (window.IsNull)
				return new(false, false, null);

			if (!TrySend(window, IpcIsDbLoaded, out var isLoaded))
				return new(false, false, null);

			string? version = null;
			if (TrySend(window, IpcGetMajorVersion, out var major) &&
				TrySend(window, IpcGetMinorVersion, out var minor) &&
				TrySend(window, IpcGetRevision, out var revision) &&
				TrySend(window, IpcGetBuildNumber, out var build))
				version = $"{major}.{minor}.{revision}.{build}";

			return new(true, isLoaded != 0, version);
		}

		/// <summary>
		/// Returns null when Everything isn't running or hasn't loaded its database yet.
		/// </summary>
		public static EverythingIpcClient? TryCreate()
		{
			var status = GetStatus(out var everythingWindow);
			if (!status.IsDatabaseLoaded)
				return null;

			var instance = (HINSTANCE)PInvoke.GetModuleHandle(default(PCWSTR));
			EnsureWindowClass(instance);

			HWND replyWindow;
			fixed (char* className = ReplyWindowClassName)
			{
				replyWindow = PInvoke.CreateWindowEx(0, className, default, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, HMENU.Null, instance, null);
			}

			return replyWindow.IsNull ? null : new EverythingIpcClient(everythingWindow, replyWindow);
		}

		/// <summary>
		/// Runs one query. Returns false when Everything rejects it, doesn't answer in time or answers malformed data.
		/// </summary>
		public bool TryQuery(string search, uint offset, uint maxResults, CancellationToken token, [NotNullWhen(true)] out EverythingQueryResult? result)
		{
			ObjectDisposedException.ThrowIf(replyWindow.IsNull, this);
			result = null;

			var replyId = (nuint)(uint)Interlocked.Increment(ref lastReplyId);
			var request = BuildQuery2(search, offset, maxResults, (uint)(nint)replyWindow.Value, (uint)replyId);

			t_expectedSender = (nint)everythingWindow.Value;
			t_expectedReplyId = replyId;
			t_reply = null;
			try
			{
				fixed (byte* pRequest = request)
				{
					COPYDATASTRUCT copyData = new()
					{
						dwData = CopyDataQuery2W,
						cbData = (uint)request.Length,
						lpData = pRequest,
					};

					var sent = PInvoke.SendMessageTimeout(everythingWindow, PInvoke.WM_COPYDATA, new WPARAM((nuint)(nint)replyWindow.Value), new LPARAM((nint)(&copyData)),
						SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, SendTimeoutMs, out var accepted);
					if (sent.Value == 0 || accepted == 0)
						return false;
				}

				if (!PumpUntilReply(token) || t_reply is not { } reply)
					return false;

				return TryParseList2(reply, out result);
			}
			finally
			{
				t_expectedReplyId = 0;
				t_expectedSender = 0;
				t_reply = null;
			}
		}

		public void Dispose()
		{
			if (replyWindow.IsNull)
				return;

			PInvoke.DestroyWindow(replyWindow);
			replyWindow = HWND.Null;
		}

		private static HWND FindEverythingWindow()
		{
			foreach (var className in EverythingWindowClassNames)
			{
				var window = PInvoke.FindWindow(className, null);
				if (!window.IsNull)
					return window;
			}

			return HWND.Null;
		}

		private static bool TrySend(HWND window, nuint command, out nuint result)
			=> PInvoke.SendMessageTimeout(window, PInvoke.WM_USER, new WPARAM(command), new LPARAM(0), SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, SendTimeoutMs, out result) != 0;

		private static void EnsureWindowClass(HINSTANCE instance)
		{
			lock (windowClassLock)
			{
				if (isWindowClassRegistered)
					return;

				fixed (char* className = ReplyWindowClassName)
				{
					WNDCLASSEXW windowClass = new()
					{
						cbSize = (uint)sizeof(WNDCLASSEXW),
						lpfnWndProc = &WndProc,
						hInstance = instance,
						lpszClassName = className,
					};

					// Zero with ERROR_CLASS_ALREADY_EXISTS still leaves a usable class; CreateWindowEx reports any real failure.
					PInvoke.RegisterClassEx(&windowClass);
				}

				isWindowClassRegistered = true;
			}
		}

		[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
		private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
		{
			if (msg != PInvoke.WM_COPYDATA)
				return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);

			try
			{
				var copyData = (COPYDATASTRUCT*)lParam.Value;
				if (copyData is null || t_expectedReplyId == 0 || (nint)wParam.Value != t_expectedSender ||
					copyData->dwData != t_expectedReplyId || copyData->cbData > int.MaxValue)
					return new LRESULT(0);

				t_reply = copyData->cbData == 0 || copyData->lpData is null
					? []
					: new ReadOnlySpan<byte>(copyData->lpData, (int)copyData->cbData).ToArray();

				return new LRESULT(1);
			}
			catch (Exception)
			{
				// Exceptions must not cross the native boundary.
				return new LRESULT(0);
			}
		}

		private static bool PumpUntilReply(CancellationToken token)
		{
			var deadline = Environment.TickCount64 + (long)ReplyTimeout.TotalMilliseconds;
			while (t_reply is null)
			{
				while (PInvoke.PeekMessage(out var msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
					PInvoke.DispatchMessage(in msg);

				if (t_reply is not null)
					break;

				var remaining = deadline - Environment.TickCount64;
				if (token.IsCancellationRequested || remaining <= 0)
					return false;

				// Sent messages (Everything's reply) are dispatched by the next PeekMessage.
				PInvoke.MsgWaitForMultipleObjects([], false, (uint)Math.Min(remaining, 100), QUEUE_STATUS_FLAGS.QS_ALLINPUT);
			}

			return true;
		}

		private static byte[] BuildQuery2(string search, uint offset, uint maxResults, uint replyWindow, uint replyId)
		{
			var request = new byte[Query2HeaderSize + (search.Length + 1) * sizeof(char)];
			var span = request.AsSpan();
			BinaryPrimitives.WriteUInt32LittleEndian(span[0..], replyWindow);
			BinaryPrimitives.WriteUInt32LittleEndian(span[4..], replyId);
			BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0); // search flags: Everything's defaults
			BinaryPrimitives.WriteUInt32LittleEndian(span[12..], offset);
			BinaryPrimitives.WriteUInt32LittleEndian(span[16..], maxResults);
			BinaryPrimitives.WriteUInt32LittleEndian(span[20..], RequestFullPathAndName);
			BinaryPrimitives.WriteUInt32LittleEndian(span[24..], SortNameAscending);
			Encoding.Unicode.GetBytes(search, span[Query2HeaderSize..]);
			return request;
		}

		// The reply comes from another process, so every offset and length is bounds-checked.
		private static bool TryParseList2(byte[] reply, [NotNullWhen(true)] out EverythingQueryResult? result)
		{
			result = null;
			ReadOnlySpan<byte> data = reply;
			if (data.Length < List2HeaderSize)
				return false;

			var totalCount = BinaryPrimitives.ReadUInt32LittleEndian(data[0..]);
			var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
			var requestFlags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
			if ((requestFlags & RequestFullPathAndName) == 0 && itemCount > 0)
				return false;

			if (itemCount > (uint)((data.Length - List2HeaderSize) / Item2Size))
				return false;

			var items = new List<EverythingItem>((int)itemCount);
			for (var i = 0; i < (int)itemCount; i++)
			{
				var itemHeader = data.Slice(List2HeaderSize + i * Item2Size, Item2Size);
				var flags = BinaryPrimitives.ReadUInt32LittleEndian(itemHeader);
				var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(itemHeader[4..]);

				// Full path is the first field when only it is requested: a DWORD length, then the characters.
				if (dataOffset > (uint)data.Length - sizeof(uint))
					return false;

				var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)dataOffset..]);
				var textStart = (long)dataOffset + sizeof(uint);
				if (length > int.MaxValue / sizeof(char) || textStart + (long)length * sizeof(char) > data.Length)
					return false;

				var path = Encoding.Unicode.GetString(data.Slice((int)textStart, (int)length * sizeof(char)));
				items.Add(new(path, (flags & ItemFlagFolder) != 0, (flags & ItemFlagRoot) != 0));
			}

			result = new(totalCount, items);
			return true;
		}
	}

	internal readonly record struct EverythingItem(string Path, bool IsFolder, bool IsRoot);

	internal sealed record EverythingQueryResult(uint TotalCount, IReadOnlyList<EverythingItem> Items);

	internal readonly record struct EverythingStatus(bool IsRunning, bool IsDatabaseLoaded, string? Version);
}
