using System;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// Renderer using Skia Graphite with Vulkan backend.
/// Renders offscreen via Graphite Recorder, then blits to X11 via XPutImage.
/// </summary>
internal class X11VulkanGraphiteRenderer : X11Renderer
{
	private const int BitmapPad = 32;

	private readonly VulkanInterop.VulkanContext _vkContext;
	private readonly GraphiteContext _graphiteContext;

	private GraphiteRecorder? _recorder;
	private SKSurface? _graphiteSurface;
	private SKBitmap? _blitBitmap;
	private IntPtr? _xImage;
	private readonly IntPtr _gc;
	private int _width;
	private int _height;
	private readonly uint _depth;

	public X11VulkanGraphiteRenderer(IXamlRootHost host, X11Window x11Window) : base(host, x11Window)
	{
		// Initialize Vulkan
		var vkContext = VulkanInterop.CreateVulkanContext()
			?? throw new NotSupportedException("Failed to create Vulkan context for Graphite renderer.");

		try
		{
			// Create GetProcAddress delegate for Skia
			using var backendContext = new GRVkBackendContext
			{
				VkInstance = vkContext.Instance,
				VkPhysicalDevice = vkContext.PhysicalDevice,
				VkDevice = vkContext.Device,
				VkQueue = vkContext.Queue,
				GraphicsQueueIndex = vkContext.GraphicsQueueIndex,
				MaxAPIVersion = VulkanInterop.VK_API_VERSION_1_1,
				GetProcedureAddress = (name, instance, device) =>
				{
					if (device != IntPtr.Zero)
					{
						var proc = VulkanInterop.vkGetDeviceProcAddr(device, name);
						if (proc != IntPtr.Zero)
						{
							return proc;
						}
					}
					return VulkanInterop.vkGetInstanceProcAddr(
						instance != IntPtr.Zero ? instance : vkContext.Instance, name);
				}
			};

			var graphiteContext = GraphiteContext.CreateVulkan(backendContext)
				?? throw new NotSupportedException("Failed to create Graphite Vulkan context.");

			_vkContext = vkContext;
			_graphiteContext = graphiteContext;
		}
		catch
		{
			vkContext.Dispose();
			throw;
		}

		// Setup X11 blit resources
		using var lockDisposable = X11Helper.XLock(x11Window.Display);
		_gc = X11Helper.XCreateGC(x11Window.Display, x11Window.Window, 0, 0);
		XWindowAttributes attributes = default;
		_ = XLib.XGetWindowAttributes(x11Window.Display, x11Window.Window, ref attributes);
		_depth = (uint)attributes.depth;

		if (this.Log().IsEnabled(LogLevel.Information))
		{
			this.Log().Info($"Using Graphite (Vulkan) for rendering. MaxTextureSize={_graphiteContext.MaxTextureSize}");
		}
	}

	protected override SKSurface UpdateSize(int width, int height)
	{
		_width = width;
		_height = height;

		// Dispose old resources
		_graphiteSurface?.Dispose();
		_recorder?.Dispose();

		// Create new Graphite recorder and render target
		_recorder = _graphiteContext.MakeRecorder()
			?? throw new InvalidOperationException("Failed to create Graphite Recorder.");

		_graphiteSurface = _recorder.MakeRenderTarget(
			new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
			?? throw new InvalidOperationException("Failed to create Graphite render target surface.");

		// Prepare blit bitmap for X11 presentation
		_blitBitmap?.Dispose();
		_blitBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

		if (_xImage is { } xImage)
		{
			unsafe
			{
				var ptr = (XImage*)xImage.ToPointer();
				ptr->data = IntPtr.Zero;
			}
			_ = XLib.XDestroyImage(xImage);
		}

		_xImage = X11Helper.XCreateImage(
			display: _x11Window.Display,
			visual: 0,
			depth: _depth,
			format: 2, // ZPixmap
			offset: 0,
			data: _blitBitmap.GetPixels(),
			width: (uint)width,
			height: (uint)height,
			bitmap_pad: BitmapPad,
			bytes_per_line: 0);

		return _graphiteSurface;
	}

	protected override void Flush()
	{
		if (_recorder is null || _graphiteSurface is null || _blitBitmap is null)
		{
			return;
		}

		// Snap the recording and submit to GPU
		using var recording = _recorder.Snap();
		if (recording is not null)
		{
			_graphiteContext.InsertRecording(recording);
			_graphiteContext.Submit(syncToCpu: true);
		}

		// Read pixels from Graphite surface into blit bitmap
		var dstInfo = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
		_graphiteSurface.ReadPixels(dstInfo, _blitBitmap.GetPixels(), dstInfo.RowBytes, 0, 0);

		// Blit to X11 window
		_ = X11Helper.XPutImage(
			display: _x11Window.Display,
			drawable: _x11Window.Window,
			gc: _gc,
			image: _xImage!.Value,
			srcx: 0,
			srcy: 0,
			destx: 0,
			desty: 0,
			width: (uint)_width,
			height: (uint)_height);

		// Check device health
		if (_graphiteContext.IsDeviceLost)
		{
			this.Log().Error("Graphite: Vulkan device lost during rendering.");
		}

		// Recreate recorder for next frame
		_recorder.Dispose();
		_recorder = _graphiteContext.MakeRecorder();
		if (_recorder is not null)
		{
			_graphiteSurface.Dispose();
			_graphiteSurface = _recorder.MakeRenderTarget(
				new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul));
		}
	}

	public override void Dispose()
	{
		_graphiteSurface?.Dispose();
		_recorder?.Dispose();
		_blitBitmap?.Dispose();

		if (_xImage is { } xImage)
		{
			unsafe
			{
				var ptr = (XImage*)xImage.ToPointer();
				ptr->data = IntPtr.Zero;
			}
			_ = XLib.XDestroyImage(xImage);
		}

		_graphiteContext.Dispose();
		_vkContext.Dispose();
	}
}
