using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void WpFractionalScalePreferredScaleDelegate(IntPtr data, IntPtr fractionalScale, uint scale);

[StructLayout(LayoutKind.Sequential)]
internal struct WpFractionalScaleListener
{
	public IntPtr preferred_scale;
}

internal static class WpFractionalScale
{
	internal const uint WP_FRACTIONAL_SCALE_MANAGER_V1_DESTROY = 0;
	internal const uint WP_FRACTIONAL_SCALE_MANAGER_V1_GET_FRACTIONAL_SCALE = 1;
	internal const uint WP_FRACTIONAL_SCALE_V1_DESTROY = 0;
}
