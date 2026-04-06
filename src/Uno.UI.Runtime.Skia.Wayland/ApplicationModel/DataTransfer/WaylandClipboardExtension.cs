using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private IntPtr _wlDisplay;
	private IntPtr _wlDataDevice;
	private IntPtr _wlDataDeviceManager;
	private IntPtr _wlSeat;
	private bool _initialized;

	// Cached clipboard text — set from event thread, read from UI thread
	private volatile string? _cachedIncomingText;
	private string? _copiedText;
	private IntPtr _currentOffer;
	private IntPtr _currentSource;
	private readonly List<string> _offerMimeTypes = new();

	// Delegates — stored as fields to prevent GC
	private WlDataDeviceDataOfferDelegate? _ddDataOffer;
	private WlDataDeviceSelectionDelegate? _ddSelection;
	private WlDataOfferOfferDelegate? _doOffer;
	private WlDataSourceSendDelegate? _dsSend;
	private WlDataSourceCancelledDelegate? _dsCancelled;
	private GCHandle _ddListenerHandle;
	private GCHandle _doListenerHandle;
	private GCHandle _dsListenerHandle;
	private bool _doListenerReady;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	internal void SetWaylandObjects(IntPtr wlDisplay, IntPtr wlDataDeviceManager, IntPtr wlSeat)
	{
		_wlDisplay = wlDisplay;
		_wlDataDeviceManager = wlDataDeviceManager;
		_wlSeat = wlSeat;
	}

	private void EnsureInitialized()
	{
		if (_initialized || _wlDataDeviceManager == IntPtr.Zero || _wlSeat == IntPtr.Zero)
		{
			return;
		}
		_initialized = true;

		_ddDataOffer = OnDataOffer;
		_ddSelection = OnSelection;
		_doOffer = OnOfferMimeType;
		_dsSend = OnSourceSend;
		_dsCancelled = OnSourceCancelled;

		// Create data device
		_wlDataDevice = WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDeviceManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0,
			IntPtr.Zero, _wlSeat);

		if (_wlDataDevice == IntPtr.Zero) { return; }

		var ddListener = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(_ddDataOffer),
			enter = IntPtr.Zero,
			leave = IntPtr.Zero,
			motion = IntPtr.Zero,
			drop = IntPtr.Zero,
			selection = Marshal.GetFunctionPointerForDelegate(_ddSelection),
		};
		_ddListenerHandle = GCHandle.Alloc(ddListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlDataDevice, _ddListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		var doListener = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(_doOffer),
			source_actions = IntPtr.Zero,
			action = IntPtr.Zero,
		};
		_doListenerHandle = GCHandle.Alloc(doListener, GCHandleType.Pinned);
		_doListenerReady = true;

		// Do a roundtrip to get the initial selection — this is safe because
		// EnsureInitialized is called from GetContent/SetContent on the UI thread,
		// and the event thread hasn't started dispatching data device events yet
		// (the data device was just created).
		_ = WaylandBindings.wl_display_roundtrip(_wlDisplay);
	}

	public void Clear()
	{
		_copiedText = null;
		_cachedIncomingText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		EnsureInitialized();

		// Return cached incoming text (eagerly read on event thread)
		var incoming = _cachedIncomingText;
		if (incoming != null)
		{
			var p = new DataPackage();
			p.SetText(incoming);
			return p.GetView();
		}

		// Fallback to our own copied text
		if (_copiedText != null)
		{
			var p = new DataPackage();
			p.SetText(_copiedText);
			return p.GetView();
		}
		return null;
	}

	public void SetContent(DataPackage? content)
	{
		EnsureInitialized();

		if (content == null) { _copiedText = null; ContentChanged?.Invoke(this, EventArgs.Empty); return; }

		try
		{
			var view = content.GetView();
			_copiedText = view.Contains(StandardDataFormats.Text)
				? view.GetTextAsync().AsTask().GetAwaiter().GetResult()
				: null;
		}
		catch { _copiedText = null; }

		if (_copiedText != null && _wlDataDevice != IntPtr.Zero)
		{
			if (_currentSource != IntPtr.Zero)
			{
				if (_dsListenerHandle.IsAllocated) { _dsListenerHandle.Free(); }
				WaylandBindings.wl_proxy_destroy(_currentSource);
			}

			_currentSource = WaylandBindings.wl_proxy_marshal_flags(
				_wlDataDeviceManager, 0, WaylandInterfaces.wl_data_source_interface,
				WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0, IntPtr.Zero);

			if (_currentSource != IntPtr.Zero)
			{
				var dsListener = new WlDataSourceListener
				{
					target = IntPtr.Zero,
					send = Marshal.GetFunctionPointerForDelegate(_dsSend!),
					cancelled = Marshal.GetFunctionPointerForDelegate(_dsCancelled!),
					dnd_drop_performed = IntPtr.Zero,
					dnd_finished = IntPtr.Zero,
					action = IntPtr.Zero,
				};
				_dsListenerHandle = GCHandle.Alloc(dsListener, GCHandleType.Pinned);
				_ = WaylandBindings.wl_proxy_add_listener(
					_currentSource, _dsListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

				OfferMime(_currentSource, "text/plain;charset=utf-8");
				OfferMime(_currentSource, "text/plain");

				WaylandBindings.wl_proxy_marshal_flags(
					_wlDataDevice, 1, IntPtr.Zero,
					WaylandBindings.wl_proxy_get_version(_wlDataDevice), 0,
					_currentSource, IntPtr.Zero);

				_ = WaylandBindings.wl_display_flush(_wlDisplay);
			}
		}
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	private static void OfferMime(IntPtr source, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(source, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(source), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	/// <summary>
	/// Read text from an offer — called on the EVENT THREAD only.
	/// </summary>
	private string? ReadOfferOnEventThread(IntPtr offer, string mime)
	{
		var fds = new int[2];
		if (Pipe(fds) != 0) { return null; }

		var p = Marshal.StringToHGlobalAnsi(mime);
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(
				offer, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(offer), 0, p, fds[1]);
		}
		finally { Marshal.FreeHGlobal(p); }

		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		// Blocking read — safe because compositor writes to pipe directly
		var buf = new byte[65536];
		var sb = new StringBuilder();
		int n;
		while ((n = Read(fds[0], buf, buf.Length)) > 0)
		{
			sb.Append(Encoding.UTF8.GetString(buf, 0, n));
		}
		_ = WaylandBindings.close(fds[0]);
		return sb.Length > 0 ? sb.ToString() : null;
	}

	// --- Event thread callbacks ---

	private void OnDataOffer(IntPtr data, IntPtr dd, IntPtr offer)
	{
		_offerMimeTypes.Clear();
		if (_doListenerReady)
		{
			_ = WaylandBindings.wl_proxy_add_listener(
				offer, _doListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
		}
	}

	private void OnSelection(IntPtr data, IntPtr dd, IntPtr offer)
	{
		if (_currentOffer != IntPtr.Zero && _currentOffer != offer)
		{
			WaylandBindings.wl_proxy_destroy(_currentOffer);
		}
		_currentOffer = offer;

		// Eagerly read the clipboard text RIGHT HERE on the event thread.
		// This avoids any cross-thread Wayland protocol calls.
		_cachedIncomingText = null;
		if (offer != IntPtr.Zero)
		{
			string? mime = null;
			if (_offerMimeTypes.Contains("text/plain;charset=utf-8"))
			{
				mime = "text/plain;charset=utf-8";
			}
			else if (_offerMimeTypes.Contains("text/plain"))
			{
				mime = "text/plain";
			}

			if (mime != null)
			{
				_cachedIncomingText = ReadOfferOnEventThread(offer, mime);
			}
		}
	}

	private void OnOfferMimeType(IntPtr data, IntPtr offer, string mime) => _offerMimeTypes.Add(mime);

	private void OnSourceSend(IntPtr data, IntPtr source, string mime, int fd)
	{
		if (_copiedText != null)
		{
			var bytes = Encoding.UTF8.GetBytes(_copiedText);
			_ = Write(fd, bytes, bytes.Length);
		}
		_ = WaylandBindings.close(fd);
	}

	private void OnSourceCancelled(IntPtr data, IntPtr source)
	{
		if (source == _currentSource) { _currentSource = IntPtr.Zero; }
		WaylandBindings.wl_proxy_destroy(source);
	}

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int Read(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")] private static extern int Write(int fd, byte[] buf, int count);
}
