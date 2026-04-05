using System;
using Uno.WinUI.Runtime.Skia.Wayland;

namespace Uno.UI.Hosting;

public static class WaylandHostBuilderExtensions
{
	public static IUnoPlatformHostBuilder UseWayland(this IUnoPlatformHostBuilder builder)
	{
		builder.AddHostBuilder(() => new WaylandHostBuilder());
		return builder;
	}

	public static IUnoPlatformHostBuilder UseWayland(this IUnoPlatformHostBuilder builder, Action<WaylandHostBuilder> action)
	{
		builder.AddHostBuilder(() =>
		{
			var waylandBuilder = new WaylandHostBuilder();
			if (((IPlatformHostBuilder)waylandBuilder).IsSupported)
			{
				action.Invoke(waylandBuilder);
			}
			return waylandBuilder;
		});

		return builder;
	}
}
