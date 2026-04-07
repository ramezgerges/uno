using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private bool _initialized;

	// Clipboard state (event thread only)
	private IntPtr _currentOffer;
	private bool _hasSelection;
	private readonly List<string> _mimeTypes = new();
	private IntPtr _currentSource;
	private string? _copyText;

	// Thread-safe communication
	private volatile string? _cachedIncoming;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopy;
	private volatile uint _lastSerial;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		// Registry listener
		var regFuncs = new IntPtr[2];
		regFuncs[0] = Marshal.GetFunctionPointerForDelegate(_regGlobal ??= RegGlobal);
		regFuncs[1] = Marshal.GetFunctionPointerForDelegate(_regRemove ??= RegRemove);
		_regPtr = Marshal.AllocHGlobal(2 * IntPtr.Size);
		Marshal.WriteIntPtr(_regPtr, 0, regFuncs[0]);
		Marshal.WriteIntPtr(_regPtr, IntPtr.Size, regFuncs[1]);

		var reg = WaylandBindings.wl_display_get_registry(wlDisplay);
		_ = WaylandBindings.wl_proxy_add_listener(reg, _regPtr, IntPtr.Zero);
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		if (_seat == IntPtr.Zero || _mgr == IntPtr.Zero) return;

		// Seat listener
		var seatFuncs = new IntPtr[2];
		seatFuncs[0] = Marshal.GetFunctionPointerForDelegate(_seatCaps ??= SeatCaps);
		seatFuncs[1] = Marshal.GetFunctionPointerForDelegate(_seatName ??= SeatName);
		_seatPtr = Marshal.AllocHGlobal(2 * IntPtr.Size);
		Marshal.WriteIntPtr(_seatPtr, 0, seatFuncs[0]);
		Marshal.WriteIntPtr(_seatPtr, IntPtr.Size, seatFuncs[1]);
		_ = WaylandBindings.wl_proxy_add_listener(_seat, _seatPtr, IntPtr.Zero);

		// Device
		_dev = WaylandBindings.wl_proxy_marshal_flags(
			_mgr, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_mgr), 0, IntPtr.Zero, _seat);
		if (_dev == IntPtr.Zero) return;

		// Device listener
		var devFuncs = new IntPtr[6];
		devFuncs[0] = Marshal.GetFunctionPointerForDelegate(_devOffer ??= DevOffer);
		devFuncs[1] = Marshal.GetFunctionPointerForDelegate(_devEnter ??= DevEnter);
		devFuncs[2] = Marshal.GetFunctionPointerForDelegate(_devLeave ??= DevLeave);
		devFuncs[3] = Marshal.GetFunctionPointerForDelegate(_devMotion ??= DevMotion);
		devFuncs[4] = Marshal.GetFunctionPointerForDelegate(_devDrop ??= DevDrop);
		devFuncs[5] = Marshal.GetFunctionPointerForDelegate(_devSel ??= DevSel);
		_devPtr = Marshal.AllocHGlobal(6 * IntPtr.Size);
		for (int i = 0; i < 6; i++) Marshal.WriteIntPtr(_devPtr, i * IntPtr.Size, devFuncs[i]);
		_ = WaylandBindings.wl_proxy_add_listener(_dev, _devPtr, IntPtr.Zero);

		// Offer listener (pre-created, attached in DevOffer callback)
		var offerFuncs = new IntPtr[3];
		offerFuncs[0] = Marshal.GetFunctionPointerForDelegate(_offOffer ??= OffOffer);
		offerFuncs[1] = Marshal.GetFunctionPointerForDelegate(_offSA ??= OffSA);
		offerFuncs[2] = Marshal.GetFunctionPointerForDelegate(_offAct ??= OffAct);
		_offerPtr = Marshal.AllocHGlobal(3 * IntPtr.Size);
		for (int i = 0; i < 3; i++) Marshal.WriteIntPtr(_offerPtr, i * IntPtr.Size, offerFuncs[i]);

		// Source listener will be created lazily in DoCopy

		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		_initialized = true;
	}

	internal void ProcessOnEventThread()
	{
		if (!_initialized) return;
		if (_pasteRequested)
		{
			_pasteRequested = false;
			_cachedIncoming = ReadOffer();
		}
		var text = Interlocked.Exchange(ref _pendingCopy, null);
		if (text != null) DoCopy(text);
	}

	public void Clear() { _copiedText = null; _cachedIncoming = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }

	public DataPackageView? GetContent()
	{
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) Thread.Sleep(20);
		var t = _cachedIncoming ?? _copiedText;
		if (t != null) { var p = new DataPackage(); p.SetText(t); return p.GetView(); }
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null) { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }
		try
		{
			var v = content.GetView();
			if (v.Contains(StandardDataFormats.Text))
			{
				var t = v.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (t != null) { _copiedText = t; _cachedIncoming = null; _pendingCopy = t; }
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// --- Event thread operations ---

	private string? ReadOffer()
	{
		if (_currentOffer == IntPtr.Zero || !_hasSelection) return null;
		string? mime = _mimeTypes.Contains("text/plain;charset=utf-8") ? "text/plain;charset=utf-8"
			: _mimeTypes.Contains("text/plain") ? "text/plain" : null;
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
		if (_mgr == IntPtr.Zero || _dev == IntPtr.Zero) return;
		if (_currentSource != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(_currentSource); _currentSource = IntPtr.Zero; }
		_copyText = text;
		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_mgr, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_mgr), 0, IntPtr.Zero);
		if (_currentSource == IntPtr.Zero) return;
		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, _srcPtr, IntPtr.Zero);
		OfferMime(_currentSource, "text/plain;charset=utf-8");
		OfferMime(_currentSource, "text/plain");
		WaylandBindings.wl_proxy_marshal_flags(_dev, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_dev), 0, _currentSource, (IntPtr)_lastSerial);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	private static void OfferMime(IntPtr src, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(src, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(src), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	// State
	private IntPtr _seat, _mgr, _dev;
	private IntPtr _regPtr, _seatPtr, _devPtr, _offerPtr, _srcPtr;

	// Delegate storage
	private static WlRegistryGlobalDelegate? _regGlobal;
	private static WlRegistryGlobalRemoveDelegate? _regRemove;
	private static WlSeatCapabilitiesDelegate? _seatCaps;
	private static WlSeatNameDelegate? _seatName;
	private static WlDataDeviceDataOfferDelegate? _devOffer;
	private static WlDataDeviceEnterDelegate? _devEnter;
	private static WlDataDeviceLeaveDelegate? _devLeave;
	private static WlDataDeviceMotionDelegate? _devMotion;
	private static WlDataDeviceDropDelegate? _devDrop;
	private static WlDataDeviceSelectionDelegate? _devSel;
	private static WlDataOfferOfferDelegate? _offOffer;
	private static WlDataOfferSourceActionsDelegate? _offSA;
	private static WlDataOfferActionDelegate? _offAct;
	private static WlDataSourceTargetDelegate? _srcTarget;
	private static WlDataSourceSendDelegate? _srcSend;
	private static WlDataSourceCancelledDelegate? _srcCancel;
	private static WlDataSourceDndDropPerformedDelegate? _srcDDP;
	private static WlDataSourceDndFinishedDelegate? _srcDF;
	private static WlDataSourceActionDelegate? _srcAct;

	// Callbacks
	private static void RegGlobal(IntPtr data, IntPtr reg, uint name, string iface, uint version)
	{
		var self = Instance;
		if (iface == "wl_seat" && self._seat == IntPtr.Zero)
			self._seat = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_seat_interface, 1);
		else if (iface == "wl_data_device_manager" && self._mgr == IntPtr.Zero)
			self._mgr = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
	}
	private static void RegRemove(IntPtr data, IntPtr reg, uint name) { }
	private static void SeatCaps(IntPtr data, IntPtr seat, uint caps) { }
	private static void SeatName(IntPtr data, IntPtr seat, string n) { }
	private static void DevOffer(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		if (self._currentOffer != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(self._currentOffer);
			self._mimeTypes.Clear();
		}
		self._currentOffer = offer;
		self._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, self._offerPtr, IntPtr.Zero);
	}
	private static void DevSel(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		self._hasSelection = true;
		if (offer == IntPtr.Zero)
		{
			if (self._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(self._currentOffer); self._currentOffer = IntPtr.Zero; }
			self._mimeTypes.Clear();
		}
	}
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void DevLeave(IntPtr d, IntPtr dd) { }
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void DevDrop(IntPtr d, IntPtr dd) { }
	private static void OffOffer(IntPtr data, IntPtr offer, string mime) { Instance._mimeTypes.Add(mime); }
	private static void OffSA(IntPtr data, IntPtr offer, uint sa) { }
	private static void OffAct(IntPtr data, IntPtr offer, uint a) { }
	private static void SrcTarget(IntPtr data, IntPtr src, string mime) { }
	private static unsafe void SrcSend(IntPtr data, IntPtr src, string mime, int fd)
	{
		var t = Instance._copyText;
		if (t != null)
		{
			var b = Encoding.UTF8.GetBytes(t);
			fixed (byte* p = b)
			{
				int w = 0;
				while (w < b.Length) { var n = LibcWritePtr(fd, (IntPtr)(p + w), b.Length - w); if (n <= 0) break; w += n; }
			}
		}
		_ = WaylandBindings.close(fd);
	}
	private static void SrcCancel(IntPtr data, IntPtr src)
	{
		var self = Instance;
		if (src == self._currentSource) { WaylandBindings.wl_proxy_destroy(src); self._currentSource = IntPtr.Zero; }
	}
	private static void SrcDDP(IntPtr data, IntPtr src) { }
	private static void SrcDF(IntPtr data, IntPtr src) { }
	private static void SrcAct(IntPtr data, IntPtr src, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int LibcWritePtr(int fd, IntPtr buf, int count);
}
