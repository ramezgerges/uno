#pragma warning disable CA1806 // Do not ignore method results

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Uno.Disposables;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandEGLRenderer : WaylandRenderer, IDisposable
{
	private const uint DefaultFramebuffer = 0;
	private const int EGL_PLATFORM_WAYLAND_KHR = 0x31D8;

	private readonly IntPtr _wlDisplay;
	private readonly IntPtr _wlSurface;
	private readonly IntPtr _eglDisplay;
	private readonly IntPtr _eglContext;
	private readonly IntPtr _eglConfig;
	private readonly GRGlInterface _glInterface;
	private readonly GRContext _grContext;
	private readonly int _samples;
	private readonly int _stencil;

	private IntPtr _eglSurface;
	private IntPtr _wlEglWindow;
	private GRBackendRenderTarget? _renderTarget;
	private IDisposable? _contextCurrentDisposable;

	public unsafe WaylandEGLRenderer(IXamlRootHost host, IntPtr wlDisplay, IntPtr wlSurface, int width, int height)
		: base(host)
	{
		_wlDisplay = wlDisplay;
		_wlSurface = wlSurface;

		// Bind OpenGL ES API — EglHelper doesn't expose eglBindAPI, so use the local binding
		if (!EglBindings.eglBindAPI(EglBindings.EGL_OPENGL_ES_API))
		{
			throw new InvalidOperationException($"eglBindAPI failed: {Enum.GetName(EglHelper.EglGetError())}");
		}

		// Get EGL display — try multiple approaches for compatibility.
		// The order matters: some MESA versions only work with specific approaches.
		_eglDisplay = IntPtr.Zero;

		foreach (var (label, getDisplay) in new (string, Func<IntPtr>)[]
		{
			("eglGetPlatformDisplay(WAYLAND)", () => EglHelper.EglGetPlatformDisplay(EGL_PLATFORM_WAYLAND_KHR, wlDisplay, null)),
			("eglGetPlatformDisplayEXT(WAYLAND)", () => EglHelper.EglGetPlatformDisplayEXT(EGL_PLATFORM_WAYLAND_KHR, wlDisplay, null)),
			("eglGetDisplay(wlDisplay)", () => EglHelper.EglGetDisplay(wlDisplay)),
			("eglGetDisplay(DEFAULT)", () => EglHelper.EglGetDisplay(IntPtr.Zero)),
		})
		{
			var display = getDisplay();
			if (display == IntPtr.Zero)
			{
				continue;
			}

			if (!EglHelper.EglInitialize(display, out var maj, out var min))
			{
				continue;
			}

			_eglDisplay = display;
			this.LogInfo()?.Info($"EGL display via {label}, version {maj}.{min}.");
			break;
		}

		if (_eglDisplay == IntPtr.Zero)
		{
			throw new InvalidOperationException($"No usable EGL display found (last error: {Enum.GetName(EglHelper.EglGetError())})");
		}

		// Choose EGL config
		int[] configAttribs =
		{
			EglHelper.EGL_RED_SIZE, 8,
			EglHelper.EGL_GREEN_SIZE, 8,
			EglHelper.EGL_BLUE_SIZE, 8,
			EglHelper.EGL_ALPHA_SIZE, 8,
			EglHelper.EGL_DEPTH_SIZE, 8,
			EglHelper.EGL_STENCIL_SIZE, 1,
			EglHelper.EGL_RENDERABLE_TYPE, EglHelper.EGL_OPENGL_ES2_BIT,
			EglHelper.EGL_NONE
		};

		var configs = new IntPtr[1];
		if (!EglHelper.EglChooseConfig(_eglDisplay, configAttribs, configs, 1, out var numConfig) || numConfig < 1)
		{
			throw new InvalidOperationException($"eglChooseConfig failed: {Enum.GetName(EglHelper.EglGetError())}");
		}

		_eglConfig = configs[0];

		if (!EglHelper.EglGetConfigAttrib(_eglDisplay, _eglConfig, EglHelper.EGL_SAMPLES, out _samples))
		{
			_samples = 0;
		}
		if (!EglHelper.EglGetConfigAttrib(_eglDisplay, _eglConfig, EglHelper.EGL_STENCIL_SIZE, out _stencil))
		{
			_stencil = 8;
		}

		// Create EGL context
		int[] contextAttribs =
		{
			EglHelper.EGL_CONTEXT_CLIENT_VERSION, 2,
			EglHelper.EGL_NONE
		};

		_eglContext = EglHelper.EglCreateContext(_eglDisplay, _eglConfig, IntPtr.Zero, contextAttribs);
		if (_eglContext == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreateContext failed: {Enum.GetName(EglHelper.EglGetError())}");
		}

		// Create Wayland EGL window
		_wlEglWindow = EglBindings.wl_egl_window_create(wlSurface, width, height);
		if (_wlEglWindow == IntPtr.Zero)
		{
			throw new InvalidOperationException("wl_egl_window_create failed.");
		}

		// Create EGL window surface using a pointer to the wl_egl_window handle
		var wlEglWindowLocal = _wlEglWindow;
		_eglSurface = EglHelper.EglCreatePlatformWindowSurface(_eglDisplay, _eglConfig, new IntPtr(&wlEglWindowLocal), [EglHelper.EGL_NONE]);
		if (_eglSurface == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreatePlatformWindowSurface failed: {Enum.GetName(EglHelper.EglGetError())}");
		}

		// Make context current to create GRContext
		MakeCurrent();

		// Create SkiaSharp GL context
		_glInterface = GRGlInterface.CreateGles(EglHelper.EglGetProcAddress);
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

		_renderTarget = new GRBackendRenderTarget(width, height, _samples, _stencil, glInfo);
		return SKSurface.Create(_grContext, _renderTarget, grSurfaceOrigin, skColorType);
	}

	protected override void MakeCurrent()
	{
		var previousContext = EglHelper.EglGetCurrentContext();
		var previousReadSurface = EglHelper.EglGetCurrentSurface(EglHelper.EGL_READ);
		var previousDrawSurface = EglHelper.EglGetCurrentSurface(EglHelper.EGL_DRAW);

		if (!EglHelper.EglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext))
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"eglMakeCurrent failed: {Enum.GetName(EglHelper.EglGetError())}");
			}
		}

		_contextCurrentDisposable = Disposable.Create(() =>
		{
			if (!EglHelper.EglMakeCurrent(_eglDisplay, previousDrawSurface, previousReadSurface, previousContext))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"eglMakeCurrent (restore) failed: {Enum.GetName(EglHelper.EglGetError())}");
				}
			}
		});
	}

	protected override void Flush()
	{
		if (!EglHelper.EglSwapBuffers(_eglDisplay, _eglSurface))
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().Error($"eglSwapBuffers failed: {Enum.GetName(EglHelper.EglGetError())}");
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

		_ = EglHelper.EglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

		if (_eglSurface != IntPtr.Zero)
		{
			_ = EglHelper.EglDestroySurface(_eglDisplay, _eglSurface);
		}

		if (_eglContext != IntPtr.Zero)
		{
			_ = EglHelper.EglDestroyContext(_eglDisplay, _eglContext);
		}

		if (_wlEglWindow != IntPtr.Zero)
		{
			EglBindings.wl_egl_window_destroy(_wlEglWindow);
		}

		if (_eglDisplay != IntPtr.Zero)
		{
			_ = EglHelper.EglTerminate(_eglDisplay);
		}

		_contextCurrentDisposable?.Dispose();
	}
}
