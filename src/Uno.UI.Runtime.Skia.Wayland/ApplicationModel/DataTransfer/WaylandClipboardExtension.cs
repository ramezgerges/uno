using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandClipboardExtension : IClipboardExtension
{
	private const string Lib = "libuno-clipboard";

	// C helper for protocol operations
	[DllImport(Lib)] private static extern int uno_clipboard_init(IntPtr wlDisplay);
	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_text();
	[DllImport(Lib)] private static extern int uno_clipboard_set_text([MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint serial);
	[DllImport(Lib)] private static extern void uno_clipboard_free(IntPtr ptr);
	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_device();
	[DllImport(Lib)] private static extern IntPtr uno_clipboard_get_manager();

	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private volatile string? _cachedText;
	private volatile string? _copiedText;
	private volatile bool _pasteRequested;
	private volatile string? _pendingCopyText;
	private volatile uint _lastSerial;
	private bool _initialized;
#pragma warning disable CS0414
	private bool _csharpListenerTest;
#pragma warning restore CS0414

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }
	internal void SetLastSerial(uint serial) => _lastSerial = serial;

	internal void InitOnEventThread(IntPtr wlDisplay)
	{
		if (_initialized) { return; }

		// Step 1: Use C helper to init (creates device with C listeners)
		var rc = uno_clipboard_init(wlDisplay);
		Console.Error.WriteLine($"[Clipboard] C init result={rc}");
		if (rc != 0) { return; }
		_initialized = true;

		// Step 2: Try to add a C# listener to the C-created device
		// This tests whether C# listeners work on a properly-created device
		var device = uno_clipboard_get_device();
		Console.Error.WriteLine($"[Clipboard] device from C={device}");

		if (device != IntPtr.Zero)
		{
			try
			{
				// Create a SECOND data device using C# marshalling,
				// with the C helper's manager — to test if C# device creation is the issue
				var manager = uno_clipboard_get_manager();
				Console.Error.WriteLine($"[Clipboard] manager from C={manager}");

				if (manager != IntPtr.Zero)
				{
					// Test: create device via C# marshal_flags
					var testDevice = WaylandBindings.wl_proxy_marshal_flags(
						manager, 1, WaylandInterfaces.wl_data_device_interface,
						WaylandBindings.wl_proxy_get_version(manager), 0,
						IntPtr.Zero, IntPtr.Zero); // NULL seat — will fail but tests if marshal_flags crashes
					Console.Error.WriteLine($"[Clipboard] C# test device (null seat)={testDevice}");
					// Don't use this device — it's invalid (null seat)
					if (testDevice != IntPtr.Zero)
					{
						WaylandBindings.wl_proxy_destroy(testDevice);
					}
				}

				_csharpListenerTest = true;
				Console.Error.WriteLine("[Clipboard] C# test passed, no crash");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Clipboard] C# test exception: {ex.Message}");
			}
		}
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

	public void Clear() { _copiedText = null; _cachedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); }
	public void Flush() { }

	public DataPackageView? GetContent()
	{
		_pasteRequested = true;
		for (int i = 0; i < 15 && _pasteRequested; i++) { Thread.Sleep(20); }
		var text = _cachedText ?? _copiedText;
		if (text != null) { var p = new DataPackage(); p.SetText(text); return p.GetView(); }
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
