#nullable enable
using System;
using System.Runtime.InteropServices;
using Uno.UI.Runtime.Skia.Vulkan.Interop;
using Uno.UI.Runtime.Skia.Vulkan.UnmanagedInterop;
using SkiaSharp;

namespace Uno.UI.Runtime.Skia.Vulkan;

/// <summary>
/// Unified Vulkan rendering context for Uno Platform.
/// Implements IVulkanPlatformGraphicsContext so it can be passed to all Interop/ classes.
/// Manages the full lifecycle: instance, device, surface, swapchain, and SkiaSharp integration.
/// </summary>
internal sealed class VulkanContext : IVulkanPlatformGraphicsContext, IDisposable
{
	private IVulkanInstance? _instance;
	private VulkanDevice? _device;
	private VulkanDisplay? _display;
	private VulkanImage? _renderImage;
	private SKGraphiteContext? _graphiteContext;
	private SKGraphiteRecorder? _recorder;
	private VulkanInstanceApi? _instanceApi;
	private VulkanDeviceApi? _deviceApi;
	private bool _disposed;
	private IVulkanPlatformSurfaceFactory? _factory;
	private IntPtr _nativeWindowHandle;

	// Cached per-size Skia resources — reused across frames, only recreated on resize.
	// Graphite uses an opaque BackendTexture wrapping the VkImage instead of Ganesh's
	// GRBackendRenderTarget; the surface is created from that.
	private SKGraphiteBackendTexture? _cachedBackendTexture;
	private SKSurface? _cachedSkSurface;

	/// <summary>
	/// Legacy Ganesh getter — always null on the Graphite path. Kept temporarily
	/// for source compatibility with renderers that haven't migrated yet; new
	/// callers should use <see cref="GraphiteContext"/> + <see cref="EndFrame"/>.
	/// </summary>
	public GRContext? GrContext => null;
	public SKGraphiteContext? GraphiteContext => _graphiteContext;
	public SKSurface? CachedSkSurface => _cachedSkSurface;
	public bool IsInitialized => _graphiteContext != null;

	// IVulkanPlatformGraphicsContext implementation
	public IVulkanDevice Device => _device!;
	public IVulkanInstance Instance => _instance!;
	public VulkanInstanceApi InstanceApi => _instanceApi!;
	public VulkanDeviceApi DeviceApi => _deviceApi!;
	public VkDevice DeviceHandle => new() { Handle = _device!.Handle };
	public VkPhysicalDevice PhysicalDeviceHandle => new() { Handle = _device!.PhysicalDeviceHandle };
	public VkInstance InstanceHandle => new() { Handle = _instance!.Handle };
	public VkQueue MainQueueHandle => new() { Handle = _device!.MainQueueHandle };
	public uint GraphicsQueueFamilyIndex => _device!.GraphicsQueueFamilyIndex;

	/// <summary>
	/// Initialize the Vulkan context with a platform-specific surface factory and native window handle.
	/// </summary>
	public void Initialize(IVulkanPlatformSurfaceFactory factory, IntPtr nativeWindowHandle, int width, int height)
	{
		_factory = factory;
		_nativeWindowHandle = nativeWindowHandle;

		// Create Vulkan instance
		var getProcAddr = factory.GetVkGetInstanceProcAddr();
		_instance = VulkanInstance.Create(getProcAddr, factory.RequiredInstanceExtensions);

		// Create instance API
		_instanceApi = new VulkanInstanceApi(_instance);

		// Create surface so we can check device presentation support
		var surfaceHandle = factory.CreateSurface((VulkanInstance)_instance, nativeWindowHandle);
		var vkSurface = new VkSurfaceKHR { Handle = surfaceHandle };

		// Create device (checks surface presentation support)
		_device = VulkanDevice.Create((VulkanInstance)_instance, _instanceApi, vkSurface);

		// Destroy the temporary surface used for device selection
		_instanceApi.DestroySurfaceKHR(new VkInstance { Handle = _instance.Handle }, vkSurface, IntPtr.Zero);

		// Create device API
		_deviceApi = new VulkanDeviceApi(_device);

		// All subsequent operations access device handles and require the device lock
		using (_device.Lock())
		{
			// Create display (swapchain) via platform surface wrapper
			var platformSurface = new DirectVulkanSurface(nativeWindowHandle, new SKSizeI(width, height), factory);
			_display = VulkanDisplay.CreateDisplay(this, platformSurface);

			// Create intermediate render image (TransitionLayout submits commands, needs lock)
			_renderImage = new VulkanImage(this, _display.CommandBufferPool,
				_display.SurfaceFormat.format, new SKSizeI(width, height));

			// Create SkiaSharp Graphite context
			CreateGraphiteContext();
		}
	}

