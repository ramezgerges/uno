#nullable enable

using System;
using System.Numerics;
using Windows.Foundation;
using Microsoft.UI.Composition;
using SkiaSharp;
using Uno.Foundation.Extensibility;
using Uno.Foundation.Logging;
using Uno.Graphics;

namespace Uno.UI.Graphics;

internal class SKCanvasVisual(Action<object, Size> renderCallback, Compositor compositor) : SKCanvasVisualBase(renderCallback, compositor)
{
	// Hardware-accelerated Skia offscreen for the WebGPU render path. WebGPU owns the frame, so we can't draw onto
	// the frame's SKCanvas directly; instead we render this element's Skia content into a Ganesh-on-GL surface,
	// read it back, and hand the pixels to the backend as an image. If a GL context can't be created we fall back
	// to a CPU-raster SKSurface (still correct, just not hardware-accelerated).
	private INativeOpenGLWrapper? _glWrapper;
	private GRContext? _grContext;
	private GRGlInterface? _glInterface;
	private bool _glInitTried;
	private SKSurface? _surface;
	private int _surfaceW, _surfaceH;

	internal override void Paint(in PaintingSession session)
	{
		// We save and restore the canvas state ourselves so that the inheritor doesn't accidentally forget to.
		session.Canvas.Save();
		// clipping here guarantees that drawing doesn't get outside the intended area
		session.Canvas.ClipRect(new SKRect(0, 0, Size.X, Size.Y), antialias: true);
		RenderCallback(session.Canvas, Size.ToSize());
		session.Canvas.Restore();
	}

	internal override unsafe void PaintWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity)
	{
		if (Size.X <= 0 || Size.Y <= 0)
		{
			return;
		}

		var xamlRoot = XamlRootProvider?.Invoke();
		var scale = (float)(xamlRoot?.RasterizationScale ?? 1.0);
		int pw = Math.Max(1, (int)MathF.Ceiling(Size.X * scale));
		int ph = Math.Max(1, (int)MathF.Ceiling(Size.Y * scale));

		EnsureGlContext(xamlRoot);

		using var _ = _glWrapper?.MakeCurrent();

		if (!EnsureSurface(pw, ph))
		{
			return;
		}

		var canvas = _surface!.Canvas;
		canvas.Clear(SKColors.Transparent);
		canvas.Save();
		canvas.Scale(scale);
		canvas.ClipRect(new SKRect(0, 0, Size.X, Size.Y), antialias: true);
		RenderCallback(canvas, Size.ToSize());
		canvas.Restore();
		_surface.Flush();

		// Unpremultiplied RGBA8888: the backend's image pipeline blends with SrcAlpha (i.e. expects straight alpha).
		var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var pixels = new byte[info.BytesSize];
		fixed (byte* p = pixels)
		{
			if (!_surface.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0))
			{
				return;
			}
		}

		var clipVec = new Vector4(clipInRoot.Left, clipInRoot.Top, clipInRoot.Right, clipInRoot.Bottom);
		draw.AddImage(TotalMatrix, Vector2.Zero, new Vector2(Size.X, Size.Y), pixels, pw, ph, opacity, clipVec);
	}

	private void EnsureGlContext(Microsoft.UI.Xaml.XamlRoot? xamlRoot)
	{
		if (_glInitTried || xamlRoot is null)
		{
			return;
		}
		_glInitTried = true;

		try
		{
			if (ApiExtensibility.CreateInstance(xamlRoot, out INativeOpenGLWrapper? wrapper) && wrapper is not null)
			{
				using (wrapper.MakeCurrent())
				{
					var glInterface = GRGlInterface.Create();
					var ctx = GRContext.CreateGl(glInterface);
					if (ctx is not null)
					{
						_glWrapper = wrapper;
						_glInterface = glInterface;
						_grContext = ctx;
					}
					else
					{
						glInterface?.Dispose();
						wrapper.Dispose();
					}
				}
			}
		}
		catch (Exception e)
		{
			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().Warn("Failed to create a GL context for hardware-accelerated SKCanvasElement; falling back to software rendering.", e);
			}
		}
	}

	private bool EnsureSurface(int pw, int ph)
	{
		if (_surface is not null && _surfaceW == pw && _surfaceH == ph)
		{
			return true;
		}

		_surface?.Dispose();
		_surface = null;

		var info = new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul);
		if (_grContext is not null)
		{
			_surface = SKSurface.Create(_grContext, true, info);
		}
		_surface ??= SKSurface.Create(info);

		_surfaceW = pw;
		_surfaceH = ph;
		return _surface is not null;
	}

	internal override bool CanPaint() => true;
	public override void Invalidate() => Compositor.InvalidateRender(this);
}
