using System;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private DataPackage? _currentContent;

	public event EventHandler<object>? ContentChanged;

	public void StartContentChanged() { }
	public void StopContentChanged() { }

	public void Clear()
	{
		_currentContent = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		return _currentContent?.GetView();
	}

	public void SetContent(DataPackage? content)
	{
		_currentContent = content;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
