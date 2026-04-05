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
	private string _title = "";

	internal WaylandWindowWrapper(Window window, XamlRoot xamlRoot)
		: base(window, xamlRoot)
	{
		_host = new WaylandXamlRootHost(this, window, xamlRoot);
	}

	public override string Title
	{
		get => _title;
		set => _title = value;
	}

	public override object NativeWindow => new WaylandNativeWindow();

	protected override void ShowCore()
	{
		// Will be implemented when rendering is wired up
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
		// Will be implemented with rendering
	}

	public override void ExtendContentIntoTitleBar(bool extend)
	{
		// Will be implemented with decorations
	}

	protected override IDisposable ApplyFullScreenPresenter()
	{
		// Will be implemented with xdg_toplevel.set_fullscreen
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
