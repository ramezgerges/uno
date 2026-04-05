using System;
using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Uno.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandWindowWrapper : NativeWindowWrapperBase
{
	private readonly WaylandXamlRootHost _host;
	private readonly XamlRoot _xamlRoot;
	private string _title = "";

	internal WaylandWindowWrapper(Window window, XamlRoot xamlRoot)
		: base(window, xamlRoot)
	{
		_xamlRoot = xamlRoot;
		_host = new WaylandXamlRootHost(this, window, xamlRoot);

		// Report initial size to the framework
		UpdateSizeFromHost();

		RasterizationScale = (float)XamlRoot.GetDisplayInformation(_xamlRoot).RawPixelsPerViewPixel;
	}

	public override string Title
	{
		get => _title;
		set => _title = value;
	}

	public override object NativeWindow => new WaylandNativeWindow();

	protected override void ShowCore()
	{
		_host.Show();
		UpdateSizeFromHost();
	}

	protected override void CloseCore()
	{
		_host.Close();
	}

	protected internal override void Activate()
	{
		// No-op: Wayland does not allow clients to raise windows
	}

	public override void Move(PointInt32 position)
	{
		// No-op: Wayland does not allow client-initiated moves
	}

	public override void Resize(SizeInt32 size)
	{
		_host.UpdateSizeFromWrapper(size.Width, size.Height);
	}

	public override void ExtendContentIntoTitleBar(bool extend)
	{
		// Will be implemented with decorations
	}

	internal void UpdateSizeFromHost()
	{
		var w = _host.Width;
		var h = _host.Height;
		var fullSize = new SizeInt32 { Width = w, Height = h };
		SetSizes(fullSize, fullSize);

		var scale = _xamlRoot.RasterizationScale;
		if (scale <= 0)
		{
			scale = 1;
		}
		var windowSize = new Size(w / scale, h / scale);
		var bounds = new Rect(default, windowSize);
		SetBoundsAndVisibleBounds(bounds, bounds);
	}

	protected override IDisposable ApplyFullScreenPresenter()
	{
		return new FullScreenDisposable();
	}

	protected override IDisposable ApplyOverlappedPresenter(Microsoft.UI.Windowing.OverlappedPresenter presenter)
	{
		return new OverlappedDisposable();
	}

	private class FullScreenDisposable : IDisposable
	{
		public void Dispose() { }
	}

	private class OverlappedDisposable : IDisposable
	{
		public void Dispose() { }
	}
}

internal record WaylandNativeWindow;
