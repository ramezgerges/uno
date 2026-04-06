using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static partial class WaylandBindings
{
	private const string LibWaylandClient = "libwayland-client.so.0";

	// wl_display
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_connect", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr wl_display_connect(string? name);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_disconnect")]
	internal static partial void wl_display_disconnect(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_dispatch")]
	internal static partial int wl_display_dispatch(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_dispatch_pending")]
	internal static partial int wl_display_dispatch_pending(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_roundtrip")]
	internal static partial int wl_display_roundtrip(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_flush")]
	internal static partial int wl_display_flush(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_get_fd")]
	internal static partial int wl_display_get_fd(IntPtr display);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_display_get_error")]
	internal static partial int wl_display_get_error(IntPtr display);

	// wl_proxy (generic object management)
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags);

	// Variadic overloads via manually constructed calls
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr arg1);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr arg1, IntPtr arg2);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, uint arg1);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, int arg1, int arg2, int arg3, int arg4);

	// For wl_pointer.set_cursor(serial, surface, hotspot_x, hotspot_y)
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, uint arg1, IntPtr arg2, int arg3, int arg4);

	// For wl_data_offer.receive(mime_type, fd) and wl_data_device.set_selection(source, serial)
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr arg1, int arg2);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr arg1, int arg2, int arg3);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_flags")]
	internal static partial IntPtr wl_proxy_marshal_flags(IntPtr proxy, uint opcode, IntPtr iface, uint version, uint flags, IntPtr arg1, int arg2, int arg3, int arg4, int arg5, int arg6);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_add_listener")]
	internal static partial int wl_proxy_add_listener(IntPtr proxy, IntPtr listener, IntPtr data);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_destroy")]
	internal static partial void wl_proxy_destroy(IntPtr proxy);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_get_version")]
	internal static partial uint wl_proxy_get_version(IntPtr proxy);

	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_get_id")]
	internal static partial uint wl_proxy_get_id(IntPtr proxy);

	// wl_display_get_registry: inline function in C header.
	// Implemented as: wl_proxy_marshal_flags(display, WL_DISPLAY_GET_REGISTRY=1, &wl_registry_interface, version, 0)
	internal static IntPtr wl_display_get_registry(IntPtr display)
	{
		var iface = WaylandInterfaces.wl_registry_interface;
		return wl_proxy_marshal_flags(display, 1, iface, wl_proxy_get_version(display), 0, IntPtr.Zero);
	}

	// wl_registry_bind: inline function in C header.
	// Implemented as: wl_proxy_marshal_flags(registry, WL_REGISTRY_BIND=0, interface, version, 0, name, interface->name, version)
	// But the actual C implementation is special — it uses wl_proxy_marshal_constructor_versioned
	[LibraryImport(LibWaylandClient, EntryPoint = "wl_proxy_marshal_constructor_versioned")]
	private static partial IntPtr wl_proxy_marshal_constructor_versioned(
		IntPtr proxy, uint opcode, IntPtr iface, uint version, uint name, IntPtr ifaceName, uint ifaceVersion);

	internal static unsafe IntPtr wl_registry_bind(IntPtr registry, uint name, IntPtr iface, uint version)
	{
		IntPtr ifaceName;
		if (iface == IntPtr.Zero)
		{
			// For extension protocols, we must still provide the interface name as a string.
			// This should not happen — callers should provide valid interface pointers.
			throw new ArgumentException("wl_registry_bind requires a non-null wl_interface pointer");
		}

		// The wl_interface struct starts with: const char* name;
		// We need to read interface->name (dereference the first pointer in the struct)
		ifaceName = *(IntPtr*)iface.ToPointer();
		return wl_proxy_marshal_constructor_versioned(registry, 0, iface, version, name, ifaceName, version);
	}

	/// <summary>
	/// Bind a registry global using a manually-specified interface name (for extension protocols
	/// whose wl_interface is not in libwayland-client).
	/// </summary>
	internal static IntPtr wl_registry_bind_with_name(IntPtr registry, uint name, string interfaceName, uint version)
	{
		var namePtr = Marshal.StringToHGlobalAnsi(interfaceName);
		try
		{
			return wl_proxy_marshal_constructor_versioned(registry, 0, IntPtr.Zero, version, name, namePtr, version);
		}
		finally
		{
			Marshal.FreeHGlobal(namePtr);
		}
	}

	// Shared memory
	[LibraryImport("libc", EntryPoint = "mmap")]
	internal static unsafe partial IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

	[LibraryImport("libc", EntryPoint = "munmap")]
	internal static partial int munmap(IntPtr addr, nuint length);

	[LibraryImport("libc", EntryPoint = "shm_open", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int shm_open(string name, int oflag, uint mode);

	[LibraryImport("libc", EntryPoint = "shm_unlink", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int shm_unlink(string name);

	[LibraryImport("libc", EntryPoint = "ftruncate")]
	internal static partial int ftruncate(int fd, long length);

	[LibraryImport("libc", EntryPoint = "close")]
	internal static partial int close(int fd);

	[LibraryImport("libc", EntryPoint = "poll")]
	internal static unsafe partial int poll(PollFd* fds, nuint nfds, int timeout);

	// Constants
	internal const int PROT_READ = 0x1;
	internal const int PROT_WRITE = 0x2;
	internal const int MAP_SHARED = 0x01;
	internal const int O_RDWR = 0x02;
	internal const int O_CREAT = 0x40;
	internal const int O_EXCL = 0x80;
	internal const short POLLIN = 0x001;
	internal const short POLLOUT = 0x004;
	internal const short POLLERR = 0x008;
	internal const short POLLHUP = 0x010;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
	public int fd;
	public short events;
	public short revents;
}