	private void CreateGraphiteContext()
	{
		if (_device == null || _instance == null)
			throw new InvalidOperationException("Vulkan device not initialized");

		IntPtr GetProcAddressWrapper(string name, IntPtr instance, IntPtr device)
		{
			if (device != IntPtr.Zero)
			{
				var addr = _instance.GetDeviceProcAddress(device, name);
				if (addr != IntPtr.Zero)
					return addr;
			}

			if (instance != IntPtr.Zero)
			{
				var addr = _instance.GetInstanceProcAddress(instance, name);
				if (addr != IntPtr.Zero)
					return addr;
			}

			return _instance.GetInstanceProcAddress(IntPtr.Zero, name);
		}

		using var bc = new SKGraphiteVkBackendContext
		{
			VkInstance = _device.Instance.Handle,
			VkPhysicalDevice = _device.PhysicalDeviceHandle,
			VkDevice = _device.Handle,
			VkQueue = _device.MainQueueHandle,
			GraphicsQueueIndex = _device.GraphicsQueueFamilyIndex,
			MaxApiVersion = (1u << 22) | (3u << 12), // VK_API_VERSION_1_3
			GetProcedureAddress = GetProcAddressWrapper,
		};

		_graphiteContext = SKGraphiteContext.CreateVulkan(bc)
			?? throw new VulkanException("Unable to create SKGraphiteContext from Vulkan device");

		// Without an ImageProvider attached to the recorder, Graphite drops every
		// draw whose source SkImage isn't already Graphite-backed (Skia's "Couldn't
		// convert SkImage to a Graphite-backed representation" warning) and the
		// internal recorder state degrades over time, eventually crashing. The
		// Default provider does an unconditional makeTextureImage upload per draw —
		// no cache. Adequate for getting a working baseline; a production caller
		// should subclass SKGraphiteImageProvider and add LRU caching keyed on
		// SkImage.UniqueId.
		// One recorder lives for the lifetime of the context. Drawing commands
		// recorded on any Graphite-backed surface vended from this recorder are
		// flushed to the GPU via Snap+InsertRecording+Submit each frame.
		_recorder = _graphiteContext.CreateRecorder(-1, SKGraphiteImageProvider.Default)
			?? throw new VulkanException("Unable to create SKGraphiteRecorder");
	}

	/// <summary>
	/// Full resize: destroys and recreates the display, swapchain, and render image.
	/// Use for major lifecycle events (window recreation, reinitialization).
	/// </summary>
	public void Resize(int width, int height)
	{
		if (_display == null || _device == null || _factory == null)
			return;

		using (_device.Lock())
		{
			_deviceApi!.DeviceWaitIdle(DeviceHandle);

			// Dispose cached Skia resources first (they reference the render image)
			DisposeCachedSkiaSurface();

			_renderImage?.Dispose();
			_display.Dispose();

			var platformSurface = new DirectVulkanSurface(_nativeWindowHandle, new SKSizeI(width, height), _factory);
			_display = VulkanDisplay.CreateDisplay(this, platformSurface);
			_renderImage = new VulkanImage(this, _display.CommandBufferPool,
				_display.SurfaceFormat.format, new SKSizeI(width, height));
			// _cachedSkSurface will be lazily recreated on next EnsureCachedSurface
		}
	}

	/// <summary>
	/// Lightweight resize: only recreates the intermediate render image and cached SKSurface.
	/// The swapchain handles its own resize via VK_ERROR_OUT_OF_DATE_KHR during presentation.
	/// Use for window resize events where only the render target dimensions change.
	/// Must be called while holding the device lock.
	/// </summary>
	public void ResizeRenderImage(int width, int height)
	{
		if (_display == null || _device == null)
			return;

		_deviceApi!.DeviceWaitIdle(DeviceHandle);
		DisposeCachedSkiaSurface();

		_renderImage?.Dispose();
		_renderImage = new VulkanImage(this, _display.CommandBufferPool,
			_display.SurfaceFormat.format, new SKSizeI(width, height));
		// _cachedSkSurface will be lazily recreated on next EnsureCachedSurface
	}

