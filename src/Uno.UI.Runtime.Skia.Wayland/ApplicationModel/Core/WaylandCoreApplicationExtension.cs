using Uno.ApplicationModel.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandCoreApplicationExtension : ICoreApplicationExtension
{
	public bool CanExit => true;

	public void Exit()
	{
		WaylandXamlRootHost.CloseAllWindows();
	}
}
