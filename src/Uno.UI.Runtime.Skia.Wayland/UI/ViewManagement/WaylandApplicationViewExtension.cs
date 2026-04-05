using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandApplicationViewExtension(object owner) : IApplicationViewExtension
{
	private readonly ApplicationView _owner = (ApplicationView)owner;

	public bool TryResizeView(Size size)
	{
		// Wayland does not allow client-initiated window resizing through ApplicationView
		return false;
	}
}
