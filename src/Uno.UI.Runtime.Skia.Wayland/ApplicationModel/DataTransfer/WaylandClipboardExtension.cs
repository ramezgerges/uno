using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Pure C# clipboard. Creates its own v1 seat and v1 data_device_manager
/// via a second registry on the app's display (same as the C helper does).
/// All calls on the event thread.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private IntPtr _mySeat;       // Our own v1 seat (separate from host's v5 seat)
	private IntPtr _myManager;    // Our own v1 manager
	private IntPtr _myDevice;     // Our data device
	private IntPtr _currentOffer;
	private IntPtr _currentSource;
	private bool _hasSelection;
	private readonly List<string> _offerMimeTypes = new();
	private string? _copyText;

	private volatile string? _cachedIncomingText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopyText;
	private volatile uint _lastSerial;
	private bool _initialized;

	// Static delegates — never GC'd
	private static WlRegistryGlobalDelegate? s_regGlobal;
	private static WlRegistryGlobalRemoveDelegate? s_regRemove;
	private static WlSeatCapabilitiesDelegate? s_seatCaps;
	private static WlSeatNameDelegate? s_seatName;
	private static GCHandle s_seatLH;
	private static WlDataDeviceDataOfferDelegate? s_devOffer;
	private static WlDataDeviceEnterDelegate? s_devEnter;
	private static WlDataDeviceLeaveDelegate? s_devLeave;
	private static WlDataDeviceMotionDelegate? s_devMotion;
	private static WlDataDeviceDropDelegate? s_devDrop;
	private static WlDataDeviceSelectionDelegate? s_devSel;
	private static WlDataOfferOfferDelegate? s_offerOffer;
	private static WlDataOfferSourceActionsDelegate? s_offerSA;
	private static WlDataOfferActionDelegate? s_offerAct;
	private static WlDataSourceTargetDelegate? s_srcTarget;
	private static WlDataSourceSendDelegate? s_srcSend;
	private static WlDataSourceCancelledDelegate? s_srcCancel;
	private static WlDataSourceDndDropPerformedDelegate? s_srcDDP;
	private static WlDataSourceDndFinishedDelegate? s_srcDF;
	private static WlDataSourceActionDelegate? s_srcAct;

	private static GCHandle s_regLH, s_devLH, s_offerLH, s_srcLH;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	private const string Lib = "libuno-clipboard";
	[DllImport(Lib)] private static extern int uno_clipboard_init(IntPtr wlDisplay);
	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_text();
	[DllImport(Lib)] private static extern int uno_clipboard_set_text([MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint serial);
	[DllImport(Lib)] private static extern void uno_clipboard_free(IntPtr ptr);

	private bool _useNative;

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		// Test: use C helper for init, C# for get/set
		// If this still crashes, the issue is NOT our C# init code
		var cResult = uno_clipboard_init(wlDisplay);
		Console.Error.WriteLine($"[Clipboard] C init={cResult}, now trying C# init...");
		if (cResult == 0)
		{
			_useNative = true;
			_initialized = true;
			return;
		}

		// Create delegates
		s_regGlobal = RegGlobal;
		s_regRemove = RegRemove;
		s_devOffer = DevOffer;
		s_devEnter = DevEnter;
		s_devLeave = DevLeave;
		s_devMotion = DevMotion;
		s_devDrop = DevDrop;
		s_devSel = DevSelection;
		s_offerOffer = OfferOffer;
		s_offerSA = OfferSA;
		s_offerAct = OfferAct;
		s_srcTarget = SrcTarget;
		s_srcSend = SrcSend;
		s_srcCancel = SrcCancel;
		s_srcDDP = SrcDDP;
		s_srcDF = SrcDF;
		s_srcAct = SrcAct;

		// Pin listener structs
		var regL = new WlRegistryListener
		{
			global = Marshal.GetFunctionPointerForDelegate(s_regGlobal),
			global_remove = Marshal.GetFunctionPointerForDelegate(s_regRemove),
		};
		s_regLH = GCHandle.Alloc(regL, GCHandleType.Pinned);

		var devL = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(s_devOffer),
			enter = Marshal.GetFunctionPointerForDelegate(s_devEnter),
			leave = Marshal.GetFunctionPointerForDelegate(s_devLeave),
			motion = Marshal.GetFunctionPointerForDelegate(s_devMotion),
			drop = Marshal.GetFunctionPointerForDelegate(s_devDrop),
			selection = Marshal.GetFunctionPointerForDelegate(s_devSel),
		};
		s_devLH = GCHandle.Alloc(devL, GCHandleType.Pinned);

		var offerL = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(s_offerOffer),
			source_actions = Marshal.GetFunctionPointerForDelegate(s_offerSA),
			action = Marshal.GetFunctionPointerForDelegate(s_offerAct),
		};
		s_offerLH = GCHandle.Alloc(offerL, GCHandleType.Pinned);

		var srcL = new WlDataSourceListener
		{
			target = Marshal.GetFunctionPointerForDelegate(s_srcTarget),
			send = Marshal.GetFunctionPointerForDelegate(s_srcSend),
			cancelled = Marshal.GetFunctionPointerForDelegate(s_srcCancel),
			dnd_drop_performed = Marshal.GetFunctionPointerForDelegate(s_srcDDP),
			dnd_finished = Marshal.GetFunctionPointerForDelegate(s_srcDF),
			action = Marshal.GetFunctionPointerForDelegate(s_srcAct),
		};
		s_srcLH = GCHandle.Alloc(srcL, GCHandleType.Pinned);

		// Create our own registry + v1 bindings (like the C helper)
		var reg = WaylandBindings.wl_display_get_registry(wlDisplay);
		_ = WaylandBindings.wl_proxy_add_listener(reg, s_regLH.AddrOfPinnedObject(), IntPtr.Zero);
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);

		Console.Error.WriteLine($"[Clipboard C#] seat={_mySeat} manager={_myManager}");
		if (_mySeat == IntPtr.Zero || _myManager == IntPtr.Zero) return;

		// Create data device
		_myDevice = WaylandBindings.wl_proxy_marshal_flags(
			_myManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_myManager), 0,
			IntPtr.Zero, _mySeat);
		Console.Error.WriteLine($"[Clipboard C#] device={_myDevice}");
		if (_myDevice == IntPtr.Zero) return;

		_ = WaylandBindings.wl_proxy_add_listener(_myDevice, s_devLH.AddrOfPinnedObject(), IntPtr.Zero);
		Console.Error.WriteLine("[Clipboard C#] listener added, doing roundtrip...");

		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		Console.Error.WriteLine($"[Clipboard C#] offer={_currentOffer} hasSel={_hasSelection} mimes={_offerMimeTypes.Count}");

		_initialized = true;
	}

	internal void ProcessOnEventThread()
	{
		if (!_initialized) return;

		if (_useNative)
		{
			if (_pasteRequested)
			{
				_pasteRequested = false;
				var ptr = uno_clipboard_get_text();
				if (ptr != IntPtr.Zero) { _cachedIncomingText = Marshal.PtrToStringUTF8(ptr); uno_clipboard_free(ptr); }
				else { _cachedIncomingText = null; }
			}
			var copyText = Interlocked.Exchange(ref _pendingCopyText, null);
			if (copyText != null) { _ = uno_clipboard_set_text(copyText, _lastSerial); }
			return;
		}

		if (_pasteRequested)
		{
			_pasteRequested = false;
			_cachedIncomingText = ReadOfferText();
		}

		var text = Interlocked.Exchange(ref _pendingCopyText, null);
		if (text != null) DoCopy(text);
	}

	public void Clear() { _copiedText = null; _cachedIncomingText = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }

	public DataPackageView? GetContent()
	{
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) Thread.Sleep(20);
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

	private string? ReadOfferText()
	{
		if (_currentOffer == IntPtr.Zero || !_hasSelection) return null;
		string? mime = null;
		if (_offerMimeTypes.Contains("text/plain;charset=utf-8")) mime = "text/plain;charset=utf-8";
		else if (_offerMimeTypes.Contains("text/plain")) mime = "text/plain";
		if (mime == null) return null;

		var fds = new int[2];
		if (Pipe(fds) != 0) return null;
		var mp = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(_currentOffer, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_currentOffer), 0, mp, fds[1]); }
		finally { Marshal.FreeHGlobal(mp); }
		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		var buf = new byte[65536]; var sb = new StringBuilder(); int n;
		while ((n = LibcRead(fds[0], buf, buf.Length)) > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
		_ = WaylandBindings.close(fds[0]);
		return sb.Length > 0 ? sb.ToString() : null;
	}

	private void DoCopy(string text)
	{
		if (_myManager == IntPtr.Zero || _myDevice == IntPtr.Zero) return;
		if (_currentSource != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(_currentSource); _currentSource = IntPtr.Zero; }
		_copyText = text;

		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_myManager, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_myManager), 0, IntPtr.Zero);
		if (_currentSource == IntPtr.Zero) return;

		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, s_srcLH.AddrOfPinnedObject(), IntPtr.Zero);
		OfferMime(_currentSource, "text/plain;charset=utf-8");
		OfferMime(_currentSource, "text/plain");
		WaylandBindings.wl_proxy_marshal_flags(_myDevice, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_myDevice), 0, _currentSource, (IntPtr)_lastSerial);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	private static void OfferMime(IntPtr src, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(src, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(src), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	// --- Callbacks (static to match C pattern) ---
	private static void RegGlobal(IntPtr data, IntPtr reg, uint name, string iface, uint version)
	{
		var self = Instance;
		if (iface == "wl_seat" && self._mySeat == IntPtr.Zero)
		{
			self._mySeat = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_seat_interface, 1);
			// Must add a listener — dispatcher segfaults on proxies without listeners
			if (s_seatCaps == null)
			{
				s_seatCaps = SeatCaps;
				s_seatName = SeatName;
				var seatL = new WlSeatListener
				{
					capabilities = Marshal.GetFunctionPointerForDelegate(s_seatCaps),
					name = Marshal.GetFunctionPointerForDelegate(s_seatName!),
				};
				s_seatLH = GCHandle.Alloc(seatL, GCHandleType.Pinned);
			}
			_ = WaylandBindings.wl_proxy_add_listener(self._mySeat, s_seatLH.AddrOfPinnedObject(), IntPtr.Zero);
		}
		else if (iface == "wl_data_device_manager" && self._myManager == IntPtr.Zero)
			self._myManager = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
	}
	private static void SeatCaps(IntPtr data, IntPtr seat, uint caps) { }
	private static void SeatName(IntPtr data, IntPtr seat, string name) { }
	private static void RegRemove(IntPtr data, IntPtr reg, uint name) { }

	private static void DevOffer(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		Console.Error.WriteLine($"[Clipboard C#] DevOffer: offer={offer}");
		if (self._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(self._currentOffer); self._offerMimeTypes.Clear(); }
		self._currentOffer = offer;
		self._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, s_offerLH.AddrOfPinnedObject(), IntPtr.Zero);
	}
	private static void DevSelection(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		Console.Error.WriteLine($"[Clipboard C#] DevSelection: offer={offer}");
		self._hasSelection = true;
		if (offer == IntPtr.Zero) { if (self._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(self._currentOffer); self._currentOffer = IntPtr.Zero; } self._offerMimeTypes.Clear(); }
	}
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void DevLeave(IntPtr d, IntPtr dd) { }
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void DevDrop(IntPtr d, IntPtr dd) { }

	private static void OfferOffer(IntPtr data, IntPtr offer, string mime)
	{
		Console.Error.WriteLine($"[Clipboard C#] OfferOffer: mime={mime}");
		Instance._offerMimeTypes.Add(mime);
	}
	private static void OfferSA(IntPtr data, IntPtr offer, uint sa) { }
	private static void OfferAct(IntPtr data, IntPtr offer, uint a) { }

	private static void SrcTarget(IntPtr data, IntPtr src, string mime) { }
	private static unsafe void SrcSend(IntPtr data, IntPtr src, string mime, int fd)
	{
		var text = Instance._copyText;
		if (text != null)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			fixed (byte* p = bytes) { int w = 0; while (w < bytes.Length) { var n = LibcWritePtr(fd, (IntPtr)(p + w), bytes.Length - w); if (n <= 0) break; w += n; } }
		}
		_ = WaylandBindings.close(fd);
	}
	private static void SrcCancel(IntPtr data, IntPtr src) { var self = Instance; if (src == self._currentSource) { WaylandBindings.wl_proxy_destroy(src); self._currentSource = IntPtr.Zero; } }
	private static void SrcDDP(IntPtr data, IntPtr src) { }
	private static void SrcDF(IntPtr data, IntPtr src) { }
	private static void SrcAct(IntPtr data, IntPtr src, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int LibcWritePtr(int fd, IntPtr buf, int count);
}
