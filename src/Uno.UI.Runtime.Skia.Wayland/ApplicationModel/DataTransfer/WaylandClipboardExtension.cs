using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using libuno-clipboard.so native helper.
///
/// Paste: uses the app's wl_display (which has keyboard focus) via
///   uno_clipboard_get_text_from_display, called on the event thread.
///   Result is cached for UI thread access.
///
/// Copy: opens its own connection via uno_clipboard_set_text on a
///   background thread to serve paste requests from other apps.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private const string Lib = "libuno-clipboard";

	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_text_from_display(IntPtr wlDisplay);
	[DllImport(Lib)] private static extern int uno_clipboard_set_text([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
	[DllImport(Lib)] private static extern void uno_clipboard_free(IntPtr ptr);

	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private volatile string? _cachedText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	/// <summary>Store the app's display pointer for paste operations.</summary>
	internal void SetDisplay(IntPtr wlDisplay) => _wlDisplay = wlDisplay;

	/// <summary>
	/// Called each iteration of the event loop. Reads clipboard if requested.
	/// Must run on the event thread (same thread as wl_display_dispatch).
	/// </summary>
	internal void ProcessOnEventThread()
	{
		if (!_pasteRequested || _wlDisplay == IntPtr.Zero) { return; }
		_pasteRequested = false;

		var ptr = uno_clipboard_get_text_from_display(_wlDisplay);
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

	public void Clear()
	{
		_copiedText = null;
		_cachedText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		// Request a fresh read from the event thread
		_pasteRequested = true;

		// Wait briefly for the event thread to process
		// (the event loop runs every ~16ms at 60fps or every 100ms poll timeout)
		for (int i = 0; i < 10 && _pasteRequested; i++)
		{
			Thread.Sleep(20);
		}

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
				if (text != null)
				{
					_copiedText = text;
					_cachedText = null;
					// Copy on background thread (opens its own connection, serves paste requests)
					new Thread(() => { try { _ = uno_clipboard_set_text(text); } catch { } })
					{ IsBackground = true, Name = "WaylandClipboardCopy" }.Start();
				}
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
