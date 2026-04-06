using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// P/Invoke bindings for libwayland-cursor.so.0
/// Used to load cursor themes and get cursor images for wl_pointer.set_cursor fallback.
/// </summary>
internal static partial class WaylandCursorBindings
{
	private const string LibWaylandCursor = "libwayland-cursor.so.0";

	[LibraryImport(LibWaylandCursor, EntryPoint = "wl_cursor_theme_load", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr wl_cursor_theme_load(string? name, int size, IntPtr wlShm);

	[LibraryImport(LibWaylandCursor, EntryPoint = "wl_cursor_theme_destroy")]
	internal static partial void wl_cursor_theme_destroy(IntPtr theme);

	[LibraryImport(LibWaylandCursor, EntryPoint = "wl_cursor_theme_get_cursor", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr wl_cursor_theme_get_cursor(IntPtr theme, string name);

	[LibraryImport(LibWaylandCursor, EntryPoint = "wl_cursor_image_get_buffer")]
	internal static partial IntPtr wl_cursor_image_get_buffer(IntPtr image);
}

/// <summary>
/// struct wl_cursor { unsigned int image_count; struct wl_cursor_image **images; char *name; }
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WlCursor
{
	public uint image_count;
	public IntPtr images; // struct wl_cursor_image**
	public IntPtr name;   // char*
}

/// <summary>
/// struct wl_cursor_image { uint width, height, hotspot_x, hotspot_y, delay; }
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WlCursorImage
{
	public uint width;
	public uint height;
	public uint hotspot_x;
	public uint hotspot_y;
	public uint delay;
}
