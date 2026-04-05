using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Provides access to exported wl_*_interface global data symbols from libwayland-client.so.0.
/// We piggyback on LibraryImport's resolution by defining a dummy P/Invoke function
/// and using NativeLibrary.GetMainProgramHandle + dlsym to find the data symbols
/// after the library has been loaded by the runtime.
/// </summary>
internal static partial class WaylandInterfaces
{
	// This dummy P/Invoke ensures libwayland-client.so.0 is loaded by the .NET runtime
	// before we try to find data symbols in it.
	[LibraryImport("libwayland-client.so.0", EntryPoint = "wl_display_connect")]
	private static partial IntPtr _dummy_load(IntPtr name);

	// dlsym from libc - we use RTLD_DEFAULT (IntPtr.Zero) to search ALL loaded libraries
	[LibraryImport("libwayland-client.so.0", EntryPoint = "wl_proxy_get_version")]
	private static partial uint _dummy_version(IntPtr proxy);

	static WaylandInterfaces()
	{
		// The P/Invoke declarations above ensure the .NET runtime knows about libwayland-client.so.0.
		// NativeLibrary.TryLoad with the assembly context will resolve it using the same DllImport resolution.
	}

	private static IntPtr GetInterface(string name)
	{
		// After the library is loaded via P/Invoke, get a handle to it
		if (!NativeLibrary.TryLoad("libwayland-client.so.0", typeof(WaylandInterfaces).Assembly, null, out var handle) || handle == IntPtr.Zero)
		{
			throw new DllNotFoundException("libwayland-client.so.0 could not be loaded for symbol resolution");
		}

		if (!NativeLibrary.TryGetExport(handle, name, out var ptr) || ptr == IntPtr.Zero)
		{
			throw new EntryPointNotFoundException($"Wayland data symbol '{name}' not found in libwayland-client.so.0");
		}
		return ptr;
	}

	// Core protocol interfaces
	internal static readonly IntPtr wl_registry_interface = GetInterface("wl_registry_interface");
	internal static readonly IntPtr wl_compositor_interface = GetInterface("wl_compositor_interface");
	internal static readonly IntPtr wl_shm_interface = GetInterface("wl_shm_interface");
	internal static readonly IntPtr wl_shm_pool_interface = GetInterface("wl_shm_pool_interface");
	internal static readonly IntPtr wl_buffer_interface = GetInterface("wl_buffer_interface");
	internal static readonly IntPtr wl_surface_interface = GetInterface("wl_surface_interface");
	internal static readonly IntPtr wl_seat_interface = GetInterface("wl_seat_interface");
	internal static readonly IntPtr wl_pointer_interface = GetInterface("wl_pointer_interface");
	internal static readonly IntPtr wl_keyboard_interface = GetInterface("wl_keyboard_interface");
	internal static readonly IntPtr wl_touch_interface = GetInterface("wl_touch_interface");
	internal static readonly IntPtr wl_output_interface = GetInterface("wl_output_interface");
	internal static readonly IntPtr wl_callback_interface = GetInterface("wl_callback_interface");
	internal static readonly IntPtr wl_region_interface = GetInterface("wl_region_interface");
	internal static readonly IntPtr wl_data_device_interface = GetInterface("wl_data_device_interface");
	internal static readonly IntPtr wl_data_device_manager_interface = GetInterface("wl_data_device_manager_interface");
	internal static readonly IntPtr wl_data_offer_interface = GetInterface("wl_data_offer_interface");
	internal static readonly IntPtr wl_data_source_interface = GetInterface("wl_data_source_interface");
	internal static readonly IntPtr wl_subcompositor_interface = GetInterface("wl_subcompositor_interface");
	internal static readonly IntPtr wl_subsurface_interface = GetInterface("wl_subsurface_interface");
}
