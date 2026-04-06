using System;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using libuno-clipboard.so. Both paste and copy use the app's
/// wl_display and execute on the event thread. The native helper uses
/// wayland-scanner generated code for correct protocol handling.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private const string Lib = "libuno-clipboard";

	[DllImport(Lib)]
	private static extern IntPtr uno_clipboard_get_text_from_display(IntPtr wlDisplay);

	[DllImport(Lib)]
	private static extern IntPtr uno_clipboard_set_selection(
		IntPtr wlDisplay, IntPtr text, uint serial, out IntPtr outCtx);

	[DllImport(Lib)]
	private static extern void uno_clipboard_destroy_source(IntPtr source, IntPtr ctx);

	[DllImport(Lib)]
	private static extern void uno_clipboard_free(IntPtr ptr);

	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private volatile string? _cachedText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopyText;
	private volatile uint _lastSerial;

	// Current data source (kept alive for send callbacks)
	private IntPtr _currentSource;
	private IntPtr _currentCtx;
	private IntPtr _currentTextPtr;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	internal void SetDisplay(IntPtr wlDisplay) => _wlDisplay = wlDisplay;
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	internal void ProcessOnEventThread()
	{
		if (_wlDisplay == IntPtr.Zero) { return; }

		if (_pasteRequested)
		{
			_pasteRequested = false;
			Console.Error.WriteLine($"[Clipboard] ProcessOnEventThread: calling get_text_from_display");
			var ptr = uno_clipboard_get_text_from_display(_wlDisplay);
			Console.Error.WriteLine($"[Clipboard] ProcessOnEventThread: ptr={ptr}");
			if (ptr != IntPtr.Zero)
			{
				_cachedText = Marshal.PtrToStringUTF8(ptr);
				Console.Error.WriteLine($"[Clipboard] ProcessOnEventThread: got '{_cachedText?.Substring(0, Math.Min(_cachedText?.Length ?? 0, 30))}'");
				uno_clipboard_free(ptr);
			}
			else
			{
				_cachedText = null;
			}
		}

		var copyText = Interlocked.Exchange(ref _pendingCopyText, null);
		if (copyText != null)
		{
			// Destroy previous source
			if (_currentSource != IntPtr.Zero)
			{
				uno_clipboard_destroy_source(_currentSource, _currentCtx);
				_currentSource = IntPtr.Zero;
				_currentCtx = IntPtr.Zero;
			}
			if (_currentTextPtr != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(_currentTextPtr);
			}

			// The native function needs a C string that stays alive as long as the source
			_currentTextPtr = Marshal.StringToHGlobalAnsi(copyText);
			_currentSource = uno_clipboard_set_selection(
				_wlDisplay, _currentTextPtr, _lastSerial, out _currentCtx);
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
		Console.Error.WriteLine($"[Clipboard] GetContent called, requesting paste...");
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) { Thread.Sleep(20); }
		Console.Error.WriteLine($"[Clipboard] paste done, cachedText='{_cachedText?.Substring(0, Math.Min(_cachedText?.Length ?? 0, 30))}' copiedText='{_copiedText?.Substring(0, Math.Min(_copiedText?.Length ?? 0, 30))}'");

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
					_pendingCopyText = text;
				}
			}
		}
		catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
