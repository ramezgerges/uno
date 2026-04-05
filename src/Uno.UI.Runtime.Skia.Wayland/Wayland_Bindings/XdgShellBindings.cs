using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

// xdg_wm_base listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgWmBasePingDelegate(IntPtr data, IntPtr xdgWmBase, uint serial);

[StructLayout(LayoutKind.Sequential)]
internal struct XdgWmBaseListener
{
	public IntPtr ping;
}

// xdg_surface listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgSurfaceConfigureDelegate(IntPtr data, IntPtr xdgSurface, uint serial);

[StructLayout(LayoutKind.Sequential)]
internal struct XdgSurfaceListener
{
	public IntPtr configure;
}

// xdg_toplevel listener
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgToplevelConfigureDelegate(IntPtr data, IntPtr toplevel, int width, int height, IntPtr states);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgToplevelCloseDelegate(IntPtr data, IntPtr toplevel);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgToplevelConfigureBoundsDelegate(IntPtr data, IntPtr toplevel, int width, int height);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgToplevelWmCapabilitiesDelegate(IntPtr data, IntPtr toplevel, IntPtr capabilities);

[StructLayout(LayoutKind.Sequential)]
internal struct XdgToplevelListener
{
	public IntPtr configure;
	public IntPtr close;
	public IntPtr configure_bounds;
	public IntPtr wm_capabilities;
}

internal enum XdgToplevelState : uint
{
	Maximized = 1,
	Fullscreen = 2,
	Resizing = 3,
	Activated = 4,
	TiledLeft = 5,
	TiledRight = 6,
	TiledTop = 7,
	TiledBottom = 8,
	Suspended = 9,
}

/// <summary>
/// Helper for xdg-shell protocol requests. Since Wayland uses a message-based protocol,
/// requests are sent via wl_proxy_marshal_flags. This class provides typed wrappers.
/// </summary>
internal static partial class XdgShell
{
	// xdg_wm_base opcodes
	internal const uint XDG_WM_BASE_DESTROY = 0;
	internal const uint XDG_WM_BASE_CREATE_POSITIONER = 1;
	internal const uint XDG_WM_BASE_GET_XDG_SURFACE = 2;
	internal const uint XDG_WM_BASE_PONG = 3;

	// xdg_surface opcodes
	internal const uint XDG_SURFACE_DESTROY = 0;
	internal const uint XDG_SURFACE_GET_TOPLEVEL = 1;
	internal const uint XDG_SURFACE_GET_POPUP = 2;
	internal const uint XDG_SURFACE_SET_WINDOW_GEOMETRY = 3;
	internal const uint XDG_SURFACE_ACK_CONFIGURE = 4;

	// xdg_toplevel opcodes
	internal const uint XDG_TOPLEVEL_DESTROY = 0;
	internal const uint XDG_TOPLEVEL_SET_PARENT = 1;
	internal const uint XDG_TOPLEVEL_SET_TITLE = 2;
	internal const uint XDG_TOPLEVEL_SET_APP_ID = 3;
	internal const uint XDG_TOPLEVEL_SHOW_WINDOW_MENU = 4;
	internal const uint XDG_TOPLEVEL_MOVE = 5;
	internal const uint XDG_TOPLEVEL_RESIZE = 6;
	internal const uint XDG_TOPLEVEL_SET_MAX_SIZE = 7;
	internal const uint XDG_TOPLEVEL_SET_MIN_SIZE = 8;
	internal const uint XDG_TOPLEVEL_SET_MAXIMIZED = 9;
	internal const uint XDG_TOPLEVEL_UNSET_MAXIMIZED = 10;
	internal const uint XDG_TOPLEVEL_SET_FULLSCREEN = 11;
	internal const uint XDG_TOPLEVEL_UNSET_FULLSCREEN = 12;
	internal const uint XDG_TOPLEVEL_SET_MINIMIZED = 13;
}
