#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Composition;
using Uno.UI.Dispatching;
using Uno.UI.Helpers;
using Uno.UI.Hosting;

namespace Microsoft.UI.Xaml.Media;

public partial class CompositionTarget
{
	internal static (bool invertNativeElementClipPath, bool applyScalingToNativeElementClipPath) FrameRenderingOptions { get; set; } = (false, true);

	private static readonly long _start = Stopwatch.GetTimestamp();
	// We're using this table as a set with weakref keys. values are always null
	private static readonly ConditionalWeakTable<CompositionTarget, object> _targets = new();
	private static bool _isRenderingActive;

	private readonly SkiaRenderHelper.FpsHelper _fpsHelper = new();
	private readonly Lock _frameGate = new();
	private readonly Lock _xamlRootBoundsGate = new();

	// Only read and set from the native rendering thread in OnNativePlatformFrameRequested
	private Size _lastCanvasSize = Size.Empty;
	private static SKPath? _lastNativeClipPath;
	private float _lastRasterizationScale = 1;
	private static SKPath? _lastScaledNativeClipPath;

	// only set on the UI thread and under _frameGate, only read under _frameGate
	private (IntPtr frame, SKPath nativeElementClipPath)? _lastRenderedFrame;

	// EXPERIMENTAL WebGPU backend: when a WebGPU renderer sets this factory, each UI-thread
	// Render() builds a WebGpuDrawList from the visual tree instead of recording an SKPicture.
	// The list is swapped here (under _frameGate) for the render thread to GPU-render + present.
	internal Func<int, int, float, Microsoft.UI.Composition.IWebGpuDrawList>? WebGpuDrawListFactory { get; set; }
	private Microsoft.UI.Composition.IWebGpuDrawList? _lastWebGpuDrawList; // under _frameGate

	// only set and read under _xamlRootBoundsGate
	private Size _xamlRootBounds;
	// only set and read under _xamlRootBoundsGate
	private float _xamlRootRasterizationScale;
	// only set and read on the UI thread
	private List<Visual> _nativeVisualsInZOrder = new();

	internal event Action? FrameRendered;

	private static event EventHandler<object>? _rendering;

	public static event EventHandler<object>? Rendering
	{
		add
		{
			NativeDispatcher.CheckThreadAccess();
			_rendering += value;
			if (!_isRenderingActive)
			{
				_isRenderingActive = true;
				foreach (var (target, _) in _targets)
				{
					((ICompositionTarget)target).RequestNewFrame();
				}
			}
		}
		remove
		{
			NativeDispatcher.CheckThreadAccess();
			_rendering -= value;
			if (_rendering == null)
			{
				_isRenderingActive = false;
			}
		}
	}

