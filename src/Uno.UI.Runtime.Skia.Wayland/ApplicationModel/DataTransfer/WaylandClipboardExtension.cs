using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using libuno-clipboard.so — a native C helper compiled from
/// uno-clipboard.c that uses wayland-scanner generated protocol code.
/// Each operation opens its own Wayland connection for thread safety.
/// Copy runs on a background thread to serve send requests.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private const string Lib = "libuno-clipboard";

	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_text();
	[DllImport(Lib)] private static extern int uno_clipboard_set_text([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
	[DllImport(Lib)] private static extern void uno_clipboard_free(IntPtr ptr);

	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private string? _lastCopiedText;
	private Thread? _copyThread;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	public void Clear()
	{
		_lastCopiedText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		try
		{
			var ptr = uno_clipboard_get_text();
			if (ptr != IntPtr.Zero)
			{
				var text = Marshal.PtrToStringUTF8(ptr);
				uno_clipboard_free(ptr);
				if (!string.IsNullOrEmpty(text))
				{
					var p = new DataPackage();
					p.SetText(text);
					return p.GetView();
				}
			}
		}
		catch { }

		// Fallback to in-process
		if (_lastCopiedText != null)
		{
			var p = new DataPackage();
			p.SetText(_lastCopiedText);
			return p.GetView();
		}
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null) { _lastCopiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }

		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (text != null)
				{
					_lastCopiedText = text;
					// Run set_text on a background thread since it dispatches events
					// to serve paste requests from other apps
					_copyThread = new Thread(() =>
					{
						try { _ = uno_clipboard_set_text(text); } catch { }
					})
					{ IsBackground = true, Name = "WaylandClipboardCopy" };
					_copyThread.Start();
				}
			}
		}
		catch { }

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
