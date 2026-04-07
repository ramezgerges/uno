using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Pure C# clipboard via wl_data_device. Listener structs allocated in
/// unmanaged memory via Marshal.AllocHGlobal. No C helper needed.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private IntPtr _seat, _mgr, _dev;
	private IntPtr _currentOffer, _currentSource;
	private bool _hasSelection;
	private readonly List<string> _mimeTypes = new();
	private string? _copyText;

	private volatile string? _cachedIncoming;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopy;
	private volatile uint _lastSerial;
	private bool _initialized;

	// Unmanaged listener pointers
	private IntPtr _regPtr, _seatPtr, _devPtr, _offerPtr, _srcPtr;

	// Delegate storage (prevent GC)
	private static WlRegistryGlobalDelegate? _dRegG;
	private static WlRegistryGlobalRemoveDelegate? _dRegR;
	private static WlSeatCapabilitiesDelegate? _dSeatC;
	private static WlSeatNameDelegate? _dSeatN;
	private static WlDataDeviceDataOfferDelegate? _dDevO;
	private static WlDataDeviceEnterDelegate? _dDevE;
	private static WlDataDeviceLeaveDelegate? _dDevL;
	private static WlDataDeviceMotionDelegate? _dDevM;
	private static WlDataDeviceDropDelegate? _dDevD;
	private static WlDataDeviceSelectionDelegate? _dDevS;
	private static WlDataOfferOfferDelegate? _dOffO;
	private static WlDataOfferSourceActionsDelegate? _dOffSA;
	private static WlDataOfferActionDelegate? _dOffA;
	private static WlDataSourceTargetDelegate? _dSrcT;
	private static WlDataSourceSendDelegate? _dSrcS;
	private static WlDataSourceCancelledDelegate? _dSrcC;
	private static WlDataSourceDndDropPerformedDelegate? _dSrcDDP;
	private static WlDataSourceDndFinishedDelegate? _dSrcDF;
	private static WlDataSourceActionDelegate? _dSrcA;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	private static IntPtr MakeListener(params IntPtr[] ptrs)
	{
		var p = Marshal.AllocHGlobal(ptrs.Length * IntPtr.Size);
		for (int i = 0; i < ptrs.Length; i++)
			Marshal.WriteIntPtr(p, i * IntPtr.Size, ptrs[i]);
		return p;
	}

	private static IntPtr FP<T>(ref T? field, T value) where T : Delegate
	{
		field = value;
		return Marshal.GetFunctionPointerForDelegate(field);
	}

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		_regPtr = MakeListener(FP(ref _dRegG, RegGlobal), FP(ref _dRegR, RegRemove));
		_seatPtr = MakeListener(FP(ref _dSeatC, SeatCaps), FP(ref _dSeatN, SeatName));
		_devPtr = MakeListener(
			FP(ref _dDevO, DevOffer), FP(ref _dDevE, DevEnter), FP(ref _dDevL, DevLeave),
			FP(ref _dDevM, DevMotion), FP(ref _dDevD, DevDrop), FP(ref _dDevS, DevSel));
		_offerPtr = MakeListener(FP(ref _dOffO, OffOffer), FP(ref _dOffSA, OffSA), FP(ref _dOffA, OffAct));
		_srcPtr = MakeListener(
			FP(ref _dSrcT, SrcTarget), FP(ref _dSrcS, SrcSend), FP(ref _dSrcC, SrcCancel),
			FP(ref _dSrcDDP, SrcDDP), FP(ref _dSrcDF, SrcDF), FP(ref _dSrcA, SrcAct));

		var reg = WaylandBindings.wl_display_get_registry(wlDisplay);
		_ = WaylandBindings.wl_proxy_add_listener(reg, _regPtr, IntPtr.Zero);
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		if (_seat == IntPtr.Zero || _mgr == IntPtr.Zero) return;

		_ = WaylandBindings.wl_proxy_add_listener(_seat, _seatPtr, IntPtr.Zero);

		_dev = WaylandBindings.wl_proxy_marshal_flags(
			_mgr, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_mgr), 0, IntPtr.Zero, _seat);
		if (_dev == IntPtr.Zero) return;

		_ = WaylandBindings.wl_proxy_add_listener(_dev, _devPtr, IntPtr.Zero);
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

	private string? ReadOffer()
	{
		if (_currentOffer == IntPtr.Zero || !_hasSelection) return null;
		string? mime = null;
		if (_mimeTypes.Contains("text/plain;charset=utf-8")) mime = "text/plain;charset=utf-8";
		else if (_mimeTypes.Contains("text/plain")) mime = "text/plain";
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

	// --- Callbacks ---
	private static void RegGlobal(IntPtr d, IntPtr r, uint n, string iface, uint v)
	{
		var s = Instance;
		if (iface == "wl_seat" && s._seat == IntPtr.Zero)
			s._seat = WaylandBindings.wl_registry_bind(r, n, WaylandInterfaces.wl_seat_interface, 1);
		else if (iface == "wl_data_device_manager" && s._mgr == IntPtr.Zero)
			s._mgr = WaylandBindings.wl_registry_bind(r, n, WaylandInterfaces.wl_data_device_manager_interface, 1);
	}
	private static void RegRemove(IntPtr d, IntPtr r, uint n) { }
	private static void SeatCaps(IntPtr d, IntPtr s, uint c) { }
	private static void SeatName(IntPtr d, IntPtr s, string n) { }

	private static void DevOffer(IntPtr d, IntPtr dev, IntPtr offer)
	{
		var s = Instance;
		if (s._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(s._currentOffer); s._mimeTypes.Clear(); }
		s._currentOffer = offer;
		s._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, s._offerPtr, IntPtr.Zero);
	}
	private static void DevSel(IntPtr d, IntPtr dev, IntPtr offer)
	{
		var s = Instance;
		s._hasSelection = true;
		if (offer == IntPtr.Zero) { if (s._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(s._currentOffer); s._currentOffer = IntPtr.Zero; } s._mimeTypes.Clear(); }
	}
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void DevLeave(IntPtr d, IntPtr dd) { }
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void DevDrop(IntPtr d, IntPtr dd) { }

	private static void OffOffer(IntPtr d, IntPtr o, string mime) { Instance._mimeTypes.Add(mime); }
	private static void OffSA(IntPtr d, IntPtr o, uint sa) { }
	private static void OffAct(IntPtr d, IntPtr o, uint a) { }

	private static void SrcTarget(IntPtr d, IntPtr s, string m) { }
	private static unsafe void SrcSend(IntPtr d, IntPtr s, string m, int fd)
	{
		var t = Instance._copyText;
		if (t != null) { var b = Encoding.UTF8.GetBytes(t); fixed (byte* p = b) { int w = 0; while (w < b.Length) { var n = LibcWritePtr(fd, (IntPtr)(p + w), b.Length - w); if (n <= 0) break; w += n; } } }
		_ = WaylandBindings.close(fd);
	}
	private static void SrcCancel(IntPtr d, IntPtr s) { var self = Instance; if (s == self._currentSource) { WaylandBindings.wl_proxy_destroy(s); self._currentSource = IntPtr.Zero; } }
	private static void SrcDDP(IntPtr d, IntPtr s) { }
	private static void SrcDF(IntPtr d, IntPtr s) { }
	private static void SrcAct(IntPtr d, IntPtr s, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int LibcWritePtr(int fd, IntPtr buf, int count);
}
