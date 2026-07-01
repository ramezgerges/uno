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
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using WColor = Windows.UI.Color;

namespace Uno.UI.Runtime.Skia.Win32;

// EXPERIMENTAL: renders the visual tree with WebGPU on Win32 (opt-in via UNO_WEBGPU=1). Like the X11
// backend it renders OFFSCREEN with WebGPU, reads the result back, then blits to the HWND via a top-down
// DIB section + BitBlt (the software renderer's proven path). On a machine with a real GPU the offscreen
// render runs on the GPU; the readback/blit is the only CPU round-trip (a swapchain present would remove it).
internal partial class Win32WindowWrapper
{
	private readonly bool _useWebGpu = Environment.GetEnvironmentVariable("UNO_WEBGPU") == "1";
	private WebGpuContext? _webGpuContext;
	private readonly WebGpuTargets _webGpuTargets = new(); // persistent render textures, reused across frames
	private readonly SkiaRenderHelper.FpsHelper _webGpuFpsHelper = new(); // same on-screen counter as the Skia path
	private readonly WebGpuFpsOverlay _webGpuFpsOverlay = new(); // composites that counter into the draw list
	private WebGpuDrawList? _dlA, _dlB; // double-buffered, reused across frames (see the factory for why two)
	private bool _dlToggle;
	private HBITMAP _webGpuBitmap;
	private IntPtr _webGpuBits;
	private int _webGpuW, _webGpuH;
	private bool _webGpuAdapterLogged;

	// One-shot: surface which GPU/backend wgpu-native actually bound ("name | backend | type"). A slow
	// resolution-bound frame on an integrated GPU, the GL backend, or a Cpu (WARP/llvmpipe) adapter looks
	// nothing like the discrete D3D12/Vulkan path, so this is the first thing to check when frames are slow.
	private void LogWebGpuAdapterOnce()
	{
		if (_webGpuAdapterLogged || _webGpuContext is null) { return; }
		_webGpuAdapterLogged = true;
		Console.WriteLine($"[PERF] WebGPU adapter: {_webGpuContext.AdapterSummary}");
	}

	private unsafe void EnsureWebGpuSize(int width, int height)
	{
		if (width == _webGpuW && height == _webGpuH && _webGpuBitmap != HBITMAP.Null) { return; }
		_webGpuW = width; _webGpuH = height;
		if (_webGpuBitmap != HBITMAP.Null) { _ = PInvoke.DeleteObject(_webGpuBitmap); }

		var info = new BITMAPINFO
		{
			bmiHeader = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				biHeight = -height, // negative → top-down rows (match the WebGPU readback order)
				biPlanes = 1,
				biBitCount = 32,
				biCompression = /* BI_RGB */ 0x0000,
			}
		};
		void* bits;
		_webGpuBitmap = PInvoke.CreateDIBSection(new HDC(IntPtr.Zero), &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
		if (_webGpuBitmap == HBITMAP.Null)
		{
			throw new InvalidOperationException($"{nameof(PInvoke.CreateDIBSection)} failed: {Win32Helper.GetErrorMessage()}");
		}
		_webGpuBits = (IntPtr)bits;
	}

