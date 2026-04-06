using System;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandDragDropExtension : IDragDropExtension
{
	private readonly DragDropManager _owner;

	public WaylandDragDropExtension(DragDropManager owner)
	{
		_owner = owner;
	}

	// TODO: Implement Wayland drag-and-drop using wl_data_device protocol
	public void StartNativeDrag(CoreDragInfo info, Action<DataPackageOperation> onCompleted)
		=> onCompleted(DataPackageOperation.None);
}
