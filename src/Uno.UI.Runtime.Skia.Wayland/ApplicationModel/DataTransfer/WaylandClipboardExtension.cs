using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using wl_data_device. ALL Wayland protocol calls happen on the event thread.
/// The UI thread only reads/writes cached strings.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	// Wayland objects — only touched from event thread
	private IntPtr _wlDisplay;
	private IntPtr _wlDataDevice;
	private IntPtr _wlDataDeviceManager;
	private IntPtr _currentOffer;
	private IntPtr _currentSource;
	private readonly List<string> _offerMimeTypes = new();

	// Delegates — prevent GC
	private WlDataDeviceDataOfferDelegate? _ddDataOffer;
	private WlDataDeviceSelectionDelegate? _ddSelection;
	private WlDataOfferOfferDelegate? _doOffer;
	private WlDataSourceSendDelegate? _dsSend;
	private WlDataSourceCancelledDelegate? _dsCancelled;
	private GCHandle _ddListenerHandle;
	private GCHandle _doListenerHandle;
	private GCHandle _dsListenerHandle;
	private bool _doListenerReady;

	// Thread-safe cached text — written by event thread, read by UI thread
	private volatile string? _incomingText;  // from other apps
	private volatile string? _outgoingText;  // text we want to copy

	// For queuing SetContent work to event thread
	private volatile string? _pendingCopyText;

	// Reference to event loop's display for flush
	private bool _initialized;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	/// <summary>
	/// Called from the EVENT THREAD at startup.
	/// </summary>
	internal void InitializeOnEventThread(IntPtr wlDisplay, IntPtr wlDataDeviceManager, IntPtr wlSeat)
	{
		_wlDisplay = wlDisplay;
		_wlDataDeviceManager = wlDataDeviceManager;

		_ddDataOffer = OnDataOffer;
		_ddSelection = OnSelection;
		_doOffer = OnOfferMimeType;
		_dsSend = OnSourceSend;
		_dsCancelled = OnSourceCancelled;

		_wlDataDevice = WaylandBindings.wl_proxy_marshal_flags(
			wlDataDeviceManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(wlDataDeviceManager), 0,
			IntPtr.Zero, wlSeat);

		if (_wlDataDevice == IntPtr.Zero) { return; }

		var ddListener = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(_ddDataOffer),
			enter = IntPtr.Zero,
			leave = IntPtr.Zero,
			motion = IntPtr.Zero,
			drop = IntPtr.Zero,
			selection = Marshal.GetFunctionPointerForDelegate(_ddSelection),
		};
		_ddListenerHandle = GCHandle.Alloc(ddListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlDataDevice, _ddListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		var doListener = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(_doOffer),
			source_actions = IntPtr.Zero,
			action = IntPtr.Zero,
		};
		_doListenerHandle = GCHandle.Alloc(doListener, GCHandleType.Pinned);
		_doListenerReady = true;
		_initialized = true;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug("Wayland clipboard initialized on event thread");
		}
	}

	/// <summary>
	/// Called from the EVENT THREAD's dispatch loop to process pending copy requests.
	/// </summary>
	internal void ProcessPendingOnEventThread()
	{
		var text = Interlocked.Exchange(ref _pendingCopyText, null);
		if (text == null || !_initialized) { return; }

		_outgoingText = text;

		if (_currentSource != IntPtr.Zero)
		{
			if (_dsListenerHandle.IsAllocated) { _dsListenerHandle.Free(); }
			WaylandBindings.wl_proxy_destroy(_currentSource);
		}

		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDeviceManager, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0, IntPtr.Zero);

		if (_currentSource == IntPtr.Zero) { return; }

		var dsListener = new WlDataSourceListener
		{
			target = IntPtr.Zero,
			send = Marshal.GetFunctionPointerForDelegate(_dsSend!),
			cancelled = Marshal.GetFunctionPointerForDelegate(_dsCancelled!),
			dnd_drop_performed = IntPtr.Zero,
			dnd_finished = IntPtr.Zero,
			action = IntPtr.Zero,
		};
		_dsListenerHandle = GCHandle.Alloc(dsListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, _dsListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		OfferMime(_currentSource, "text/plain;charset=utf-8");
		OfferMime(_currentSource, "text/plain");

		WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDevice, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_wlDataDevice), 0,
			_currentSource, IntPtr.Zero);

		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	// --- UI thread methods (no Wayland calls) ---

	public void Clear()
	{
		_outgoingText = null;
		_incomingText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

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
			var text = view.Contains(StandardDataFormats.Text)
				? view.GetTextAsync().AsTask().GetAwaiter().GetResult()
				: null;
			if (text != null)
			{
				_outgoingText = text;
				_incomingText = null;
				// Queue the Wayland work for the event thread
				_pendingCopyText = text;
			}
		}
		catch { }

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// --- Helpers (event thread only) ---

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
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(offer, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(offer), 0, p, fds[1]);
		}
		finally { Marshal.FreeHGlobal(p); }
		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		var buf = new byte[65536];
		var sb = new StringBuilder();
		int n;
		while ((n = Read(fds[0], buf, buf.Length)) > 0)
		{
			sb.Append(Encoding.UTF8.GetString(buf, 0, n));
		}
		_ = WaylandBindings.close(fds[0]);
		return sb.Length > 0 ? sb.ToString() : null;
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

		// Eagerly read clipboard on this (event) thread
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
			_ = Write(fd, bytes, bytes.Length);
		}
		_ = WaylandBindings.close(fd);
	}

	private void OnSourceCancelled(IntPtr data, IntPtr source)
	{
		if (source == _currentSource) { _currentSource = IntPtr.Zero; }
		WaylandBindings.wl_proxy_destroy(source);
	}

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int Read(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int Write(int fd, byte[] buf, int count);
}
