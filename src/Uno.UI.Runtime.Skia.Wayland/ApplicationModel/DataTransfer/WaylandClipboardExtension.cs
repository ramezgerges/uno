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
/// Pure C# clipboard using wl_data_device protocol.
/// Ported from the working native C helper (uno-clipboard.c).
/// All protocol calls happen on the event thread.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	// Wayland objects — event thread only
	private IntPtr _wlDisplay;
	private IntPtr _wlSeat;
	private IntPtr _wlManager;
	private IntPtr _wlDevice;
	private IntPtr _currentOffer;
	private IntPtr _currentSource;
	private bool _hasSelection;
	private readonly List<string> _offerMimeTypes = new();

	// Copy state
	private string? _copyText;

	// Thread-safe UI<->event thread communication
	private volatile string? _cachedIncomingText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopyText;
	private volatile uint _lastSerial;
	private bool _initialized;

	// ALL delegates stored as static fields to prevent GC — matching C's static listeners
	private static readonly WlRegistryGlobalDelegate s_regGlobal = OnRegGlobal;
	private static readonly WlRegistryGlobalRemoveDelegate s_regRemove = OnRegRemove;
	private static readonly WlDataDeviceDataOfferDelegate s_devDataOffer = OnDevDataOffer;
	private static readonly WlDataDeviceEnterDelegate s_devEnter = OnDevEnter;
	private static readonly WlDataDeviceLeaveDelegate s_devLeave = OnDevLeave;
	private static readonly WlDataDeviceMotionDelegate s_devMotion = OnDevMotion;
	private static readonly WlDataDeviceDropDelegate s_devDrop = OnDevDrop;
	private static readonly WlDataDeviceSelectionDelegate s_devSelection = OnDevSelection;
	private static readonly WlDataOfferOfferDelegate s_offerOffer = OnOfferOffer;
	private static readonly WlDataOfferSourceActionsDelegate s_offerSA = OnOfferSA;
	private static readonly WlDataOfferActionDelegate s_offerAction = OnOfferAction;
	private static readonly WlDataSourceTargetDelegate s_srcTarget = OnSrcTarget;
	private static readonly WlDataSourceSendDelegate s_srcSend = OnSrcSend;
	private static readonly WlDataSourceCancelledDelegate s_srcCancelled = OnSrcCancelled;
	private static readonly WlDataSourceDndDropPerformedDelegate s_srcDDP = OnSrcDDP;
	private static readonly WlDataSourceDndFinishedDelegate s_srcDF = OnSrcDF;
	private static readonly WlDataSourceActionDelegate s_srcAction = OnSrcAction;

	// Pinned listener structs — allocated once, never freed
	private static GCHandle s_regListenerHandle;
	private static GCHandle s_devListenerHandle;
	private static GCHandle s_offerListenerHandle;
	private static GCHandle s_srcListenerHandle;

	static WaylandClipboardExtension()
	{
		var regL = new WlRegistryListener
		{
			global = Marshal.GetFunctionPointerForDelegate(s_regGlobal),
			global_remove = Marshal.GetFunctionPointerForDelegate(s_regRemove),
		};
		s_regListenerHandle = GCHandle.Alloc(regL, GCHandleType.Pinned);

		var devL = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(s_devDataOffer),
			enter = Marshal.GetFunctionPointerForDelegate(s_devEnter),
			leave = Marshal.GetFunctionPointerForDelegate(s_devLeave),
			motion = Marshal.GetFunctionPointerForDelegate(s_devMotion),
			drop = Marshal.GetFunctionPointerForDelegate(s_devDrop),
			selection = Marshal.GetFunctionPointerForDelegate(s_devSelection),
		};
		s_devListenerHandle = GCHandle.Alloc(devL, GCHandleType.Pinned);

		var offerL = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(s_offerOffer),
			source_actions = Marshal.GetFunctionPointerForDelegate(s_offerSA),
			action = Marshal.GetFunctionPointerForDelegate(s_offerAction),
		};
		s_offerListenerHandle = GCHandle.Alloc(offerL, GCHandleType.Pinned);

		var srcL = new WlDataSourceListener
		{
			target = Marshal.GetFunctionPointerForDelegate(s_srcTarget),
			send = Marshal.GetFunctionPointerForDelegate(s_srcSend),
			cancelled = Marshal.GetFunctionPointerForDelegate(s_srcCancelled),
			dnd_drop_performed = Marshal.GetFunctionPointerForDelegate(s_srcDDP),
			dnd_finished = Marshal.GetFunctionPointerForDelegate(s_srcDF),
			action = Marshal.GetFunctionPointerForDelegate(s_srcAction),
		};
		s_srcListenerHandle = GCHandle.Alloc(srcL, GCHandleType.Pinned);
	}

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	/// <summary>Called ONCE on the event thread. Uses host's already-bound objects.</summary>
	internal void InitOnEventThread(IntPtr wlDisplay, IntPtr wlSeat, IntPtr wlDataDeviceManager)
	{
		if (_initialized) { return; }
		if (wlSeat == IntPtr.Zero || wlDataDeviceManager == IntPtr.Zero) { return; }

		_wlDisplay = wlDisplay;
		_wlSeat = wlSeat;
		_wlManager = wlDataDeviceManager;

		// Create data device: get_data_device opcode=1
		_wlDevice = WaylandBindings.wl_proxy_marshal_flags(
			_wlManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_wlManager), 0,
			IntPtr.Zero, _wlSeat);

		if (_wlDevice == IntPtr.Zero) { return; }

		_ = WaylandBindings.wl_proxy_add_listener(_wlDevice, s_devListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		_initialized = true;
	}

	/// <summary>Called each event loop iteration on the event thread.</summary>
	internal void ProcessOnEventThread()
	{
		if (!_initialized) { return; }

		if (_pasteRequested)
		{
			_pasteRequested = false;
			_cachedIncomingText = ReadOfferText();
		}

		var text = Interlocked.Exchange(ref _pendingCopyText, null);
		if (text != null)
		{
			DoCopy(text);
		}
	}

	// --- UI thread (no Wayland calls) ---

	public void Clear() { _copiedText = null; _cachedIncomingText = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }

	public DataPackageView? GetContent()
	{
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) { Thread.Sleep(20); }
		var text = _cachedIncomingText ?? _copiedText;
		if (text != null) { var p = new DataPackage(); p.SetText(text); return p.GetView(); }
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null) { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }
		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (text != null) { _copiedText = text; _cachedIncomingText = null; _pendingCopyText = text; }
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// --- Event thread operations ---

	private string? ReadOfferText()
	{
		if (_currentOffer == IntPtr.Zero || !_hasSelection) { return null; }

		string? mime = null;
		if (_offerMimeTypes.Contains("text/plain;charset=utf-8")) { mime = "text/plain;charset=utf-8"; }
		else if (_offerMimeTypes.Contains("text/plain")) { mime = "text/plain"; }
		if (mime == null) { return null; }

		var fds = new int[2];
		if (Pipe(fds) != 0) { return null; }

		// wl_data_offer.receive opcode=0
		var mimePtr = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(_currentOffer, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_currentOffer), 0, mimePtr, fds[1]); }
		finally { Marshal.FreeHGlobal(mimePtr); }

		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		var buf = new byte[65536];
		var sb = new StringBuilder();
		int n;
		while ((n = LibcRead(fds[0], buf, buf.Length)) > 0) { sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
		_ = WaylandBindings.close(fds[0]);
		return sb.Length > 0 ? sb.ToString() : null;
	}

	private void DoCopy(string text)
	{
		if (_wlManager == IntPtr.Zero || _wlDevice == IntPtr.Zero) { return; }

		if (_currentSource != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(_currentSource);
			_currentSource = IntPtr.Zero;
		}

		_copyText = text;

		// create_data_source opcode=0
		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_wlManager, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_wlManager), 0, IntPtr.Zero);

		if (_currentSource == IntPtr.Zero) { return; }

		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, s_srcListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		OfferMime(_currentSource, "text/plain;charset=utf-8");
		OfferMime(_currentSource, "text/plain");

		// set_selection opcode=1
		WaylandBindings.wl_proxy_marshal_flags(
			_wlDevice, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_wlDevice), 0,
			_currentSource, (IntPtr)_lastSerial);

		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	private static void OfferMime(IntPtr source, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(source, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(source), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	// --- Static callbacks (matching C's static listener functions) ---

	private static void OnRegGlobal(IntPtr data, IntPtr reg, uint name, string iface, uint version)
	{
		var self = Instance;
		if (iface == "wl_seat" && self._wlSeat == IntPtr.Zero)
		{
			self._wlSeat = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_seat_interface, 1);
		}
		else if (iface == "wl_data_device_manager" && self._wlManager == IntPtr.Zero)
		{
			self._wlManager = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
		}
	}
	private static void OnRegRemove(IntPtr data, IntPtr reg, uint name) { }

	private static void OnDevDataOffer(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		if (self._currentOffer != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(self._currentOffer);
			self._offerMimeTypes.Clear();
		}
		self._currentOffer = offer;
		self._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, s_offerListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
	}

	private static void OnDevSelection(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		self._hasSelection = true;
		if (offer == IntPtr.Zero)
		{
			if (self._currentOffer != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(self._currentOffer);
				self._currentOffer = IntPtr.Zero;
			}
			self._offerMimeTypes.Clear();
		}
	}

	private static void OnDevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void OnDevLeave(IntPtr d, IntPtr dd) { }
	private static void OnDevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void OnDevDrop(IntPtr d, IntPtr dd) { }

	private static void OnOfferOffer(IntPtr data, IntPtr offer, string mime) => Instance._offerMimeTypes.Add(mime);
	private static void OnOfferSA(IntPtr data, IntPtr offer, uint sa) { }
	private static void OnOfferAction(IntPtr data, IntPtr offer, uint a) { }

	private static void OnSrcTarget(IntPtr data, IntPtr src, string mime) { }
	private static unsafe void OnSrcSend(IntPtr data, IntPtr src, string mime, int fd)
	{
		var text = Instance._copyText;
		if (text != null)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			int written = 0;
			while (written < bytes.Length)
			{
				fixed (byte* p = &bytes[written])
				{
					var n = LibcWriteRaw(fd, (IntPtr)p, bytes.Length - written);
					if (n <= 0) { break; }
					written += n;
				}
			}
		}
		_ = WaylandBindings.close(fd);
	}
	private static void OnSrcCancelled(IntPtr data, IntPtr src)
	{
		var self = Instance;
		if (src == self._currentSource)
		{
			WaylandBindings.wl_proxy_destroy(self._currentSource);
			self._currentSource = IntPtr.Zero;
		}
	}
	private static void OnSrcDDP(IntPtr data, IntPtr src) { }
	private static void OnSrcDF(IntPtr data, IntPtr src) { }
	private static void OnSrcAction(IntPtr data, IntPtr src, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern unsafe int LibcWriteRaw(int fd, IntPtr buf, int count);
}