	private void Render()
	{
		this.LogTrace()?.Trace($"CompositionTarget#{GetHashCode()}: {nameof(Render)} begins with timestamp {Stopwatch.GetTimestamp()}");

		NativeDispatcher.CheckThreadAccess();

		var rootElement = ContentRoot.VisualTree.RootElement;
		var bounds = ContentRoot.VisualTree.Size;

		// EXPERIMENTAL WebGPU backend: walk the tree into a WebGpuDrawList instead of recording an
		// SKPicture. Runs here on the UI thread (right after the timeline tick), so it's safe from
		// concurrent Children mutations and reuses the entire frame state machine below.
		if (WebGpuDrawListFactory is { } webGpuFactory)
		{
			// bounds are in logical (view) pixels; the GPU frame must be rendered at physical pixels or it gets
			// upscaled at present (blurry + only partly covering a high-DPI window). Read the scale the same robust
			// way as UpdateXamlRootBoundsAndScale (DisplayInformation, not the async _rasterizationScale field).
			var rasterizationScale = rootElement.XamlRoot is { } xamlRoot
				? (float)XamlRoot.GetDisplayInformation(xamlRoot).RawPixelsPerViewPixel
				: 1f;
			// Perf bisection: UNO_WEBGPU_FORCE_SCALE pins the render scale so we can render the offscreen +
			// swapchain at logical size (=1) — the pre-DPI-fix behaviour — to confirm whether the physical-res
			// render is what introduced the present/acquire back-pressure on high-DPI displays.
			if (global::System.Environment.GetEnvironmentVariable("UNO_WEBGPU_FORCE_SCALE") is { Length: > 0 } forcedScaleText
				&& float.TryParse(forcedScaleText, global::System.Globalization.NumberStyles.Float, global::System.Globalization.CultureInfo.InvariantCulture, out var forcedScale)
				&& forcedScale > 0)
			{
				rasterizationScale = forcedScale;
			}
			// ≤1-in-flight backpressure. The draw list is built here (UI thread) and rendered on the render thread;
			// there are only two double-buffered draw-list instances. If the previous frame is still pending (the
			// render thread hasn't picked it up yet), DON'T build another: the UI build far outruns the vsync-paced
			// render, and building again would let the buffer-toggle rewrite the instance the render thread is mid-
			// render on → torn commands vs vertices (out-of-bounds draws). Skipping also avoids burning the UI thread
			// on frames that would just be dropped. When pending is null the render has already taken the last frame,
			// so the toggle picks the OTHER buffer — never the in-flight one — and the build safely overlaps the render.
			bool renderBusy;
			lock (_frameGate) { renderBusy = _lastWebGpuDrawList is not null; }
			if (!renderBusy)
			{
				var drawList = webGpuFactory((int)bounds.Width, (int)bounds.Height, rasterizationScale);
				bool webGpuPerf = global::System.Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
				long buildStart = webGpuPerf ? global::System.Diagnostics.Stopwatch.GetTimestamp() : 0;
				rootElement.Visual.RenderRootVisualWebGpu(drawList);
				if (webGpuPerf)
				{
					var buildMs = (global::System.Diagnostics.Stopwatch.GetTimestamp() - buildStart) * 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;
					global::System.Console.WriteLine($"[PERF] wgpu.build (UI-thread tree walk + glyph flatten)={buildMs:F2} ms");
				}
				lock (_frameGate)
				{
					_lastWebGpuDrawList = drawList;
				}

				_fpsHelper.OnFrameRecorded();
			}

			if (_isRenderingActive)
			{
				((ICompositionTarget)this).RequestNewFrame();
			}

			if (rootElement.XamlRoot is not null)
			{
				XamlRootMap.GetHostForRoot(rootElement.XamlRoot)?.InvalidateRender();
			}

			FrameRendered?.Invoke();
			return;
		}

		var (picture, path, nativeVisualsInZOrder) = SkiaRenderHelper.RecordPictureAndReturnPath(
			(float)bounds.Width,
			(float)bounds.Height,
			rootElement.Visual,
			invertPath: FrameRenderingOptions.invertNativeElementClipPath);
		var renderedFrame = (picture, path);
		var previousFrame = default((IntPtr frame, SKPath path)?);
		lock (_frameGate)
		{
			previousFrame = _lastRenderedFrame;

			_lastRenderedFrame = renderedFrame;
		}

		_fpsHelper.OnFrameRecorded();

		// Delete previous SKPicture now since we are swapping it
		if (previousFrame != null)
		{
			UnoSkiaApi.sk_refcnt_safe_unref(previousFrame.Value.frame);
		}

		if (_isRenderingActive)
		{
			((ICompositionTarget)this).RequestNewFrame();
		}

		if (rootElement.XamlRoot is not null)
		{
			XamlRootMap.GetHostForRoot(rootElement.XamlRoot)?.InvalidateRender();
		}

		var nativeVisualsZOrderChanged = _nativeVisualsInZOrder.Count != nativeVisualsInZOrder.Count;
		if (!nativeVisualsZOrderChanged)
		{
			for (int i = 0; i < nativeVisualsInZOrder.Count; i++)
			{
				if (nativeVisualsInZOrder[i] != _nativeVisualsInZOrder[i])
				{
					nativeVisualsZOrderChanged = true;
					break;
				}
			}
		}

		if (nativeVisualsZOrderChanged)
		{
			_nativeVisualsInZOrder = nativeVisualsInZOrder;
			ContentPresenter.OnNativeHostsRenderOrderChanged(nativeVisualsInZOrder);
		}

		FrameRendered?.Invoke();
		this.LogTrace()?.Trace($"CompositionTarget#{GetHashCode()}: {nameof(Render)} ends");
	}

