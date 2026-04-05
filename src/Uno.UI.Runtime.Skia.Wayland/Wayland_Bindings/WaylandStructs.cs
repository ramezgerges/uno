using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// Delegates for wl_registry listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlRegistryGlobalDelegate(IntPtr data, IntPtr registry, uint name, string iface, uint version);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlRegistryGlobalRemoveDelegate(IntPtr data, IntPtr registry, uint name);

[StructLayout(LayoutKind.Sequential)]
internal struct WlRegistryListener
{
	public IntPtr global;
	public IntPtr global_remove;
}

// Delegates for wl_surface listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlSurfaceEnterDelegate(IntPtr data, IntPtr surface, IntPtr output);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlSurfaceLeaveDelegate(IntPtr data, IntPtr surface, IntPtr output);

[StructLayout(LayoutKind.Sequential)]
internal struct WlSurfaceListener
{
	public IntPtr enter;
	public IntPtr leave;
}

// Delegates for wl_callback (frame callback)
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlCallbackDoneDelegate(IntPtr data, IntPtr callback, uint callbackData);

[StructLayout(LayoutKind.Sequential)]
internal struct WlCallbackListener
{
	public IntPtr done;
}

// Delegates for wl_seat listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlSeatCapabilitiesDelegate(IntPtr data, IntPtr seat, uint capabilities);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlSeatNameDelegate(IntPtr data, IntPtr seat, string name);

[StructLayout(LayoutKind.Sequential)]
internal struct WlSeatListener
{
	public IntPtr capabilities;
	public IntPtr name;
}

// Delegates for wl_pointer listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerEnterDelegate(IntPtr data, IntPtr pointer, uint serial, IntPtr surface, int sx, int sy);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerLeaveDelegate(IntPtr data, IntPtr pointer, uint serial, IntPtr surface);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerMotionDelegate(IntPtr data, IntPtr pointer, uint time, int sx, int sy);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerButtonDelegate(IntPtr data, IntPtr pointer, uint serial, uint time, uint button, uint state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerAxisDelegate(IntPtr data, IntPtr pointer, uint time, uint axis, int value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerFrameDelegate(IntPtr data, IntPtr pointer);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerAxisSourceDelegate(IntPtr data, IntPtr pointer, uint axisSource);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerAxisStopDelegate(IntPtr data, IntPtr pointer, uint time, uint axis);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlPointerAxisDiscreteDelegate(IntPtr data, IntPtr pointer, uint axis, int discrete);

[StructLayout(LayoutKind.Sequential)]
internal struct WlPointerListener
{
	public IntPtr enter;
	public IntPtr leave;
	public IntPtr motion;
	public IntPtr button;
	public IntPtr axis;
	public IntPtr frame;
	public IntPtr axis_source;
	public IntPtr axis_stop;
	public IntPtr axis_discrete;
}

// Delegates for wl_keyboard listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardKeymapDelegate(IntPtr data, IntPtr keyboard, uint format, int fd, uint size);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardEnterDelegate(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface, IntPtr keys);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardLeaveDelegate(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardKeyDelegate(IntPtr data, IntPtr keyboard, uint serial, uint time, uint key, uint state);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardModifiersDelegate(IntPtr data, IntPtr keyboard, uint serial, uint modsDepressed, uint modsLatched, uint modsLocked, uint group);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlKeyboardRepeatInfoDelegate(IntPtr data, IntPtr keyboard, int rate, int delay);

[StructLayout(LayoutKind.Sequential)]
internal struct WlKeyboardListener
{
	public IntPtr keymap;
	public IntPtr enter;
	public IntPtr leave;
	public IntPtr key;
	public IntPtr modifiers;
	public IntPtr repeat_info;
}

// Delegates for wl_touch listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlTouchDownDelegate(IntPtr data, IntPtr touch, uint serial, uint time, IntPtr surface, int id, int x, int y);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlTouchUpDelegate(IntPtr data, IntPtr touch, uint serial, uint time, int id);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlTouchMotionDelegate(IntPtr data, IntPtr touch, uint time, int id, int x, int y);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlTouchFrameDelegate(IntPtr data, IntPtr touch);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlTouchCancelDelegate(IntPtr data, IntPtr touch);

[StructLayout(LayoutKind.Sequential)]
internal struct WlTouchListener
{
	public IntPtr down;
	public IntPtr up;
	public IntPtr motion;
	public IntPtr frame;
	public IntPtr cancel;
}

// Delegates for wl_output listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputGeometryDelegate(IntPtr data, IntPtr output, int x, int y, int physicalWidth, int physicalHeight, int subpixel, string make, string model, int transform);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputModeDelegate(IntPtr data, IntPtr output, uint flags, int width, int height, int refresh);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputDoneDelegate(IntPtr data, IntPtr output);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputScaleDelegate(IntPtr data, IntPtr output, int factor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputNameDelegate(IntPtr data, IntPtr output, string name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlOutputDescriptionDelegate(IntPtr data, IntPtr output, string description);

[StructLayout(LayoutKind.Sequential)]
internal struct WlOutputListener
{
	public IntPtr geometry;
	public IntPtr mode;
	public IntPtr done;
	public IntPtr scale;
	public IntPtr name;
	public IntPtr description;
}

// wl_data_device listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceDataOfferDelegate(IntPtr data, IntPtr dataDevice, IntPtr offer);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceEnterDelegate(IntPtr data, IntPtr dataDevice, uint serial, IntPtr surface, int x, int y, IntPtr offer);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceLeaveDelegate(IntPtr data, IntPtr dataDevice);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceMotionDelegate(IntPtr data, IntPtr dataDevice, uint time, int x, int y);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceDropDelegate(IntPtr data, IntPtr dataDevice);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataDeviceSelectionDelegate(IntPtr data, IntPtr dataDevice, IntPtr offer);

[StructLayout(LayoutKind.Sequential)]
internal struct WlDataDeviceListener
{
	public IntPtr data_offer;
	public IntPtr enter;
	public IntPtr leave;
	public IntPtr motion;
	public IntPtr drop;
	public IntPtr selection;
}

// wl_data_offer listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataOfferOfferDelegate(IntPtr data, IntPtr offer, string mimeType);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataOfferSourceActionsDelegate(IntPtr data, IntPtr offer, uint sourceActions);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataOfferActionDelegate(IntPtr data, IntPtr offer, uint dndAction);

[StructLayout(LayoutKind.Sequential)]
internal struct WlDataOfferListener
{
	public IntPtr offer;
	public IntPtr source_actions;
	public IntPtr action;
}

// wl_data_source listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataSourceTargetDelegate(IntPtr data, IntPtr source, string mimeType);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataSourceSendDelegate(IntPtr data, IntPtr source, string mimeType, int fd);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WlDataSourceCancelledDelegate(IntPtr data, IntPtr source);

[StructLayout(LayoutKind.Sequential)]
internal struct WlDataSourceListener
{
	public IntPtr target;
	public IntPtr send;
	public IntPtr cancelled;
}
