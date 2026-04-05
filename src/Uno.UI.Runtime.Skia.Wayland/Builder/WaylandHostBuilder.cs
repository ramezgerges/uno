using System;
using Microsoft.UI.Xaml;
using Uno.WinUI.Runtime.Skia.Wayland;

namespace Uno.UI.Hosting;

public partial class WaylandHostBuilder : IPlatformHostBuilder
{
	private int _renderFrameRate = 60;
	private bool _useSystemHarfBuzz;

	internal WaylandHostBuilder()
	{
	}

	public WaylandHostBuilder RenderFrameRate(int renderFrameRate)
	{
		_renderFrameRate = renderFrameRate;
		return this;
	}

	public WaylandHostBuilder UseSystemHarfBuzz(bool value)
	{
		_useSystemHarfBuzz = value;
		return this;
	}

	bool IPlatformHostBuilder.IsSupported
	{
		get
		{
			if (!OperatingSystem.IsLinux())
			{
				return false;
			}

			// Check for explicit backend override
			var backendOverride = Environment.GetEnvironmentVariable("UNO_PLATFORM_BACKEND");
			if (string.Equals(backendOverride, "x11", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (string.Equals(backendOverride, "wayland", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			// Check for WAYLAND_DISPLAY environment variable
			return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is not null;
		}
	}

	UnoPlatformHost IPlatformHostBuilder.Create(Func<Application> appBuilder, Type appType)
		=> new WaylandApplicationHost(appBuilder, _renderFrameRate, _useSystemHarfBuzz);
}
