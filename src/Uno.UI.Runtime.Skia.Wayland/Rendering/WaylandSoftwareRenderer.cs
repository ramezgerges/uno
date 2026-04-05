using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandSoftwareRenderer : WaylandRenderer
{
	private readonly IntPtr _wlDisplay;
	private readonly IntPtr _wlSurface;
	private readonly IntPtr _wlShm;
	private IntPtr _buffer;
	private IntPtr _shmData;
	private int _width;
	private int _height;
	private int _stride;
	private long _bufferSize;

	public WaylandSoftwareRenderer(IXamlRootHost host, IntPtr wlDisplay, IntPtr wlSurface, IntPtr wlShm)
		: base(host)
	{
		_wlDisplay = wlDisplay;
		_wlSurface = wlSurface;
		_wlShm = wlShm;
	}

	protected override SKSurface UpdateSize(int width, int height)
	{
		_width = width;
		_height = height;
		_stride = width * 4; // ARGB8888 = 4 bytes per pixel
		_bufferSize = _stride * height;

		// Clean up previous buffer
		if (_shmData != IntPtr.Zero)
		{
			_ = WaylandBindings.munmap(_shmData, (nuint)_bufferSize);
			_shmData = IntPtr.Zero;
		}

		if (_buffer != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(_buffer);
			_buffer = IntPtr.Zero;
		}

		// Create shared memory
		var name = $"/uno-wayland-shm-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}-{Environment.TickCount64}";
		var fd = WaylandBindings.shm_open(name, WaylandBindings.O_RDWR | WaylandBindings.O_CREAT | WaylandBindings.O_EXCL, 0x180 /* 0600 */);
		if (fd < 0)
		{
			throw new InvalidOperationException($"Failed to create shared memory: {Marshal.GetLastPInvokeErrorMessage()}");
		}

		_ = WaylandBindings.shm_unlink(name);
		_ = WaylandBindings.ftruncate(fd, _bufferSize);

		_shmData = WaylandBindings.mmap(
			IntPtr.Zero,
			(nuint)_bufferSize,
			WaylandBindings.PROT_READ | WaylandBindings.PROT_WRITE,
			WaylandBindings.MAP_SHARED,
			fd,
			0);

		// Create wl_shm_pool and wl_buffer
		// wl_shm.create_pool opcode = 0, args: new_id (pool), fd, size
		var pool = WaylandBindings.wl_proxy_marshal_flags(
			_wlShm, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlShm), 0, IntPtr.Zero, fd, (int)_bufferSize);

		// wl_shm_pool.create_buffer opcode = 0, args: new_id (buffer), offset, width, height, stride, format
		_buffer = WaylandBindings.wl_proxy_marshal_flags(
			pool, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(pool), 0,
			IntPtr.Zero, 0, width, height, _stride, (int)WlShmFormat.ARGB8888);

		// wl_shm_pool.destroy opcode = 1
		WaylandBindings.wl_proxy_marshal_flags(pool, 1, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(pool), 1);

		_ = WaylandBindings.close(fd);

		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		return SKSurface.Create(info, _shmData, _stride);
	}

	protected override void Flush()
	{
		if (_buffer == IntPtr.Zero || _wlSurface == IntPtr.Zero)
		{
			return;
		}

		// wl_surface.attach opcode = 1, args: buffer, x, y
		WaylandBindings.wl_proxy_marshal_flags(
			_wlSurface, 1, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlSurface), 0, _buffer, 0, 0);

		// wl_surface.damage_buffer opcode = 9, args: x, y, width, height
		WaylandBindings.wl_proxy_marshal_flags(
			_wlSurface, 9, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlSurface), 0, 0, 0, _width, _height);

		// wl_surface.commit opcode = 6
		WaylandBindings.wl_proxy_marshal_flags(
			_wlSurface, 6, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlSurface), 0);

		_ = WaylandBindings.wl_display_flush(_wlDisplay);
	}

	public override void Dispose()
	{
		if (_shmData != IntPtr.Zero)
		{
			_ = WaylandBindings.munmap(_shmData, (nuint)_bufferSize);
			_shmData = IntPtr.Zero;
		}

		if (_buffer != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(_buffer);
			_buffer = IntPtr.Zero;
		}
	}
}