	private SKPath Draw(SKCanvas? canvas, Func<Size, SKCanvas> resizeFunc)
	{
		this.LogTrace()?.Trace($"CompositionTarget#{GetHashCode()}: {nameof(Draw)}");

		(IntPtr frame, SKPath nativeElementClipPath)? lastRenderedFrameNullable;
		lock (_frameGate)
		{
			lastRenderedFrameNullable = _lastRenderedFrame;

			// Borrow frame temporarily
			_lastRenderedFrame = null;

			_fpsHelper.OnFramePresentRequested();
		}

		if (lastRenderedFrameNullable is not { } lastRenderedFrame)
		{
			return new SKPath();
		}
		else
		{
			Size xamlRootBounds;
			float rasterizationScale;
			lock (_xamlRootBoundsGate)
			{
				xamlRootBounds = _xamlRootBounds;
				rasterizationScale = _xamlRootRasterizationScale;
			}
			if (xamlRootBounds.Width <= 0 || xamlRootBounds.Height <= 0)
			{
				ReturnFrame(lastRenderedFrame);

				// Besides being an optimization step, returning early here also avoids resizing
				// the canvas to 0x0 which may crash on some targets
				return lastRenderedFrame.nativeElementClipPath;
			}
			if (canvas is null || _lastCanvasSize != xamlRootBounds || _lastRasterizationScale != rasterizationScale)
			{
				canvas = resizeFunc(new Size(Math.Round(xamlRootBounds.Width * rasterizationScale), Math.Round(xamlRootBounds.Height * rasterizationScale)));
				_lastCanvasSize = xamlRootBounds;
				_lastRasterizationScale = rasterizationScale;
				_lastScaledNativeClipPath = null;
			}

			canvas.Save();
			if (rasterizationScale != 1)
			{
				canvas.Scale(rasterizationScale, rasterizationScale);
			}
			using var fpsHelperDisposable = _fpsHelper.BeginFrame();
			SkiaRenderHelper.RenderPicture(
				canvas,
				lastRenderedFrame.frame,
				SKColors.Transparent,
				_fpsHelper.DrawFps);
			canvas.Restore();

			ReturnFrame(lastRenderedFrame);

			InvokeRendering();

			if (FrameRenderingOptions.applyScalingToNativeElementClipPath && rasterizationScale != 1)
			{
				if (_lastNativeClipPath != lastRenderedFrame.nativeElementClipPath || _lastScaledNativeClipPath == null)
				{
					_lastScaledNativeClipPath = new();

					lastRenderedFrame
						.nativeElementClipPath
						.Transform(SKMatrix.CreateScale(rasterizationScale, rasterizationScale), _lastScaledNativeClipPath);

					_lastNativeClipPath = lastRenderedFrame.nativeElementClipPath;
				}

				return _lastScaledNativeClipPath;
			}

			return lastRenderedFrame.nativeElementClipPath;
		}
	}

	private void ReturnFrame((IntPtr picture, SKPath path) frame)
	{
		var pictureToDelete = IntPtr.Zero;

		lock (_frameGate)
		{
			// Put the frame back unless it has changed
			if (_lastRenderedFrame == null)
			{
				_lastRenderedFrame = frame;
			}
			else
			{
				pictureToDelete = frame.picture;
			}
		}

		// Delete it then
		if (pictureToDelete != IntPtr.Zero)
		{
			UnoSkiaApi.sk_refcnt_safe_unref(pictureToDelete);
		}
	}

	internal static void InvokeRendering()
	{
		if (NativeDispatcher.Main.HasThreadAccess)
		{
			_rendering?.Invoke(null, new RenderingEventArgs(Stopwatch.GetElapsedTime(_start)));
		}
		else
		{
			NativeDispatcher.Main.Enqueue(() =>
			{
				_rendering?.Invoke(null, new RenderingEventArgs(Stopwatch.GetElapsedTime(_start)));
			}, NativeDispatcherPriority.High);
		}
	}
}
