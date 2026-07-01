#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Common;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;
using Uno.UI.Hosting;
using WebGpuExperiment;
using WColor = Windows.UI.Color;

namespace Uno.WinUI.Runtime.Skia.X11;

// EXPERIMENTAL: renders the visual tree with WebGPU (pure WebGPU drawing; Skia only reads
// font/path geometry). Renders offscreen, then blits the result to the X11 window via XPutImage
// (the software renderer's proven, xvfb-visible display path) — lavapipe's Vulkan WSI present
// doesn't reliably composite to the X window, so we avoid the swapchain entirely.
internal sealed unsafe class X11WebGpuRenderer : X11Renderer
{
	private readonly WebGpuContext _ctx;
	private readonly WebGpuTargets _targets = new(); // persistent render textures, reused across frames
	private readonly SkiaRenderHelper.FpsHelper _fpsHelper = new(); // same on-screen counter as the Skia path
	private readonly WebGpuFpsOverlay _fpsOverlay = new(); // composites that counter into the draw list
	private WebGpuDrawList? _dlA, _dlB; // double-buffered, reused across frames (see the factory for why two)
	private bool _dlToggle;
	private readonly IntPtr _gc;
	private readonly uint _depth;
	private int _w, _h;
	private IntPtr _buffer;   // unmanaged BGRA scanlines backing the XImage
	private IntPtr _xImage;

	public X11WebGpuRenderer(IXamlRootHost host, X11Window x11Window) : base(host, x11Window)
	{
		_ctx = new WebGpuContext("webgpu-x11"); // offscreen; no surface/swapchain needed
		using (X11Helper.XLock(_x11Window.Display))
		{
			_gc = X11Helper.XCreateGC(x11Window.Display, x11Window.Window, 0, 0);
			XWindowAttributes attr = default;
			_ = XLib.XGetWindowAttributes(_x11Window.Display, _x11Window.Window, ref attr);
			_depth = (uint)attr.depth;
		}
		if (this.Log().IsEnabled(LogLevel.Information))
		{
			this.Log().Info($"X11WebGpuRenderer: offscreen render + XPutImage (depth {_depth})");
		}
	}