	private unsafe void RenderWebGpu()
	{
		if (((IXamlRootHost)this).RootElement?.Visual is not { } rootVisual) { return; }
		if (rootVisual.CompositionTarget is not CompositionTarget compositionTarget) { return; }

		// Same frame pipeline as Skia: the draw list is BUILT on the UI thread inside CompositionTarget.Render()
		// while this render thread may still be CONSUMING the previous frame's list. Reuse the list across frames
		// (Reset() keeps the vertex-list capacity) but with TWO instances alternated, so the in-flight frame's
		// list is never Reset()/refilled mid-render (≤1 frame in flight); a resize makes a fresh instance.
		compositionTarget.WebGpuDrawListFactory ??=
			(w, h, scale) =>
			{
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

		_webGpuContext ??= new WebGpuContext("webgpu-win32");
		LogWebGpuAdapterOnce();

		// Swapchain present: render offscreen (no readback) and GPU-blit to the backbuffer. Falls back to the
		// readback+BitBlt path below if it fails (so rendering is never broken). The frame-rate counter rides the
		// draw list as a top-most image, so it works on this path too — no readback fallback needed for it.
		// UNO_WEBGPU_NOSWAPCHAIN=1 forces the readback+GDI path: the offscreen pixels are byte-identical either way,
		// so this isolates whether a colour difference comes from the swapchain present vs the rendering.
		if (Environment.GetEnvironmentVariable("UNO_WEBGPU_NOSWAPCHAIN") != "1" && TryRenderWebGpuSwapchain(compositionTarget))
		{
			return;
		}

		bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
		long t0 = perf ? Stopwatch.GetTimestamp() : 0;

		if (compositionTarget.OnNativePlatformFrameRequestedWebGpu() is not WebGpuDrawList drawList)
		{
			return; // no new frame this tick; the window keeps the previously presented pixels
		}

		_webGpuFpsHelper.OnFrameRecorded();
		using var fpsScope = _webGpuFpsHelper.BeginFrame();

		long t1 = perf ? Stopwatch.GetTimestamp() : 0;

		_webGpuContext ??= new WebGpuContext("webgpu-win32"); // offscreen; no surface/swapchain needed
		LogWebGpuAdapterOnce();
		int width = (int)drawList.Width, height = (int)drawList.Height;
		EnsureWebGpuSize(width, height);
		_webGpuFpsOverlay.Compose(_webGpuFpsHelper, drawList, width, height); // top-most counter, part of the render
		var bytes = drawList.Render(_webGpuContext, WColor.FromArgb(255, 255, 255, 255), _webGpuTargets); // RGBA

		long t2 = perf ? Stopwatch.GetTimestamp() : 0;

		// RGBA -> BGRA (the DIB is 32-bit BI_RGB = BGRX little-endian) into the DIB section's pixels.
		byte* dst = (byte*)_webGpuBits;
		fixed (byte* src = bytes)
		{
			int n = width * height;
			for (int i = 0; i < n; i++)
			{
				int o = i * 4;
				dst[o] = src[o + 2]; dst[o + 1] = src[o + 1]; dst[o + 2] = src[o]; dst[o + 3] = src[o + 3];
			}
		}

		_webGpuFpsHelper.OnFramePresentRequested();

		BlitWebGpu(width, height);

		if (perf)
		{
			long t3 = Stopwatch.GetTimestamp();
			double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
			Console.WriteLine($"[PERF] wgpu {width}x{height} build={Ms(t0, t1):F2} render={Ms(t1, t2):F2} blit={Ms(t2, t3):F2} total={Ms(t0, t3):F2} ms");
		}
	}

	private unsafe void BlitWebGpu(int width, int height)
	{
		var paintDc = PInvoke.GetDC(_hwnd);
		if (paintDc == new HDC(IntPtr.Zero)) { this.LogError()?.Error($"{nameof(PInvoke.GetDC)} failed: {Win32Helper.GetErrorMessage()}"); return; }
		try
		{
			var bitmapDc = PInvoke.CreateCompatibleDC(paintDc);
			if (bitmapDc == new HDC(IntPtr.Zero)) { this.LogError()?.Error($"{nameof(PInvoke.CreateCompatibleDC)} failed: {Win32Helper.GetErrorMessage()}"); return; }
			try
			{
				if (PInvoke.SelectObject(bitmapDc, _webGpuBitmap) == 0) { this.LogError()?.Error($"{nameof(PInvoke.SelectObject)} failed: {Win32Helper.GetErrorMessage()}"); return; }
				if (!PInvoke.BitBlt(paintDc, 0, 0, width, height, bitmapDc, 0, 0, ROP_CODE.SRCCOPY)) { this.LogError()?.Error($"{nameof(PInvoke.BitBlt)} failed: {Win32Helper.GetErrorMessage()}"); }
			}
			finally
			{
				_ = PInvoke.DeleteObject(new HGDIOBJ(bitmapDc.Value));
			}
		}
		finally
		{
			_ = PInvoke.ReleaseDC(_hwnd, paintDc);
		}
	}

	private void DisposeWebGpu()
	{
		if (_webGpuBitmap != HBITMAP.Null) { _ = PInvoke.DeleteObject(_webGpuBitmap); _webGpuBitmap = HBITMAP.Null; }
		DisposeSwapchain();
		_webGpuTargets.Dispose();
		_webGpuFpsHelper.Dispose();
		_webGpuFpsOverlay.Dispose();
		_webGpuContext?.Dispose();
		_webGpuContext = null;
	}
}
