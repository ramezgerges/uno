using System;
using System.Diagnostics;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Helpers;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.X11;

internal abstract class X11Renderer : IDisposable
{
	private int _renderCount;
	private SKColor _background = SKColors.White;
	private SKSurface? _surface;
	private X11AirspaceRenderHelper? _airspaceHelper;
	protected readonly IXamlRootHost _host;
	protected readonly X11Window _x11Window;

	protected X11Renderer(IXamlRootHost host, X11Window x11Window)
	{
		_host = host;
		_x11Window = x11Window;
	}

	public void SetBackgroundColor(SKColor color) => _background = color;

	public virtual void Render()
	{
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Render {_renderCount++}");
		}

		var display = _x11Window.Display;
		var window = _x11Window.Window;

		if (_host is X11XamlRootHost { Closed.IsCompleted: true })
		{
			return;
		}

		using (X11Helper.XLock(display))
		{
			MakeCurrent();
		}

		bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
		long t0 = perf ? Stopwatch.GetTimestamp() : 0;

		_surface?.Canvas.Clear(_background);
		var nativeElementClipPath = ((CompositionTarget)_host.RootElement!.Visual.CompositionTarget!).OnNativePlatformFrameRequested(_surface?.Canvas, size =>
		{
			_surface?.Dispose();
			using (X11Helper.XLock(display))
			{
				_surface = UpdateSize((int)size.Width, (int)size.Height);
			}
			_surface.Canvas.Clear(_background);
			_airspaceHelper?.Dispose();
			_airspaceHelper = new X11AirspaceRenderHelper(display, window, (int)size.Width, (int)size.Height);
			return _surface.Canvas;
		});

		long t1 = perf ? Stopwatch.GetTimestamp() : 0;

		_airspaceHelper?.XShapeClip(nativeElementClipPath);

		using (X11Helper.XLock(display))
		{
			Flush();
			_ = XLib.XFlush(display);
		}

		if (perf)
		{
			long t2 = Stopwatch.GetTimestamp();
			double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
			var sz = _surface is { } s ? $"{s.Canvas.DeviceClipBounds.Width}x{s.Canvas.DeviceClipBounds.Height}" : "?";
			this.Log().Info($"[PERF] skia {sz} draw={Ms(t0, t1):F2} flush+present={Ms(t1, t2):F2} total={Ms(t0, t2):F2} ms");
		}
	}

	protected abstract SKSurface UpdateSize(int width, int height);
	protected virtual void MakeCurrent() { }
	protected abstract void Flush();
	public abstract void Dispose();
}
