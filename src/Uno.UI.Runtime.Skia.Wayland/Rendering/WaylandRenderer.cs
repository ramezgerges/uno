using System;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal abstract class WaylandRenderer : IDisposable
{
	private int _renderCount;
	private SKColor _background = SKColors.White;
	private SKSurface? _surface;
	protected readonly IXamlRootHost _host;

	protected WaylandRenderer(IXamlRootHost host)
	{
		_host = host;
	}

	public void SetBackgroundColor(SKColor color) => _background = color;

	public void Render()
	{
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Render {_renderCount++}");
		}

		if (_host is WaylandXamlRootHost { Closed.IsCompleted: true })
		{
			return;
		}

		MakeCurrent();

		_surface?.Canvas.Clear(_background);
		_ = ((CompositionTarget)_host.RootElement!.Visual.CompositionTarget!).OnNativePlatformFrameRequested(_surface?.Canvas, size =>
		{
			_surface?.Dispose();
			_surface = UpdateSize((int)size.Width, (int)size.Height);
			_surface.Canvas.Clear(_background);
			return _surface.Canvas;
		});

		Flush();
	}

	protected abstract SKSurface UpdateSize(int width, int height);
	protected virtual void MakeCurrent() { }
	protected abstract void Flush();
	public abstract void Dispose();
}
