using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static partial class EglBindings
{
	private const string LibEGL = "libEGL.so.1";
	private const string LibWaylandEgl = "libwayland-egl.so.1";

	// EGL constants
	internal const int EGL_NONE = 0x3038;
	internal const int EGL_SURFACE_TYPE = 0x3033;
	internal const int EGL_WINDOW_BIT = 0x0004;
	internal const int EGL_RED_SIZE = 0x3024;
	internal const int EGL_GREEN_SIZE = 0x3025;
	internal const int EGL_BLUE_SIZE = 0x3026;
	internal const int EGL_ALPHA_SIZE = 0x3027;
	internal const int EGL_DEPTH_SIZE = 0x3025;
	internal const int EGL_STENCIL_SIZE = 0x3026;
	internal const int EGL_RENDERABLE_TYPE = 0x3040;
	internal const int EGL_OPENGL_ES2_BIT = 0x0004;
	internal const int EGL_OPENGL_ES3_BIT = 0x0040;
	internal const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
	internal const int EGL_NO_CONTEXT = 0;
	internal const int EGL_NO_DISPLAY = 0;
	internal const int EGL_NO_SURFACE = 0;
	internal const int EGL_DEFAULT_DISPLAY = 0;
	internal const int EGL_PLATFORM_WAYLAND_KHR = 0x31D8;
	internal const int EGL_TRUE = 1;
	internal const int EGL_FALSE = 0;

	// EGL functions
	[LibraryImport(LibEGL, EntryPoint = "eglGetDisplay")]
	internal static partial IntPtr eglGetDisplay(IntPtr nativeDisplay);

	[LibraryImport(LibEGL, EntryPoint = "eglGetPlatformDisplay")]
	internal static partial IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr attribList);

	[LibraryImport(LibEGL, EntryPoint = "eglGetPlatformDisplayEXT")]
	internal static partial IntPtr eglGetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, IntPtr attribList);

	[LibraryImport(LibEGL, EntryPoint = "eglInitialize")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglInitialize(IntPtr display, out int major, out int minor);

	[LibraryImport(LibEGL, EntryPoint = "eglChooseConfig")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglChooseConfig(IntPtr display, int[] attribs, IntPtr[] configs, int configSize, out int numConfig);

	[LibraryImport(LibEGL, EntryPoint = "eglCreateContext")]
	internal static partial IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attribs);

	[LibraryImport(LibEGL, EntryPoint = "eglCreateWindowSurface")]
	internal static partial IntPtr eglCreateWindowSurface(IntPtr display, IntPtr config, IntPtr nativeWindow, int[]? attribs);

	[LibraryImport(LibEGL, EntryPoint = "eglMakeCurrent")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

	[LibraryImport(LibEGL, EntryPoint = "eglSwapBuffers")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglSwapBuffers(IntPtr display, IntPtr surface);

	[LibraryImport(LibEGL, EntryPoint = "eglSwapInterval")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglSwapInterval(IntPtr display, int interval);

	[LibraryImport(LibEGL, EntryPoint = "eglDestroySurface")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglDestroySurface(IntPtr display, IntPtr surface);

	[LibraryImport(LibEGL, EntryPoint = "eglDestroyContext")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglDestroyContext(IntPtr display, IntPtr context);

	[LibraryImport(LibEGL, EntryPoint = "eglTerminate")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglTerminate(IntPtr display);

	[LibraryImport(LibEGL, EntryPoint = "eglGetError")]
	internal static partial int eglGetError();

	[LibraryImport(LibEGL, EntryPoint = "eglBindAPI")]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool eglBindAPI(int api);

	internal const int EGL_OPENGL_ES_API = 0x30A0;
	internal const int EGL_DRAW = 0x3059;
	internal const int EGL_READ = 0x305A;

	[LibraryImport(LibEGL, EntryPoint = "eglGetCurrentContext")]
	internal static partial IntPtr eglGetCurrentContext();

	[LibraryImport(LibEGL, EntryPoint = "eglGetCurrentSurface")]
	internal static partial IntPtr eglGetCurrentSurface(int readdraw);

	[LibraryImport(LibEGL, EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr eglGetProcAddress(string procname);

	[LibraryImport(LibEGL, EntryPoint = "eglCreatePbufferSurface")]
	internal static partial IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attribs);

	// wayland-egl
	[LibraryImport(LibWaylandEgl, EntryPoint = "wl_egl_window_create")]
	internal static partial IntPtr wl_egl_window_create(IntPtr surface, int width, int height);

	[LibraryImport(LibWaylandEgl, EntryPoint = "wl_egl_window_destroy")]
	internal static partial void wl_egl_window_destroy(IntPtr window);

	[LibraryImport(LibWaylandEgl, EntryPoint = "wl_egl_window_resize")]
	internal static partial void wl_egl_window_resize(IntPtr window, int width, int height, int dx, int dy);
}
