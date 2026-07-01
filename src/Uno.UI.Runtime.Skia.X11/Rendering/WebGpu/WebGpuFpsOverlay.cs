#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using SkiaSharp;
using Uno.UI.Helpers;

namespace WebGpuExperiment;

// Composites the frame-rate counter (identical look to the Skia path's FpsHelper) into the WebGPU draw list as a
// final, top-most image. Going through AddImage means the panel rides the normal render and works on EVERY present
// path (offscreen→readback→blit AND swapchain) with no per-backend code. The panel is painted into a reusable CPU
// bitmap and handed over as unpremultiplied RGBA in device pixels; the buffers are reused frame-to-frame so the
// counter never churns the (LOH-sized) pixel array.
internal sealed class WebGpuFpsOverlay : IDisposable
{
	// Generous fixed canvas: the panel anchors at the top-left and the remainder stays transparent (it composites
	// to nothing), so we don't need DrawFps to report its dynamic panel size.
	private const int PanelW = 360;
	private const int PanelH = 112;

	private SKBitmap? _bitmap;
	private SKCanvas? _canvas;
	private byte[]? _rgba;

	public void Compose(SkiaRenderHelper.FpsHelper fps, IWebGpuDrawList draw, int width, int height)
	{
		if (!fps.Enabled)
		{
			return;
		}

		if (_bitmap is null)
		{
			_bitmap = new SKBitmap(new SKImageInfo(PanelW, PanelH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
			_canvas = new SKCanvas(_bitmap);
			_rgba = new byte[PanelW * PanelH * 4];
		}

		_canvas!.Clear(SKColors.Transparent);
		fps.DrawFps(_canvas);
		Marshal.Copy(_bitmap.GetPixels(), _rgba!, 0, _rgba!.Length);

		// Identity matrix → local coordinates are logical pixels; the panel lands at the top-left corner. The draw
		// list bakes the DPI scale into both the quad and the clip, so the panel is sized/clipped in logical units.
		draw.AddImage(Matrix4x4.Identity, Vector2.Zero, new Vector2(PanelW, PanelH), _rgba, PanelW, PanelH, 1f,
			new Vector4(0, 0, PanelW, PanelH));
	}

	public void Dispose()
	{
		_canvas?.Dispose();
		_bitmap?.Dispose();
	}
}
