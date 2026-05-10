using System;
using System.Threading;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using IOSurface;
using Metal;
using MetalKit;
using Microsoft.Graphics.Display;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using UIKit;
using Uno.Foundation.Logging;
using Uno.UI.Dispatching;
using Uno.UI.Helpers;

namespace Uno.UI.Runtime.Skia.AppleUIKit
{
	internal sealed partial class UnoSKMetalView : MTKView, IMTKViewDelegate
	{
		private readonly SKGraphiteContext? _context;
		private readonly SKGraphiteRecorder? _recorder;
		private readonly IMTLCommandQueue? _queue;

		private RootViewController? _owner;
		private CADisplayLink _link;
		private Thread? _renderThread;

		/// <summary>
		/// Creates a new instance of <see cref="UnoSKMetalView"/>.
		/// </summary>
		/// <param name="onFrameDrawn">A delegate that will be called on a separate thread once per frame draw.</param>
		public UnoSKMetalView()
			: base(CGRect.Empty, null)
		{
			_link = CADisplayLink.Create(() => this.Draw());
			var device = MTLDevice.SystemDefault;

			if (device == null)
			{
				Console.WriteLine("Metal is not supported on this device.");
				return;
			}

			var queue = device.CreateCommandQueue();

			if (queue == null)
			{
				Console.WriteLine("Failed to create command queue.");

				return;
			}

			// Graphite-backed context. Skia CFRetains the device/queue handles inside the
			// native MtlBackendContext, so the SKGraphiteMtlBackendContext wrapper is
			// safe to dispose right after CreateMetal returns.
			using (var bc = new SKGraphiteMtlBackendContext { MtlDevice = device.Handle, MtlQueue = queue.Handle })
			{
				_context = SKGraphiteContext.CreateMetal(bc);
			}
			// Without an ImageProvider, Graphite drops every draw whose source SkImage
			// isn't already Graphite-backed. The Default provider uploads-on-demand
			// with an LRU cache; sufficient as a baseline policy.
			_recorder = _context?.CreateRecorder(-1, SKGraphiteImageProvider.Default);

			_queue = queue;

			ColorPixelFormat = MTLPixelFormat.BGRA8Unorm;
			DepthStencilPixelFormat = MTLPixelFormat.Depth32Float_Stencil8;
			SampleCount = 1;

			FramebufferOnly = false;

			// Disable UIKit’s display‑link
			Paused = true;

			// We're drawing ourselves
			EnableSetNeedsDisplay = false;

			var fps = UIScreen.MainScreen.MaximumFramesPerSecond;
			PreferredFramesPerSecond = fps;

			this.LogDebug()?.LogDebug($"UnoSKMetalView: {nameof(PreferredFramesPerSecond)} = {fps}");

			Device = device;

			Delegate = this;

			StartRenderThread();
		}

		private void StartRenderThread()
		{
			_renderThread = new Thread(() =>
			{
				var currentThread = NSThread.Current;
				currentThread.QualityOfService = NSQualityOfService.UserInteractive;
				currentThread.Name = "UnoSKMetalViewRenderThread";

				// CAFrameRateRange is only available on iOS 15.0+
				if (UIDevice.CurrentDevice.CheckSystemVersion(15, 0))
				{
					_link.PreferredFrameRateRange = new CAFrameRateRange()
					{
						Minimum = 30,
						Preferred = PreferredFramesPerSecond,
						Maximum = PreferredFramesPerSecond
					};
				}
				else
				{
					// Fallback for iOS < 15.0: use the deprecated PreferredFramesPerSecond property
					// Note: The legacy API doesn't support setting minimum/maximum frame rates,
					// so we only set the preferred rate. This provides best-effort frame rate control.
#pragma warning disable CA1422 // Validate platform compatibility
					_link.PreferredFramesPerSecond = PreferredFramesPerSecond;
#pragma warning restore CA1422 // Validate platform compatibility
				}

				_link.AddToRunLoop(NSRunLoop.Current, NSRunLoopMode.Default);

				NSRunLoop.Current.Run();   // blocks forever
			})
			{
				IsBackground = true,
				Name = "UnoSKMetalViewRenderThread"
			};
			_renderThread.Start();
		}

		internal void SetOwner(RootViewController owner) => _owner = owner;

		public void QueueRender()
		{
			_link.Paused = false;
		}

		void IMTKViewDelegate.DrawableSizeWillChange(MTKView view, CGSize size)
		{
			if (Paused && EnableSetNeedsDisplay)
			{
				SetNeedsDisplay();
			}
		}

#if REPORT_FPS
		static FrameRateLogger _drawFpsLogger = new FrameRateLogger(typeof(UnoSKMetalView), "Draw");
#endif

		void IMTKViewDelegate.Draw(MTKView view)
		{
#if REPORT_FPS
			_drawFpsLogger.ReportFrame();
#endif

			_link.Paused = true;

			var size = DrawableSize;

			var width = (int)size.Width;
			var height = (int)size.Height;

			SKSurface? surface = null;
			SKCanvas? canvas = null;
			SKGraphiteBackendTexture? backendTexture = null;
			ICAMetalDrawable? drawable = null;
			IMTLCommandBuffer? commandBuffer = null;

			try
			{
#if __TVOS__ // TODO: tvOS is not supported yet.
				surface = SKSurface.CreateNull(width, height);
				canvas = surface.Canvas;
				_owner?.OnRenderFrameRequested(canvas);
#else
				if (_context is null || _recorder is null)
				{
					return;
				}

				// Acquire the drawable upfront on the Graphite path — we need its
				// underlying MTLTexture to wrap as a BackendTexture before any
				// drawing can be recorded. (The Ganesh helper that took an MTKView
				// directly did this internally.)
				drawable = CurrentDrawable;
				if (drawable is null)
				{
					return;
				}

				backendTexture = SKGraphiteBackendTexture.CreateMetal(width, height, drawable.Texture.Handle);
				if (backendTexture is null)
				{
					return;
				}
				surface = SKSurface.Create(_recorder, backendTexture, SKColorType.Bgra8888);
				if (surface is null)
				{
					return;
				}
				canvas = surface.Canvas;

				_owner?.OnRenderFrameRequested(canvas);

				// Snap + insert + submit — Graphite equivalent of GRContext.Flush(submit: true).
				using (var recording = _recorder.Snap())
				{
					if (recording is not null)
					{
						_context.InsertRecording(recording);
					}
				}
				_context.Submit();

				// Present the drawable we already acquired.
				commandBuffer = _queue!.CommandBuffer()!;
				commandBuffer.PresentDrawable(drawable);
				commandBuffer.Commit();
#endif
			}
			finally
			{
				// Release the drawable as soon as possible
				// See : https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Drawables.html
				((IDisposable?)commandBuffer)?.Dispose();
				((IDisposable?)drawable)?.Dispose();
				((IDisposable?)canvas)?.Dispose();
				((IDisposable?)surface)?.Dispose();
				backendTexture?.Dispose();
			}
		}
	}
}
