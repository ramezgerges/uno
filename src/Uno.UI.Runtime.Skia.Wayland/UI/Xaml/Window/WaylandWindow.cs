using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

#pragma warning disable CS0649 // Field is never assigned to
internal struct WaylandWindow
{
	public IntPtr Display;
	public IntPtr Surface;
	public IntPtr XdgSurface;
	public IntPtr XdgToplevel;
	public int Width;
	public int Height;
	public float Scale;
	public bool Configured;
}
#pragma warning restore CS0649
