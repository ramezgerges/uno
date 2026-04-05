#pragma warning disable CA1806 // Do not ignore method results

using System;
using Microsoft.UI.Xaml;
using Uno.Disposables;
using Uno.Foundation.Logging;
using Uno.Graphics;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandNativeOpenGLWrapper : INativeOpenGLWrapper
{
	private IntPtr _eglDisplay;
	private IntPtr _eglContext;
	private IntPtr _pBufferSurface;

	public WaylandNativeOpenGLWrapper(XamlRoot xamlRoot)
	{
		if (XamlRootMap.GetHostForRoot(xamlRoot) is not WaylandXamlRootHost host)
		{
			throw new InvalidOperationException(
				$"The XamlRoot and its XamlRootHost must be initialized on the element before constructing a {nameof(WaylandNativeOpenGLWrapper)}.");
		}

		_eglDisplay = host.EglDisplay;
		if (_eglDisplay == IntPtr.Zero)
		{
			throw new InvalidOperationException("EGL display is not initialized on the host.");
		}

		// Choose a config suitable for a PBuffer surface
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

		int[] contextAttribs =
		{
			EglBindings.EGL_CONTEXT_CLIENT_VERSION, 2,
			EglBindings.EGL_NONE
		};

		_eglContext = EglBindings.eglCreateContext(_eglDisplay, configs[0], IntPtr.Zero, contextAttribs);
		if (_eglContext == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreateContext failed: {EglBindings.eglGetError()}");
		}

		// Create a 1x1 PBuffer surface for offscreen context activation
		int[] pbufferAttribs =
		{
			0x3057 /* EGL_WIDTH */, 1,
			0x3056 /* EGL_HEIGHT */, 1,
			EglBindings.EGL_NONE
		};

		_pBufferSurface = EglBindings.eglCreatePbufferSurface(_eglDisplay, configs[0], pbufferAttribs);
		if (_pBufferSurface == IntPtr.Zero)
		{
			throw new InvalidOperationException($"eglCreatePbufferSurface failed: {EglBindings.eglGetError()}");
		}
	}

	public IDisposable MakeCurrent()
	{
		var previousContext = EglBindings.eglGetCurrentContext();
		var previousReadSurface = EglBindings.eglGetCurrentSurface(EglBindings.EGL_READ);
		var previousDrawSurface = EglBindings.eglGetCurrentSurface(EglBindings.EGL_DRAW);

		_ = EglBindings.eglMakeCurrent(_eglDisplay, _pBufferSurface, _pBufferSurface, _eglContext);

		return Disposable.Create(() =>
			_ = EglBindings.eglMakeCurrent(_eglDisplay, previousDrawSurface, previousReadSurface, previousContext));
	}

	public IntPtr GetProcAddress(string proc) => EglBindings.eglGetProcAddress(proc);

	public bool TryGetProcAddress(string proc, out IntPtr addr)
	{
		addr = EglBindings.eglGetProcAddress(proc);
		return addr != IntPtr.Zero;
	}

	public void Dispose()
	{
		if (_eglDisplay != IntPtr.Zero && _pBufferSurface != IntPtr.Zero)
		{
			_ = EglBindings.eglDestroySurface(_eglDisplay, _pBufferSurface);
		}

		if (_eglDisplay != IntPtr.Zero && _eglContext != IntPtr.Zero)
		{
			_ = EglBindings.eglDestroyContext(_eglDisplay, _eglContext);
		}

		_eglDisplay = default;
		_eglContext = default;
		_pBufferSurface = default;
	}
}
