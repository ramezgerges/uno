using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Extensibility;
using Uno.Foundation.Logging;
using Uno.Helpers;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia;
using Uno.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

public partial class WaylandApplicationHost : SkiaHost, ISkiaApplicationHost, IDisposable
{
	[ThreadStatic] private static bool _isDispatcherThread;
	private readonly EventLoop _eventLoop;
	private readonly Func<Application> _appBuilder;

	static WaylandApplicationHost()
	{
		ApiExtensibility.Register(typeof(Uno.ApplicationModel.Core.ICoreApplicationExtension), _ => new WaylandCoreApplicationExtension());
		ApiExtensibility.Register(typeof(Windows.UI.ViewManagement.IApplicationViewExtension), o => new WaylandApplicationViewExtension(o));
		ApiExtensibility.Register(typeof(Windows.Graphics.Display.IDisplayInformationExtension), o => new WaylandDisplayInformationExtension(o));
		ApiExtensibility.Register(typeof(INativeWindowFactoryExtension), _ => new WaylandNativeWindowFactoryExtension());

		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoCorePointerInputSource), o => new WaylandPointerInputSource(o));
		ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoKeyboardInputSource), o => new WaylandKeyboardInputSource(o));

		ApiExtensibility.Register<XamlRoot>(typeof(Uno.Graphics.INativeOpenGLWrapper), xamlRoot => new WaylandNativeOpenGLWrapper(xamlRoot));

		ApiExtensibility.Register(typeof(IClipboardExtension), _ => WaylandClipboardExtension.Instance);

		ApiExtensibility.Register<DragDropManager>(typeof(Windows.ApplicationModel.DataTransfer.DragDrop.Core.IDragDropExtension), o => new WaylandDragDropExtension(o));

		CompositionTarget.FrameRenderingOptions = (true, true);
	}

	public WaylandApplicationHost(Func<Application> appBuilder, int renderFrameRate = 60, bool useSystemHarfBuzz = false)
	{
		_appBuilder = appBuilder;

		if (RenderFrameRate != default && renderFrameRate != RenderFrameRate)
		{
			throw new InvalidOperationException("Wayland's render frame rate should only be set once.");
		}
		RenderFrameRate = renderFrameRate;

		_eventLoop = new EventLoop();
		_eventLoop.Schedule(() => { Thread.CurrentThread.Name = "Uno Event Loop"; });

		_eventLoop.Schedule(() =>
		{
			_isDispatcherThread = true;
		});
		CoreDispatcher.DispatchOverride = (a, p) => _eventLoop.Schedule(a);
		CoreDispatcher.HasThreadAccessOverride = () => _isDispatcherThread;
	}

	internal static int RenderFrameRate { get; private set; }

	protected override Task RunLoop()
	{
		Thread.CurrentThread.Name = "Main Thread (keep-alive)";
		_eventLoop.Schedule(StartApp);

		while (!WaylandXamlRootHost.AllWindowsDone())
		{
			Thread.Sleep(100);
		}

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"{nameof(WaylandApplicationHost)} is exiting");
		}

		return Task.CompletedTask;
	}

	private void StartApp()
	{
		void CreateApp(ApplicationInitializationCallbackParams _)
		{
			var app = _appBuilder();
			app.Host = this;
		}

		Application.Start(CreateApp);
	}

	protected override void Initialize()
	{
	}

	public void Dispose()
	{
	}
}
