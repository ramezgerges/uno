using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void XdgToplevelDecorationConfigureDelegate(IntPtr data, IntPtr decoration, uint mode);

[StructLayout(LayoutKind.Sequential)]
internal struct XdgToplevelDecorationListener
{
	public IntPtr configure;
}

internal enum XdgDecorationMode : uint
{
	ClientSide = 1,
	ServerSide = 2,
}

internal static class XdgDecoration
{
	internal const uint ZXDG_DECORATION_MANAGER_V1_DESTROY = 0;
	internal const uint ZXDG_DECORATION_MANAGER_V1_GET_TOPLEVEL_DECORATION = 1;
	internal const uint ZXDG_TOPLEVEL_DECORATION_V1_DESTROY = 0;
	internal const uint ZXDG_TOPLEVEL_DECORATION_V1_SET_MODE = 1;
	internal const uint ZXDG_TOPLEVEL_DECORATION_V1_UNSET_MODE = 2;
}