	/// <summary>
	/// Render a complete frame: acquire lock, create SKSurface, invoke the render callback,
	/// flush, blit to swapchain, and present — all within a single device lock scope.
	/// The callback receives the SKSurface to render into.
	/// </summary>
	public bool RenderFrame(Action<SKSurface> renderCallback)
	{
		if (_display == null || _graphiteContext == null || _recorder == null || _renderImage == null || _device == null)
			return false;

		using (_device.Lock())
		{
			try
			{
				_display.EnsureSwapchainAvailable();

				// Lazily create or reuse the cached SKSurface wrapping the intermediate VkImage
				EnsureCachedSkiaSurface();

				if (_cachedSkSurface == null)
					return false;

				// Invoke the Uno composition rendering callback
				renderCallback(_cachedSkSurface);

				// Snap + insert + submit: this is the Graphite equivalent of the
				// Ganesh canvas/context Flush pair. Snap converts queued draws into
				// a Recording, InsertRecording schedules it, Submit drives the GPU.
				FlushPendingGraphiteWork();

				// StartPresentation acquires next swapchain image and begins a command buffer
				var commandBuffer = _display.StartPresentation();

				// Blit intermediate render image to the swapchain image
				_display.BlitImageToCurrentImage(commandBuffer, _renderImage);

				// End presentation: transition to present layout, submit, and present
				_display.EndPresentation(commandBuffer);

				return true;
			}
			catch (VulkanException ex) when (
				ex.Message.Contains("OUT_OF_DATE") ||
				ex.Message.Contains("SUBOPTIMAL"))
			{
				return false;
			}
		}
	}

	/// <summary>
	/// Flush any pending Graphite draws to the Vulkan intermediate image.
	/// Equivalent to the old <c>SKCanvas.Flush()</c> + <c>GRContext.Flush()</c>
	/// pair used on the Ganesh path. Safe to call when nothing was recorded —
	/// <see cref="SKGraphiteRecorder.Snap"/> returns null in that case.
	/// </summary>
	private void FlushPendingGraphiteWork()
	{
		using var recording = _recorder!.Snap();
		if (recording != null)
		{
			_graphiteContext!.InsertRecording(recording);
		}
		_graphiteContext!.Submit();
	}

	private void EnsureCachedSkiaSurface()
	{
		if (_cachedSkSurface != null)
			return;

		var imageInfo = _renderImage!.ImageInfo;

		// Build the Graphite-typed view of the existing VkImage. The flags here
		// mirror the VkImage's actual creation parameters in VulkanImageBase
		// (color attachment + transfer src/dst + sampled + input attachment, all
		// required by Skia's VulkanCaps for renderable formats).
		var vkti = new SKGraphiteVkTextureInfo
		{
			SampleCount = (int)imageInfo.SampleCount,
			Mipmapped = imageInfo.LevelCount > 1 ? 1 : 0,
			Flags = 0,
			Format = (int)imageInfo.Format,
			ImageTiling = (int)imageInfo.Tiling,
			ImageUsageFlags = imageInfo.UsageFlags,
			SharingMode = 0, // VK_SHARING_MODE_EXCLUSIVE
			AspectMask = 0x1, // VK_IMAGE_ASPECT_COLOR_BIT
		};

		_cachedBackendTexture = SKGraphiteBackendTexture.CreateVulkan(
			imageInfo.PixelSize.Width,
			imageInfo.PixelSize.Height,
			vkti,
			(int)imageInfo.Layout,
			_device!.GraphicsQueueFamilyIndex,
			(IntPtr)(long)imageInfo.Handle)
			?? throw new VulkanException("Unable to create SKGraphiteBackendTexture from VkImage");

		var colorType = OperatingSystem.IsAndroid() ? SKColorType.Rgba8888 : SKColorType.Bgra8888;
		_cachedSkSurface = SKSurface.Create(_recorder!, _cachedBackendTexture, colorType, SKColorSpace.CreateSrgb())
			?? throw new VulkanException("Unable to create SKSurface from Graphite BackendTexture");
	}

	private void DisposeCachedSkiaSurface()
	{
		if (_device != null)
		{
			// Wait for GPU to finish all pending work before disposing Skia resources
			_deviceApi?.DeviceWaitIdle(DeviceHandle);
		}
		_cachedSkSurface?.Dispose();
		_cachedSkSurface = null;
		_cachedBackendTexture?.Dispose();
		_cachedBackendTexture = null;
	}

	/// <summary>
	/// Invalidate the cached surface reference without disposing it.
	/// Call when the caller has already disposed the SKSurface externally
	/// (e.g., X11Renderer base class disposes _surface before calling UpdateSize).
	/// </summary>
	public void InvalidateCachedSurface()
	{
		_cachedSkSurface = null;
		_cachedBackendTexture?.Dispose();
		_cachedBackendTexture = null;
	}

	/// <summary>
	/// Ensure the cached Skia surface is created. Call while holding the device lock.
	/// Used by platforms (Win32) that use a split StartPaint/EndPaint pattern.
	/// </summary>
	public void EnsureCachedSurface()
	{
		EnsureCachedSkiaSurface();
	}

