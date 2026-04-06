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

		var rootElement = _host.RootElement;
		if (rootElement?.Visual?.CompositionTarget is not CompositionTarget compositionTarget)
		{
			return; // UI not ready yet
		}

		MakeCurrent();

		_surface?.Canvas.Clear(_background);
		_ = compositionTarget.OnNativePlatformFrameRequested(_surface?.Canvas, size =>
		{
			_surface?.Dispose();
			_surface = UpdateSize((int)size.Width, (int)size.Height);
			if (_surface == null)
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"UpdateSize returned null surface for {size.Width}x{size.Height}");
				}
				return null!;
			}
			_surface.Canvas.Clear(_background);
			return _surface.Canvas;
		});

		if (_surface != null)
		{
			Flush();
		}
	}

	protected abstract SKSurface UpdateSize(int width, int height);
	protected virtual void MakeCurrent() { }
	protected abstract void Flush();
	public abstract void Dispose();
}
