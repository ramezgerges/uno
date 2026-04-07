using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Pure C# clipboard via wl_data_device. Listener structs are allocated in
/// UNMANAGED memory (Marshal.AllocHGlobal) to match C's behavior — the GC
/// heap causes segfaults during wl_display_dispatch.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private IntPtr _mySeat;
	private IntPtr _myManager;
	private IntPtr _myDevice;
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

	// Listener structs in UNMANAGED memory (like C's .data segment)
	private static IntPtr s_regListenerPtr;
	private static IntPtr s_seatListenerPtr;
	private static IntPtr s_devListenerPtr;
	private static IntPtr s_offerListenerPtr;
	private static IntPtr s_srcListenerPtr;

	// Static delegates to prevent GC
	private static WlRegistryGlobalDelegate? s_regGlobal;
	private static WlRegistryGlobalRemoveDelegate? s_regRemove;
	private static WlSeatCapabilitiesDelegate? s_seatCaps;
	private static WlSeatNameDelegate? s_seatName;
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

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	/// <summary>
	/// Allocate a struct in unmanaged memory and write function pointers to it.
	/// Returns the unmanaged pointer (never freed — lives for the process lifetime).
	/// </summary>
	private static IntPtr AllocUnmanagedListener(IntPtr[] funcPtrs)
	{
		var size = funcPtrs.Length * IntPtr.Size;
		var ptr = Marshal.AllocHGlobal(size);
		for (int i = 0; i < funcPtrs.Length; i++)
		{
			Marshal.WriteIntPtr(ptr, i * IntPtr.Size, funcPtrs[i]);
		}
		return ptr;
	}

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		// Create all delegates
		s_regGlobal = RegGlobal;
		s_regRemove = RegRemove;
		s_seatCaps = SeatCaps;
		s_seatName = SeatName;
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

		// Allocate listener structs in UNMANAGED memory
		s_regListenerPtr = AllocUnmanagedListener([
			Marshal.GetFunctionPointerForDelegate(s_regGlobal),
			Marshal.GetFunctionPointerForDelegate(s_regRemove),
		]);
		s_seatListenerPtr = AllocUnmanagedListener([
			Marshal.GetFunctionPointerForDelegate(s_seatCaps),
			Marshal.GetFunctionPointerForDelegate(s_seatName),
		]);
		s_devListenerPtr = AllocUnmanagedListener([
			Marshal.GetFunctionPointerForDelegate(s_devOffer),
			Marshal.GetFunctionPointerForDelegate(s_devEnter),
			Marshal.GetFunctionPointerForDelegate(s_devLeave),
			Marshal.GetFunctionPointerForDelegate(s_devMotion),
			Marshal.GetFunctionPointerForDelegate(s_devDrop),
			Marshal.GetFunctionPointerForDelegate(s_devSel),
		]);
		s_offerListenerPtr = AllocUnmanagedListener([
			Marshal.GetFunctionPointerForDelegate(s_offerOffer),
			Marshal.GetFunctionPointerForDelegate(s_offerSA),
			Marshal.GetFunctionPointerForDelegate(s_offerAct),
		]);
		s_srcListenerPtr = AllocUnmanagedListener([
			Marshal.GetFunctionPointerForDelegate(s_srcTarget),
			Marshal.GetFunctionPointerForDelegate(s_srcSend),
			Marshal.GetFunctionPointerForDelegate(s_srcCancel),
			Marshal.GetFunctionPointerForDelegate(s_srcDDP),
			Marshal.GetFunctionPointerForDelegate(s_srcDF),
			Marshal.GetFunctionPointerForDelegate(s_srcAct),
		]);

		// Create our own registry and bind v1 seat + v1 manager
		var reg = WaylandBindings.wl_display_get_registry(wlDisplay);
		_ = WaylandBindings.wl_proxy_add_listener(reg, s_regListenerPtr, IntPtr.Zero);
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);

		if (_mySeat == IntPtr.Zero || _myManager == IntPtr.Zero) return;

		// Add listener to our seat (prevents segfault from unhandled seat events)
		_ = WaylandBindings.wl_proxy_add_listener(_mySeat, s_seatListenerPtr, IntPtr.Zero);

		// Create data device
		_myDevice = WaylandBindings.wl_proxy_marshal_flags(
			_myManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_myManager), 0,
			IntPtr.Zero, _mySeat);
		if (_myDevice == IntPtr.Zero) return;

		_ = WaylandBindings.wl_proxy_add_listener(_myDevice, s_devListenerPtr, IntPtr.Zero);

		// Roundtrip to get initial selection
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		_initialized = true;
	}

	internal void ProcessOnEventThread()
	{
		if (!_initialized) return;

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

	// --- Event thread operations ---

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
		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, s_srcListenerPtr, IntPtr.Zero);
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

	// --- Static callbacks ---
	private static void RegGlobal(IntPtr data, IntPtr reg, uint name, string iface, uint version)
	{
		var self = Instance;
		if (iface == "wl_seat" && self._mySeat == IntPtr.Zero)
			self._mySeat = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_seat_interface, 1);
		else if (iface == "wl_data_device_manager" && self._myManager == IntPtr.Zero)
			self._myManager = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
	}
	private static void RegRemove(IntPtr data, IntPtr reg, uint name) { }
	private static void SeatCaps(IntPtr data, IntPtr seat, uint caps) { }
	private static void SeatName(IntPtr data, IntPtr seat, string seatName) { }

	private static void DevOffer(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		if (self._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(self._currentOffer); self._offerMimeTypes.Clear(); }
		self._currentOffer = offer;
		self._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, s_offerListenerPtr, IntPtr.Zero);
	}
	private static void DevSelection(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var self = Instance;
		self._hasSelection = true;
		if (offer == IntPtr.Zero) { if (self._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(self._currentOffer); self._currentOffer = IntPtr.Zero; } self._offerMimeTypes.Clear(); }
	}
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private static void DevLeave(IntPtr d, IntPtr dd) { }
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }
	private static void DevDrop(IntPtr d, IntPtr dd) { }

	private static void OfferOffer(IntPtr data, IntPtr offer, string mime) => Instance._offerMimeTypes.Add(mime);
	private static void OfferSA(IntPtr data, IntPtr offer, uint sa) { }
	private static void OfferAct(IntPtr data, IntPtr offer, uint a) { }

	private static void SrcTarget(IntPtr data, IntPtr src, string mime) { }
	private static unsafe void SrcSend(IntPtr data, IntPtr src, string mime, int fd)
	{
		var text = Instance._copyText;
		if (text != null) { var bytes = Encoding.UTF8.GetBytes(text); fixed (byte* p = bytes) { int w = 0; while (w < bytes.Length) { var n = LibcWritePtr(fd, (IntPtr)(p + w), bytes.Length - w); if (n <= 0) break; w += n; } } }
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