	private void EnsureSize(int width, int height)
	{
		if (width == _w && height == _h && _buffer != IntPtr.Zero) { return; }
		_w = width; _h = height;
		if (_xImage != IntPtr.Zero) { ((XImage*)_xImage)->data = IntPtr.Zero; XLib.XDestroyImage(_xImage); _xImage = IntPtr.Zero; }
		if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); }
		_buffer = Marshal.AllocHGlobal(width * height * 4);
		_xImage = X11Helper.XCreateImage(_x11Window.Display, 0, _depth, 2 /*ZPixmap*/, 0, _buffer, (uint)width, (uint)height, 32, 0);
	}

	public override void Render()
	{
		if (_host is X11XamlRootHost { Closed.IsCompleted: true }) { return; }
		if (_host.RootElement?.Visual is not { } rootVisual) { return; }
		if (rootVisual.CompositionTarget is not CompositionTarget compositionTarget) { return; }

		// Route through Uno's frame pipeline: the WebGpuDrawList is built on the UI thread inside
		// CompositionTarget.Render() (right after the timeline tick), and this call drives the same
		// frame state machine + ticks animations, handing back the latest frame to present.
		// Reuse the draw list across frames (Reset() keeps the vertex-list capacity) instead of allocating one per
		// frame. TWO instances, alternated: the list is BUILT on the UI thread (CompositionTarget.Render) while
		// this render thread may still be CONSUMING the previous frame's list, so a single shared instance would
		// be Reset()/refilled mid-render (→ e.g. _backdrops cleared under RenderSegmented). Double-buffering keeps
		// the in-flight frame's list untouched (≤1 frame in flight); a resize makes a fresh instance.
		compositionTarget.WebGpuDrawListFactory ??=
			(w, h, scale) =>
			{
				// UNO_WEBGPU_FORCE_SCALE overrides the DPI scale — headless X11 reports 1.0, so this is how the
				// physical-pixel render path gets exercised/validated without a real high-DPI display.
				if (Environment.GetEnvironmentVariable("UNO_WEBGPU_FORCE_SCALE") is { Length: > 0 } fs
					&& float.TryParse(fs, System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced > 0)
				{
					scale = forced;
				}
				uint dw = (uint)Math.Max(1, w), dh = (uint)Math.Max(1, h);
				uint pw = (uint)Math.Round(dw * scale), ph = (uint)Math.Round(dh * scale);
				_dlToggle = !_dlToggle;
				var inst = _dlToggle ? _dlA : _dlB;
				if (inst is null || inst.Width != pw || inst.Height != ph)
				{
					inst = new WebGpuDrawList(dw, dh, scale);
					if (_dlToggle) { _dlA = inst; } else { _dlB = inst; }
				}
				else
				{
					inst.Reset();
				}
				return inst;
			};

		bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
		long t0 = perf ? Stopwatch.GetTimestamp() : 0;

		if (compositionTarget.OnNativePlatformFrameRequestedWebGpu() is not WebGpuDrawList drawList)
		{
			return; // no new frame this tick; the window keeps the previously presented pixels
		}

		_fpsHelper.OnFrameRecorded();
		using var fpsScope = _fpsHelper.BeginFrame();

		long t1 = perf ? Stopwatch.GetTimestamp() : 0;

		int width = (int)drawList.Width, height = (int)drawList.Height;
		EnsureSize(width, height);
		_fpsOverlay.Compose(_fpsHelper, drawList, width, height); // top-most counter, part of the normal render
		var bytes = drawList.Render(_ctx, WColor.FromArgb(255, 255, 255, 255), _targets); // RGBA

		long t2 = perf ? Stopwatch.GetTimestamp() : 0;

		// RGBA -> BGRA (X TrueColor, little-endian) into the XImage buffer.
		byte* dst = (byte*)_buffer;
		fixed (byte* src = bytes)
		{
			int n = width * height;
			for (int i = 0; i < n; i++)
			{
				int o = i * 4;
				dst[o] = src[o + 2]; dst[o + 1] = src[o + 1]; dst[o + 2] = src[o]; dst[o + 3] = src[o + 3];
			}
		}

		_fpsHelper.OnFramePresentRequested();

		using (X11Helper.XLock(_x11Window.Display))
		{
			if (_xImage != IntPtr.Zero)
			{
				_ = X11Helper.XPutImage(_x11Window.Display, _x11Window.Window, _gc, _xImage, 0, 0, 0, 0, (uint)width, (uint)height);
			}
			_ = XLib.XFlush(_x11Window.Display);
		}

		if (perf)
		{
			long t3 = Stopwatch.GetTimestamp();
			double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
			this.Log().Info($"[PERF] wgpu {width}x{height} build={Ms(t0, t1):F2} render={Ms(t1, t2):F2} blit={Ms(t2, t3):F2} total={Ms(t0, t3):F2} ms");
		}

		// Opt-in verification: overwrite the dump each frame so the file holds the latest settled
		// frame (static samples idle after a few frames). Set UNO_WEBGPU_DUMP=1 to enable.
		if (Environment.GetEnvironmentVariable("UNO_WEBGPU_DUMP") == "1")
		{
			// The frame-rate counter (when enabled) is part of the render output, so dumping the raw RGBA bytes
			// already reflects what's on screen. Write to a temp file then atomically rename, so a reader never
			// sees a half-written PNG (the process may be killed mid-write).
			try { PngWriter.Write("/tmp/uno-webgpu-frame.png.tmp", width, height, bytes); System.IO.File.Move("/tmp/uno-webgpu-frame.png.tmp", "/tmp/uno-webgpu-frame.png", true); } catch { }
		}
	}

	// Reads back the ACTUAL pixels the X server holds for the window — ground-truth "what's on screen".
	private void DumpOnScreen(int width, int height)
	{
		IntPtr img;
		using (X11Helper.XLock(_x11Window.Display))
		{
			img = X11Helper.XGetImage(_x11Window.Display, _x11Window.Window, 0, 0, (uint)width, (uint)height, ~0UL, 2 /*ZPixmap*/);
		}
		if (img == IntPtr.Zero) { this.Log().Error("XGetImage returned null"); return; }
		var x = (XImage*)img;
		int bpp = x->bits_per_pixel / 8;
		int stride = x->bytes_per_line;
		byte* data = (byte*)x->data;
		var rgba = new byte[width * height * 4];
		for (int y = 0; y < height; y++)
		{
			for (int xx = 0; xx < width; xx++)
			{
				byte* p = data + y * stride + xx * bpp; // BGRA/BGRX little-endian
				int o = (y * width + xx) * 4;
				rgba[o] = p[2]; rgba[o + 1] = p[1]; rgba[o + 2] = p[0]; rgba[o + 3] = 255;
			}
		}
		this.Log().Info($"on-screen XGetImage: depth={x->depth} bpp={x->bits_per_pixel} stride={stride} byte_order={x->byte_order}");
		XLib.XDestroyImage(img);
		PngWriter.Write("/tmp/uno-webgpu-onscreen.png", width, height, rgba);
	}

	protected override SkiaSharp.SKSurface UpdateSize(int width, int height) => throw new NotSupportedException();
	protected override void Flush() { }

	public override void Dispose()
	{
		if (_xImage != IntPtr.Zero) { ((XImage*)_xImage)->data = IntPtr.Zero; XLib.XDestroyImage(_xImage); }
		if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); }
		_targets.Dispose();
		_ctx.Dispose();
		_fpsHelper.Dispose();
		_fpsOverlay.Dispose();
	}
}
