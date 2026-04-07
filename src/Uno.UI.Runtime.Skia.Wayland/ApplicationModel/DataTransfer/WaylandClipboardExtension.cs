using System;
using System.Runtime.InteropServices;
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

	private volatile string? _copiedText;
	private volatile uint _lastSerial;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	// Incremental test: create registry, bind seat+manager, create device, add listener
	// Each step logged. Comment out steps to find which one segfaults.
	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) return;
		_wlDisplay = wlDisplay;

		// Step 1: Create registry with C# listener in unmanaged memory
		var regFuncs = new IntPtr[2];
		regFuncs[0] = Marshal.GetFunctionPointerForDelegate(_regGlobal ??= RegGlobal);
		regFuncs[1] = Marshal.GetFunctionPointerForDelegate(_regRemove ??= RegRemove);
		_regPtr = Marshal.AllocHGlobal(2 * IntPtr.Size);
		Marshal.WriteIntPtr(_regPtr, 0, regFuncs[0]);
		Marshal.WriteIntPtr(_regPtr, IntPtr.Size, regFuncs[1]);

		var reg = WaylandBindings.wl_display_get_registry(wlDisplay);
		_ = WaylandBindings.wl_proxy_add_listener(reg, _regPtr, IntPtr.Zero);
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		Console.Error.WriteLine($"[CB] Step1: seat={_seat} mgr={_mgr}");
		if (_seat == IntPtr.Zero || _mgr == IntPtr.Zero) return;

		// Step 2: Add seat listener
		var seatFuncs = new IntPtr[2];
		seatFuncs[0] = Marshal.GetFunctionPointerForDelegate(_seatCaps ??= SeatCaps);
		seatFuncs[1] = Marshal.GetFunctionPointerForDelegate(_seatName ??= SeatName);
		_seatPtr = Marshal.AllocHGlobal(2 * IntPtr.Size);
		Marshal.WriteIntPtr(_seatPtr, 0, seatFuncs[0]);
		Marshal.WriteIntPtr(_seatPtr, IntPtr.Size, seatFuncs[1]);
		_ = WaylandBindings.wl_proxy_add_listener(_seat, _seatPtr, IntPtr.Zero);
		Console.Error.WriteLine("[CB] Step2: seat listener added");

		// Step 3: Create data device
		_dev = WaylandBindings.wl_proxy_marshal_flags(
			_mgr, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_mgr), 0,
			IntPtr.Zero, _seat);
		Console.Error.WriteLine($"[CB] Step3: dev={_dev}");
		if (_dev == IntPtr.Zero) return;

		// Step 4: Add device listener — THIS IS THE SUSPECT
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
		Console.Error.WriteLine("[CB] Step4: device listener added");

		// Step 5: Roundtrip to get initial selection
		Console.Error.WriteLine("[CB] Step5: roundtrip...");
		_ = WaylandBindings.wl_display_roundtrip(wlDisplay);
		Console.Error.WriteLine("[CB] Step5: done");

		// Step 6: Pre-create offer listener (not attached to anything yet)
		var offerFuncs = new IntPtr[3];
		offerFuncs[0] = Marshal.GetFunctionPointerForDelegate(_offOffer ??= OffOffer);
		offerFuncs[1] = Marshal.GetFunctionPointerForDelegate(_offSA ??= OffSA);
		offerFuncs[2] = Marshal.GetFunctionPointerForDelegate(_offAct ??= OffAct);
		_offerPtr = Marshal.AllocHGlobal(3 * IntPtr.Size);
		for (int i = 0; i < 3; i++) Marshal.WriteIntPtr(_offerPtr, i * IntPtr.Size, offerFuncs[i]);
		Console.Error.WriteLine("[CB] Step6: offer listener created (not attached)");

		_initialized = true;
	}

	internal void ProcessOnEventThread()
	{
		// In-process only for now
	}

	public void Clear() { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }
	public DataPackageView? GetContent()
	{
		var text = _copiedText;
		if (text != null) { var p = new DataPackage(); p.SetText(text); return p.GetView(); }
		return null;
	}
	public void SetContent(DataPackage? content)
	{
		if (content == null) { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }
		try { var v = content.GetView(); if (v.Contains(StandardDataFormats.Text)) _copiedText = v.GetTextAsync().AsTask().GetAwaiter().GetResult(); } catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// State
	private IntPtr _seat, _mgr, _dev;
	private IntPtr _regPtr, _seatPtr, _devPtr, _offerPtr;

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
	private static void SeatCaps(IntPtr data, IntPtr seat, uint caps) { Console.Error.WriteLine($"[CB] SeatCaps: {caps}"); }
	private static void SeatName(IntPtr data, IntPtr seat, string n) { Console.Error.WriteLine($"[CB] SeatName: {n}"); }
	private static void DevOffer(IntPtr data, IntPtr dev, IntPtr offer) { Console.Error.WriteLine($"[CB] DevOffer: {offer}"); }
	private static void DevEnter(IntPtr d, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { Console.Error.WriteLine("[CB] DevEnter"); }
	private static void DevLeave(IntPtr d, IntPtr dd) { Console.Error.WriteLine("[CB] DevLeave"); }
	private static void DevMotion(IntPtr d, IntPtr dd, uint time, int x, int y) { Console.Error.WriteLine("[CB] DevMotion"); }
	private static void DevDrop(IntPtr d, IntPtr dd) { Console.Error.WriteLine("[CB] DevDrop"); }
	private static void DevSel(IntPtr data, IntPtr dev, IntPtr offer) { Console.Error.WriteLine($"[CB] DevSel: {offer}"); }
	private static void OffOffer(IntPtr data, IntPtr offer, string mime) { Console.Error.WriteLine($"[CB] OffOffer: {mime}"); }
	private static void OffSA(IntPtr data, IntPtr offer, uint sa) { }
	private static void OffAct(IntPtr data, IntPtr offer, uint a) { }
}
