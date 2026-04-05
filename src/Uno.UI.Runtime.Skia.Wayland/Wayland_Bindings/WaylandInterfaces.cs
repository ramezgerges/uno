using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Provides access to exported wl_*_interface global data symbols.
/// Core protocol interfaces come from libwayland-client.so.0.
/// Extension protocol interfaces (xdg-shell) come from libxdg-shell-protocol.so
/// which is compiled from wayland-scanner output and shipped as a native asset.
/// </summary>
internal static class WaylandInterfaces
{
	private static IntPtr _waylandClientHandle;
	private static IntPtr _xdgShellHandle;

	static WaylandInterfaces()
	{
		// Empty — lazy init in EnsureLoaded()
	}

	private static bool _initialized;

	internal static void EnsureLoaded()
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;

		// Get a handle — try multiple approaches
		NativeLibrary.TryLoad("libwayland-client.so.0", out _waylandClientHandle);
		if (_waylandClientHandle == IntPtr.Zero)
		{
			NativeLibrary.TryLoad("/usr/lib/x86_64-linux-gnu/libwayland-client.so.0.22.0", out _waylandClientHandle);
		}
		if (_waylandClientHandle == IntPtr.Zero)
		{
			NativeLibrary.TryLoad("/lib/x86_64-linux-gnu/libwayland-client.so.0.22.0", out _waylandClientHandle);
		}
		if (_waylandClientHandle == IntPtr.Zero)
		{
			_waylandClientHandle = FindLoadedLibrary("libwayland-client.so");
		}

		// Load xdg-shell protocol library from the app's native assets
		var appDir = AppContext.BaseDirectory;
		foreach (var candidate in new[]
		{
			System.IO.Path.Combine(appDir, "runtimes", "linux-x64", "native", "libxdg-shell-protocol.so"),
			System.IO.Path.Combine(appDir, "libxdg-shell-protocol.so"),
		})
		{
			if (System.IO.File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _xdgShellHandle) && _xdgShellHandle != IntPtr.Zero)
			{
				break;
			}
		}
	}

	private static IntPtr FindLoadedLibrary(string name)
	{
		// Parse /proc/self/maps to find already-loaded libraries
		try
		{
			foreach (var line in System.IO.File.ReadLines("/proc/self/maps"))
			{
				if (line.Contains(name) && line.Contains(".so"))
				{
					// Extract the path from the maps line
					var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 6)
					{
						var path = parts[^1];
						if (System.IO.File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
						{
							return handle;
						}
					}
				}
			}
		}
		catch
		{
			// /proc may not be available
		}
		return IntPtr.Zero;
	}

	private static IntPtr GetExport(IntPtr handle, string name, string libName)
	{
		if (handle == IntPtr.Zero)
		{
			throw new DllNotFoundException($"{libName} not loaded — cannot resolve '{name}'");
		}
		if (!NativeLibrary.TryGetExport(handle, name, out var ptr) || ptr == IntPtr.Zero)
		{
			throw new EntryPointNotFoundException($"Symbol '{name}' not found in {libName} (handle={handle})");
		}
		return ptr;
	}

	private static IntPtr GetCoreInterface(string name)
	{
		EnsureLoaded();
		return GetExport(_waylandClientHandle, name, "libwayland-client.so.0");
	}

	private static IntPtr GetXdgInterface(string name)
	{
		EnsureLoaded();
		return GetExport(_xdgShellHandle, name, "libxdg-shell-protocol.so");
	}

	// Core protocol interfaces (from libwayland-client.so.0)
	// These are resolved lazily on first access via GetCoreInterface
	internal static IntPtr wl_registry_interface => GetCoreInterface("wl_registry_interface");
	internal static IntPtr wl_compositor_interface => GetCoreInterface("wl_compositor_interface");
	internal static IntPtr wl_shm_interface => GetCoreInterface("wl_shm_interface");
	internal static IntPtr wl_shm_pool_interface => GetCoreInterface("wl_shm_pool_interface");
	internal static IntPtr wl_buffer_interface => GetCoreInterface("wl_buffer_interface");
	internal static IntPtr wl_surface_interface => GetCoreInterface("wl_surface_interface");
	internal static IntPtr wl_seat_interface => GetCoreInterface("wl_seat_interface");
	internal static IntPtr wl_pointer_interface => GetCoreInterface("wl_pointer_interface");
	internal static IntPtr wl_keyboard_interface => GetCoreInterface("wl_keyboard_interface");
	internal static IntPtr wl_touch_interface => GetCoreInterface("wl_touch_interface");
	internal static IntPtr wl_output_interface => GetCoreInterface("wl_output_interface");
	internal static IntPtr wl_callback_interface => GetCoreInterface("wl_callback_interface");
	internal static IntPtr wl_region_interface => GetCoreInterface("wl_region_interface");
	internal static IntPtr wl_data_device_interface => GetCoreInterface("wl_data_device_interface");
	internal static IntPtr wl_data_device_manager_interface => GetCoreInterface("wl_data_device_manager_interface");
	internal static IntPtr wl_data_offer_interface => GetCoreInterface("wl_data_offer_interface");
	internal static IntPtr wl_data_source_interface => GetCoreInterface("wl_data_source_interface");
	internal static IntPtr wl_subcompositor_interface => GetCoreInterface("wl_subcompositor_interface");
	internal static IntPtr wl_subsurface_interface => GetCoreInterface("wl_subsurface_interface");

	// XDG shell interfaces (from libxdg-shell-protocol.so — generated by wayland-scanner)
	internal static IntPtr xdg_wm_base_interface => GetXdgInterface("xdg_wm_base_interface");
	internal static IntPtr xdg_positioner_interface => GetXdgInterface("xdg_positioner_interface");
	internal static IntPtr xdg_surface_interface => GetXdgInterface("xdg_surface_interface");
	internal static IntPtr xdg_toplevel_interface => GetXdgInterface("xdg_toplevel_interface");
	internal static IntPtr xdg_popup_interface => GetXdgInterface("xdg_popup_interface");
}
