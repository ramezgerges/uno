using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Uno.UI.Xaml.Controls;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandNativeWindowFactoryExtension : INativeWindowFactoryExtension
{
	public bool SupportsClosingCancellation => true;
	public bool SupportsMultipleWindows => true;

	public INativeWindowWrapper CreateWindow(Window window, XamlRoot xamlRoot)
		=> new WaylandWindowWrapper(window, xamlRoot);
}