	/// <summary>
	/// Blit the intermediate render image to the swapchain and present.
	/// Call after Skia canvas/context flush, while holding the device lock.
	/// Used by platforms (Win32) that use a split StartPaint/EndPaint pattern.
	/// </summary>
	public void BlitAndPresent()
	{
		if (_display == null || _renderImage == null || _deviceApi == null)
			return;

		// Flush any Graphite work recorded since the last present so the
		// VkImage's contents reflect the just-rendered frame before we blit.
		if (_graphiteContext != null && _recorder != null)
		{
			FlushPendingGraphiteWork();
		}

		// Try to acquire next swapchain image without retrying.
		var acquireResult = _display.TryAcquireNextImage();
		if (acquireResult != 0) // VK_SUCCESS = 0
		{
			// Swapchain out of date — recreate it now.
			// This is safe because we're inside the device lock and not
			// mid-presentation. The previous segfaults were caused by
			// StartPresentation's retry loop calling RecreateSwapchain
			// while already holding partial presentation state.
			try
			{
				_deviceApi.DeviceWaitIdle(DeviceHandle);
				_display.RecreateSwapchainSafe();

				// Retry acquire after recreation
				acquireResult = _display.TryAcquireNextImage();
				if (acquireResult != 0)
					return; // Still failing — skip this frame
			}
			catch
			{
				return; // Recreation failed — skip this frame
			}
		}

		try
		{
			var commandBuffer = _display.CommandBufferPool.CreateCommandBuffer();
			commandBuffer.BeginRecording();
			_display.PrepareCurrentImageForBlit(commandBuffer);
			_display.BlitImageToCurrentImage(commandBuffer, _renderImage);
			_display.EndPresentation(commandBuffer);
		}
		catch (VulkanException)
		{
			// Presentation failed — skip this frame
		}
	}

	/// <summary>
	/// Get information about the initialized Vulkan device for diagnostic logging.
	/// </summary>
	public unsafe (string DeviceName, string DriverVersion) GetDeviceInfo()
	{
		if (_device == null || _instanceApi == null)
			return ("Unknown", "Unknown");

		_instanceApi.GetPhysicalDeviceProperties(PhysicalDeviceHandle, out var properties);

		var deviceName = Marshal.PtrToStringAnsi(new IntPtr(properties.deviceName)) ?? "Unknown";
		var major = (properties.driverVersion >> 22) & 0x3FF;
		var minor = (properties.driverVersion >> 12) & 0x3FF;
		var patch = properties.driverVersion & 0xFFF;
		var driverVersion = $"{major}.{minor}.{patch}";

		return (deviceName, driverVersion);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		if (_device != null)
		{
			using (_device.Lock())
			{
				_deviceApi?.DeviceWaitIdle(DeviceHandle);
				DisposeCachedSkiaSurface();
			}
		}

		// Graphite has no AbandonContext analogue — disposing the context
		// tears down the Skia-side resources cleanly. The recorder must be
		// disposed before the context (recorders are vended by the context).
		_recorder?.Dispose();
		_recorder = null;
		_graphiteContext?.Dispose();
		_graphiteContext = null;

		_renderImage?.Dispose();
		_renderImage = null;

		_display?.Dispose();
		_display = null;

		_device?.Dispose();
		_device = null;

		(_instance as IDisposable)?.Dispose();
		_instance = null;
	}

	/// <summary>
	/// Simple IVulkanKhrSurfacePlatformSurface implementation that wraps a native window handle directly.
	/// The surface was already created during Initialize — this provides the CreateSurface callback for
	/// VulkanDisplay/VulkanKhrSurface to use.
	/// </summary>
	private sealed class DirectVulkanSurface : IVulkanKhrSurfacePlatformSurface
	{
		private readonly IntPtr _nativeWindowHandle;
		private readonly IVulkanPlatformSurfaceFactory _factory;

		public DirectVulkanSurface(IntPtr nativeWindowHandle, SKSizeI size, IVulkanPlatformSurfaceFactory factory)
		{
			_nativeWindowHandle = nativeWindowHandle;
			_factory = factory;
			Size = size;
		}

		public SKSizeI Size { get; }

		public ulong CreateSurface(IVulkanPlatformGraphicsContext context)
		{
			// Create a new VkSurfaceKHR from the native window handle
			return _factory.CreateSurface((VulkanInstance)context.Instance, _nativeWindowHandle);
		}

		public void Dispose() { }
	}
}

// Extension to convert IntPtr to Vulkan handle structs for API calls
internal static class VulkanHandleExtensions
{
	public static VkDevice ToVkDevice(this IntPtr handle) => new VkDevice { Handle = handle };
	public static VkInstance ToVkInstance(this IntPtr handle) => new VkInstance { Handle = handle };
	public static VkPhysicalDevice ToVkPhysicalDevice(this IntPtr handle) => new VkPhysicalDevice { Handle = handle };
	public static VkQueue ToVkQueue(this IntPtr handle) => new VkQueue { Handle = handle };
}
