#pragma warning disable CA1806 // Do not ignore method results

using System;
using System.Diagnostics;
using SkiaSharp;
using Uno.Disposables;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandEGLRenderer : WaylandRenderer, IDisposable
{
	private const uint DefaultFramebuffer = 0;

	private readonly IntPtr _wlDisplay;
	private readonly IntPtr _wlSurface;
	private readonly IntPtr _eglDisplay;
	private readonly IntPtr _eglContext;
	private readonly IntPtr _eglConfig;
	private readonly GRGlInterface _glInterface;
	private readonly GRContext _grContext;

	private IntPtr _eglSurface;
	private IntPtr _wlEglWindow;
	private GRBackendRenderTarget? _renderTarget;
	private IDisposable? _contextCurrentDisposable;

	public WaylandEGLRenderer(IXamlRootHost host, IntPtr wlDisplay, IntPtr wlSurface, int width, int height)
		: base(host)
	{
		_wlDisplay = wlDisplay;
		_wlSurface = wlSurface;

		// Bind OpenGL ES API
		if (!EglBindings.eglBindAPI(EglBindings.EGL_OPENGL_ES_API))
		{
			throw new InvalidOperationException($"eglBindAPI failed: {EglBindings.eglGetError()}");
		}

		// Get EGL display — try multiple approaches for compatibility.
		// The order matters: some MESA versions only work with specific approaches.
		_eglDisplay = IntPtr.Zero;
		var eglError = 0;

		// Try each approach, init, and test if configs are available
		foreach (var (label, getDisplay) in new (string, Func<IntPtr>)[]
		{
			("eglGetPlatformDisplay(WAYLAND)", () => EglBindings.eglGetPlatformDisplay(EglBindings.EGL_PLATFORM_WAYLAND_KHR, wlDisplay, IntPtr.Zero)),
			("eglGetPlatformDisplayEXT(WAYLAND)", () => EglBindings.eglGetPlatformDisplayEXT(EglBindings.EGL_PLATFORM_WAYLAND_KHR, wlDisplay, IntPtr.Zero)),
			("eglGetDisplay(wlDisplay)", () => EglBindings.eglGetDisplay(wlDisplay)),
			("eglGetDisplay(DEFAULT)", () => EglBindings.eglGetDisplay(IntPtr.Zero)),
		})
		{
			var display = getDisplay();
			if (display == IntPtr.Zero)
			{
				continue;
			}

			if (!EglBindings.eglInitialize(display, out var maj, out var min))
			{
				continue;
			}

			// Test if this display actually has usable configs
			var testAttribs = new[] { EglBindings.EGL_RENDERABLE_TYPE, EglBindings.EGL_OPENGL_ES2_BIT, EglBindings.EGL_NONE };
			var testConfigs = new IntPtr[1];
			if (EglBindings.eglChooseConfig(display, testAttribs, testConfigs, 1, out var testNum) && testNum > 0)
			{
				_eglDisplay = display;
				this.LogInfo()?.Info($"EGL display via {label}, version {maj}.{min}, {testNum} config(s).");
				break;
			}

			// This display has no usable configs, terminate and try next
			EglBindings.eglTerminate(display);
		}

		if (_eglDisplay == IntPtr.Zero)
		{
			eglError = EglBindings.eglGetError();
			throw new InvalidOperationException($"No usable EGL display found (last error: {eglError})");
		}

		// Choose EGL config
		int[] configAttribs =
		{
			EglBindings.EGL_SURFACE_TYPE, EglBindings.EGL_WINDOW_BIT,
			EglBindings.EGL_RED_SIZE, 8,
			EglBindings.EGL_GREEN_SIZE, 8,
			EglBindings.EGL_BLUE_SIZE, 8,
			EglBindings.EGL_ALPHA_SIZE, 8,
			EglBindings.EGL_RENDERABLE_TYPE, EglBindings.EGL_OPENGL_ES2_BIT,
			EglBindings.EGL_NONE
		};

		var configs = new IntPtr[1];
		if (!EglBindings.eglChooseConfig(_eglDisplay, configAttribs, configs, 1, out var numConfig) || numConfig < 1)
		{
			throw new InvalidOperationException($"eglChooseConfig failed: {EglBindings.eglGetError()}");
		}

		_eglConfig = configs[0];

		// Create EGL context
		int[] contextAttribs =
		{
			EglBindings.EGL_CONTEXT_CLIENT_VERSION, 2,
			EglBindings.EGL_NONE
		};

		_eglContext = EglBindings.eglCreateContext(_eglDisplay, _eglConfig, IntPtr.Zero, contextAttribs);
		if (_eglContext == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreateContext failed: {EglBindings.eglGetError()}");
		}

		// Create Wayland EGL window
		_wlEglWindow = EglBindings.wl_egl_window_create(wlSurface, width, height);
		if (_wlEglWindow == IntPtr.Zero)
		{
			throw new InvalidOperationException("wl_egl_window_create failed.");
		}

		// Create EGL window surface
		_eglSurface = EglBindings.eglCreateWindowSurface(_eglDisplay, _eglConfig, _wlEglWindow, null);
		if (_eglSurface == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreateWindowSurface failed: {EglBindings.eglGetError()}");
		}

		// Make context current to create GRContext
		MakeCurrent();

		// Create SkiaSharp GL context
		_glInterface = GRGlInterface.CreateGles(EglBindings.eglGetProcAddress);
		if (_glInterface == null)
		{
			throw new NotSupportedException("OpenGL ES is not supported in this system.");
		}

		_grContext = GRContext.CreateGl(_glInterface);
		if (_grContext == null)
		{
			throw new NotSupportedException("OpenGL ES is not supported in this system (failed to create GRContext).");
		}

		_contextCurrentDisposable!.Dispose();
	}

	protected override SKSurface UpdateSize(int width, int height)
	{
		_renderTarget?.Dispose();

		// Resize the Wayland EGL window
		EglBindings.wl_egl_window_resize(_wlEglWindow, width, height, 0, 0);

		var skColorType = SKColorType.Rgba8888;
		var grSurfaceOrigin = GRSurfaceOrigin.BottomLeft;
		var glInfo = new GRGlFramebufferInfo(DefaultFramebuffer, skColorType.ToGlSizedFormat());

		_renderTarget = new GRBackendRenderTarget(width, height, 0, 8, glInfo);
		return SKSurface.Create(_grContext, _renderTarget, grSurfaceOrigin, skColorType);
	}

	protected override void MakeCurrent()
	{
		var previousContext = EglBindings.eglGetCurrentContext();
		var previousReadSurface = EglBindings.eglGetCurrentSurface(EglBindings.EGL_READ);
		var previousDrawSurface = EglBindings.eglGetCurrentSurface(EglBindings.EGL_DRAW);

		if (!EglBindings.eglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext))
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"eglMakeCurrent failed: {EglBindings.eglGetError()}");
			}
		}

		_contextCurrentDisposable = Disposable.Create(() =>
		{
			if (!EglBindings.eglMakeCurrent(_eglDisplay, previousDrawSurface, previousReadSurface, previousContext))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"eglMakeCurrent (restore) failed: {EglBindings.eglGetError()}");
				}
			}
		});
	}

	protected override void Flush()
	{
		if (!EglBindings.eglSwapBuffers(_eglDisplay, _eglSurface))
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"eglSwapBuffers failed: {EglBindings.eglGetError()}");
			}
		}

		Debug.Assert(_contextCurrentDisposable is not null);
		_contextCurrentDisposable?.Dispose();
		_contextCurrentDisposable = null;
	}

	public override void Dispose()
	{
		MakeCurrent();

		_renderTarget?.Dispose();
		_grContext.Dispose();
		_glInterface.Dispose();

		_ = EglBindings.eglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

		if (_eglSurface != IntPtr.Zero)
		{
			_ = EglBindings.eglDestroySurface(_eglDisplay, _eglSurface);
		}

		if (_eglContext != IntPtr.Zero)
		{
			_ = EglBindings.eglDestroyContext(_eglDisplay, _eglContext);
		}

		if (_wlEglWindow != IntPtr.Zero)
		{
			EglBindings.wl_egl_window_destroy(_wlEglWindow);
		}

		if (_eglDisplay != IntPtr.Zero)
		{
			_ = EglBindings.eglTerminate(_eglDisplay);
		}

		_contextCurrentDisposable?.Dispose();
	}
}
