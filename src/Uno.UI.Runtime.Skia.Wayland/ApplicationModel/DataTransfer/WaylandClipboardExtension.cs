using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using wl_data_device on the main app's Wayland connection.
/// All protocol calls happen on the event thread. The UI thread only
/// reads/writes cached strings via volatile fields.
///
/// Selection events are only sent to clients with keyboard focus, so
/// we MUST use the app's own connection (which owns the focused surface).
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	// Wayland objects — only accessed from event thread
	private IntPtr _wlDisplay;
	private IntPtr _wlDataDevice;
	private IntPtr _wlDataDeviceManager;
	private IntPtr _currentOffer;
	private IntPtr _currentSource;
	private readonly List<string> _offerMimeTypes = new();

	// Delegates stored as fields to prevent GC
	private WlDataDeviceDataOfferDelegate? _ddDataOffer;
	private WlDataDeviceSelectionDelegate? _ddSelection;
	private WlDataOfferOfferDelegate? _doOffer;
	private WlDataSourceSendDelegate? _dsSend;
	private WlDataSourceCancelledDelegate? _dsCancelled;
	private GCHandle _ddListenerHandle;
	private GCHandle _doListenerHandle;
	private GCHandle _dsListenerHandle;
	private bool _doListenerReady;
	private bool _initialized;

	// Thread-safe cached text
	private volatile string? _incomingText;
	private volatile string? _outgoingText;
	private volatile string? _pendingCopyText;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	/// <summary>Called on the EVENT THREAD at the start of EventLoop.</summary>
	internal void InitializeOnEventThread(IntPtr wlDisplay, IntPtr wlDataDeviceManager, IntPtr wlSeat)
	{
		if (_initialized) { return; }
		_initialized = true;
		_wlDisplay = wlDisplay;
		_wlDataDeviceManager = wlDataDeviceManager;

		if (wlDataDeviceManager == IntPtr.Zero || wlSeat == IntPtr.Zero) { return; }

		// Create all delegates up front
		_ddDataOffer = OnDataOffer;
		_ddSelection = OnSelection;
		_doOffer = OnOfferMimeType;
		_dsSend = OnSourceSend;
		_dsCancelled = OnSourceCancelled;

		// Create data device: get_data_device opcode=1
		_wlDataDevice = WaylandBindings.wl_proxy_marshal_flags(
			wlDataDeviceManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(wlDataDeviceManager), 0,
			IntPtr.Zero, wlSeat);
		if (_wlDataDevice == IntPtr.Zero) { return; }

		// Data device listener — ALL 6 slots filled
		var ddListener = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(_ddDataOffer),
			enter = Marshal.GetFunctionPointerForDelegate((WlDataDeviceEnterDelegate)NoopEnter),
			leave = Marshal.GetFunctionPointerForDelegate((WlDataDeviceLeaveDelegate)NoopLeave),
			motion = Marshal.GetFunctionPointerForDelegate((WlDataDeviceMotionDelegate)NoopMotion),
			drop = Marshal.GetFunctionPointerForDelegate((WlDataDeviceDropDelegate)NoopDrop),
			selection = Marshal.GetFunctionPointerForDelegate(_ddSelection),
		};
		_ddListenerHandle = GCHandle.Alloc(ddListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlDataDevice, _ddListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// Pre-build offer listener — ALL 3 slots filled
		var doListener = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(_doOffer),
			source_actions = Marshal.GetFunctionPointerForDelegate((WlDataOfferSourceActionsDelegate)NoopSourceActions),
			action = Marshal.GetFunctionPointerForDelegate((WlDataOfferActionDelegate)NoopAction),
		};
		_doListenerHandle = GCHandle.Alloc(doListener, GCHandleType.Pinned);
		_doListenerReady = true;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug("Clipboard initialized on event thread");
		}
	}

	/// <summary>Called each iteration of the event loop to process queued copy requests.</summary>
	internal void ProcessPendingOnEventThread()
	{
		var text = System.Threading.Interlocked.Exchange(ref _pendingCopyText, null);
		if (text == null || !_initialized || _wlDataDevice == IntPtr.Zero) { return; }

		_outgoingText = text;
		_incomingText = null;

		// Clean up previous source
		if (_currentSource != IntPtr.Zero)
		{
			if (_dsListenerHandle.IsAllocated) { _dsListenerHandle.Free(); }
			WaylandBindings.wl_proxy_destroy(_currentSource);
		}

		// create_data_source opcode=0
		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDeviceManager, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0, IntPtr.Zero);
		if (_currentSource == IntPtr.Zero) { return; }

		// Source listener — ALL 6 slots filled
		var dsListener = new WlDataSourceListener
		{
			target = Marshal.GetFunctionPointerForDelegate((WlDataSourceTargetDelegate)NoopTarget),
			send = Marshal.GetFunctionPointerForDelegate(_dsSend!),
			cancelled = Marshal.GetFunctionPointerForDelegate(_dsCancelled!),
			dnd_drop_performed = Marshal.GetFunctionPointerForDelegate((WlDataSourceDndDropPerformedDelegate)NoopDndDropPerformed),
			dnd_finished = Marshal.GetFunctionPointerForDelegate((WlDataSourceDndFinishedDelegate)NoopDndFinished),
			action = Marshal.GetFunctionPointerForDelegate((WlDataSourceActionDelegate)NoopSourceAction),
		};
		_dsListenerHandle = GCHandle.Alloc(dsListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, _dsListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		OfferMime(_currentSource, "text/plain;charset=utf-8");
		OfferMime(_currentSource, "text/plain");

		// set_selection opcode=1
		WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDevice, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_wlDataDevice), 0,
			_currentSource, IntPtr.Zero);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	// --- UI thread methods (NO Wayland calls) ---

	public void Clear() { _outgoingText = null; _incomingText = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }

	public DataPackageView? GetContent()
	{
		var text = _incomingText ?? _outgoingText;
		if (text != null)
		{
			var p = new DataPackage();
			p.SetText(text);
			return p.GetView();
		}
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null) { _outgoingText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }
		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (text != null) { _pendingCopyText = text; _outgoingText = text; _incomingText = null; }
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// --- Event thread callbacks ---

	private void OnDataOffer(IntPtr data, IntPtr dd, IntPtr offer)
	{
		_offerMimeTypes.Clear();
		if (_doListenerReady)
		{
			_ = WaylandBindings.wl_proxy_add_listener(offer, _doListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
		}
	}

	private void OnSelection(IntPtr data, IntPtr dd, IntPtr offer)
	{
		if (_currentOffer != IntPtr.Zero && _currentOffer != offer)
		{
			WaylandBindings.wl_proxy_destroy(_currentOffer);
		}
		_currentOffer = offer;
		_incomingText = null;

		if (offer != IntPtr.Zero)
		{
			string? mime = null;
			if (_offerMimeTypes.Contains("text/plain;charset=utf-8")) { mime = "text/plain;charset=utf-8"; }
			else if (_offerMimeTypes.Contains("text/plain")) { mime = "text/plain"; }
			if (mime != null) { _incomingText = ReadOfferText(offer, mime); }
		}
	}

	private void OnOfferMimeType(IntPtr data, IntPtr offer, string mime) => _offerMimeTypes.Add(mime);

	private void OnSourceSend(IntPtr data, IntPtr source, string mime, int fd)
	{
		var text = _outgoingText;
		if (text != null)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			int written = 0;
			while (written < bytes.Length)
			{
				var n = LibcWrite(fd, bytes, written, bytes.Length - written);
				if (n <= 0) { break; }
				written += n;
			}
		}
		_ = WaylandBindings.close(fd);
	}

	private void OnSourceCancelled(IntPtr data, IntPtr source)
	{
		if (source == _currentSource) { _currentSource = IntPtr.Zero; }
		WaylandBindings.wl_proxy_destroy(source);
	}

	// --- Helpers ---

	private static void OfferMime(IntPtr source, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(source, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(source), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	private string? ReadOfferText(IntPtr offer, string mime)
	{
		var fds = new int[2];
		if (Pipe(fds) != 0) { return null; }
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(offer, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(offer), 0, p, fds[1]); }
		finally { Marshal.FreeHGlobal(p); }
		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		var buf = new byte[65536];
		var sb = new StringBuilder();
		int n;
		while ((n = LibcRead(fds[0], buf, buf.Length)) > 0) { sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
		_ = WaylandBindings.close(fds[0]);
		return sb.Length > 0 ? sb.ToString() : null;
	}

	// No-op callbacks for all listener slots that must be non-null
	private static void NoopEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void NoopLeave(IntPtr d, IntPtr dd) { }
	private static void NoopMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void NoopDrop(IntPtr d, IntPtr dd) { }
	private static void NoopSourceActions(IntPtr d, IntPtr offer, uint sa) { }
	private static void NoopAction(IntPtr d, IntPtr offer, uint a) { }
	private static void NoopTarget(IntPtr d, IntPtr source, string mime) { }
	private static void NoopDndDropPerformed(IntPtr d, IntPtr source) { }
	private static void NoopDndFinished(IntPtr d, IntPtr source) { }
	private static void NoopSourceAction(IntPtr d, IntPtr source, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern unsafe int LibcWriteRaw(int fd, IntPtr buf, int count);

	private static unsafe int LibcWrite(int fd, byte[] buf, int offset, int count)
	{
		fixed (byte* p = &buf[offset]) { return LibcWriteRaw(fd, (IntPtr)p, count); }
	}
}
