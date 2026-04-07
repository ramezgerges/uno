using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Pure C# clipboard via wl_data_device. Uses [UnmanagedCallersOnly] for
/// direct function pointers (no delegate trampolines), matching C exactly.
/// </summary>
internal unsafe class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private bool _initialized;
	private IntPtr _seat, _mgr, _dev;
	private IntPtr _currentOffer, _currentSource;
	private bool _hasSelection;
	private static readonly List<string> s_mimeTypes = new();
	private static string? s_copyText;

	// Thread-safe communication
	private volatile string? _cachedIncoming;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopy;
	private volatile uint _lastSerial;

	// Unmanaged listener memory
	private IntPtr _regPtr, _seatPtr, _devPtr, _offerPtr, _srcPtr;

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

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		_regPtr = MakeListener(
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, uint, void>)&RegGlobal,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&RegRemove);

		_seatPtr = MakeListener(
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&SeatCaps,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&SeatName);

		_devPtr = MakeListener(
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&DevOffer,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, int, int, IntPtr, void>)&DevEnter,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DevLeave,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int, int, void>)&DevMotion,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&DevDrop,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&DevSel);

		_offerPtr = MakeListener(
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OffOffer,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OffSA,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&OffAct);

		_srcPtr = MakeListener(
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&SrcTarget,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int, void>)&SrcSend,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&SrcCancel,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&SrcDDP,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&SrcDF,
			(IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void>)&SrcAct);

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
		if (_pasteRequested) { _pasteRequested = false; _cachedIncoming = ReadOffer(); }
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
		string? mime = s_mimeTypes.Contains("text/plain;charset=utf-8") ? "text/plain;charset=utf-8"
			: s_mimeTypes.Contains("text/plain") ? "text/plain" : null;
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
		s_copyText = text;
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

	// --- [UnmanagedCallersOnly] callbacks — direct function pointers, no delegates ---

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void RegGlobal(IntPtr data, IntPtr reg, uint name, IntPtr iface, uint version)
	{
		var s = Instance;
		var ifaceStr = Marshal.PtrToStringUTF8(iface);
		if (ifaceStr == "wl_seat" && s._seat == IntPtr.Zero)
			s._seat = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_seat_interface, 1);
		else if (ifaceStr == "wl_data_device_manager" && s._mgr == IntPtr.Zero)
			s._mgr = WaylandBindings.wl_registry_bind(reg, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void RegRemove(IntPtr data, IntPtr reg, uint name) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SeatCaps(IntPtr data, IntPtr seat, uint caps) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SeatName(IntPtr data, IntPtr seat, IntPtr name) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevOffer(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var s = Instance;
		if (s._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(s._currentOffer); s_mimeTypes.Clear(); }
		s._currentOffer = offer;
		s._hasSelection = false;
		_ = WaylandBindings.wl_proxy_add_listener(offer, s._offerPtr, IntPtr.Zero);
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevLeave(IntPtr d, IntPtr dd) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevDrop(IntPtr d, IntPtr dd) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void DevSel(IntPtr data, IntPtr dev, IntPtr offer)
	{
		var s = Instance;
		s._hasSelection = true;
		if (offer == IntPtr.Zero)
		{
			if (s._currentOffer != IntPtr.Zero) { WaylandBindings.wl_proxy_destroy(s._currentOffer); s._currentOffer = IntPtr.Zero; }
			s_mimeTypes.Clear();
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OffOffer(IntPtr data, IntPtr offer, IntPtr mime)
	{
		var str = Marshal.PtrToStringUTF8(mime);
		if (str != null) s_mimeTypes.Add(str);
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OffSA(IntPtr data, IntPtr offer, uint sa) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OffAct(IntPtr data, IntPtr offer, uint a) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcTarget(IntPtr data, IntPtr src, IntPtr mime) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcSend(IntPtr data, IntPtr src, IntPtr mime, int fd)
	{
		var t = s_copyText;
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

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcCancel(IntPtr data, IntPtr src)
	{
		var s = Instance;
		if (src == s._currentSource) { WaylandBindings.wl_proxy_destroy(src); s._currentSource = IntPtr.Zero; }
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcDDP(IntPtr data, IntPtr src) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcDF(IntPtr data, IntPtr src) { }

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void SrcAct(IntPtr data, IntPtr src, uint a) { }

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int LibcWritePtr(int fd, IntPtr buf, int count);
}
