using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using libuno-clipboard.so — a small native helper (source in native/uno-clipboard.c)
/// that uses wayland-scanner generated protocol code. The helper creates a persistent
/// wl_data_device on the app's display and tracks selection events through the normal
/// wl_display_dispatch loop. All calls happen on the event thread.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private const string Lib = "libuno-clipboard";

	[DllImport(Lib)] private static extern int uno_clipboard_init(IntPtr wlDisplay);
	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_text();
	[DllImport(Lib)] private static extern int uno_clipboard_set_text(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint serial);
	[DllImport(Lib)] private static extern void uno_clipboard_free(IntPtr ptr);

	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private volatile string? _cachedText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopyText;
	private volatile uint _lastSerial;
	private bool _initialized;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) { return; }
		_initialized = uno_clipboard_init(wlDisplay) == 0;
	}

	internal void ProcessOnEventThread()
	{
		if (!_initialized) { return; }

		if (_pasteRequested)
		{
			_pasteRequested = false;
			var ptr = uno_clipboard_get_text();
			if (ptr != IntPtr.Zero)
			{
				_cachedText = Marshal.PtrToStringUTF8(ptr);
				uno_clipboard_free(ptr);
			}
			else
			{
				_cachedText = null;
			}
		}

		var text = Interlocked.Exchange(ref _pendingCopyText, null);
		if (text != null)
		{
			_ = uno_clipboard_set_text(text, _lastSerial);
		}
	}

	public void Clear()
	{
		_copiedText = null;
		_cachedText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) { Thread.Sleep(20); }
		var text = _cachedText ?? _copiedText;
		if (text != null)
		{
			var p = new DataPackage();
			p.SetText(text);
			return p.GetView();
		}
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null) { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }
		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (text != null) { _copiedText = text; _cachedText = null; _pendingCopyText = text; }
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
