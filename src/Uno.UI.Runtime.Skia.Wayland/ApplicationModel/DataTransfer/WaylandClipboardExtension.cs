using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard extension using the Wayland wl_data_device protocol for cross-app copy/paste.
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	// Wayland protocol objects (set by WaylandXamlRootHost)
	private IntPtr _wlDisplay;
	private IntPtr _wlDataDeviceManager;
	private IntPtr _wlDataDevice;
	private IntPtr _wlSeat;

	// Current clipboard state
	private string? _copiedText; // Text we've copied (for wl_data_source.send)
#pragma warning disable CS0414
	private string? _incomingText; // Text from other apps (from wl_data_offer)
#pragma warning restore CS0414
	private IntPtr _currentSource; // Our active wl_data_source
	private IntPtr _currentOffer; // The current incoming wl_data_offer
	private readonly List<string> _offerMimeTypes = new();

	// Delegates to prevent GC
	private WlDataDeviceDataOfferDelegate? _dataOfferDelegate;
	private WlDataDeviceEnterDelegate? _enterDelegate;
	private WlDataDeviceLeaveDelegate? _leaveDelegate;
	private WlDataDeviceMotionDelegate? _motionDelegate;
	private WlDataDeviceDropDelegate? _dropDelegate;
	private WlDataDeviceSelectionDelegate? _selectionDelegate;
	private WlDataOfferOfferDelegate? _offerOfferDelegate;
	private WlDataOfferSourceActionsDelegate? _offerSourceActionsDelegate;
	private WlDataOfferActionDelegate? _offerActionDelegate;
	private WlDataSourceTargetDelegate? _sourceTargetDelegate;
	private WlDataSourceSendDelegate? _sourceSendDelegate;
	private WlDataSourceCancelledDelegate? _sourceCancelledDelegate;
	private GCHandle _dataDeviceListenerHandle;
	private GCHandle _dataOfferListenerHandle;
	private GCHandle _dataSourceListenerHandle;

	public event EventHandler<object>? ContentChanged;

	public void StartContentChanged() { }
	public void StopContentChanged() { }

	/// <summary>
	/// Called by WaylandXamlRootHost after binding the data device manager and seat.
	/// </summary>
	internal void Initialize(IntPtr wlDisplay, IntPtr wlDataDeviceManager, IntPtr wlSeat)
	{
		_wlDisplay = wlDisplay;
		_wlDataDeviceManager = wlDataDeviceManager;
		_wlSeat = wlSeat;

		if (_wlDataDeviceManager == IntPtr.Zero || _wlSeat == IntPtr.Zero)
		{
			return;
		}

		// wl_data_device_manager.get_data_device opcode = 1, args: new_id, seat
		_wlDataDevice = WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDeviceManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0,
			IntPtr.Zero, _wlSeat);

		if (_wlDataDevice == IntPtr.Zero)
		{
			return;
		}

		// Set up data device listener
		_dataOfferDelegate = OnDataOffer;
		_enterDelegate = OnDndEnter;
		_leaveDelegate = OnDndLeave;
		_motionDelegate = OnDndMotion;
		_dropDelegate = OnDndDrop;
		_selectionDelegate = OnSelection;
		var listener = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(_dataOfferDelegate),
			enter = Marshal.GetFunctionPointerForDelegate(_enterDelegate),
			leave = Marshal.GetFunctionPointerForDelegate(_leaveDelegate),
			motion = Marshal.GetFunctionPointerForDelegate(_motionDelegate),
			drop = Marshal.GetFunctionPointerForDelegate(_dropDelegate),
			selection = Marshal.GetFunctionPointerForDelegate(_selectionDelegate),
		};
		_dataDeviceListenerHandle = GCHandle.Alloc(listener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlDataDevice, _dataDeviceListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug("Wayland clipboard initialized with wl_data_device");
		}
	}

	public void Clear()
	{
		_copiedText = null;
		_incomingText = null;

		if (_currentSource != IntPtr.Zero)
		{
			WaylandBindings.wl_proxy_destroy(_currentSource);
			_currentSource = IntPtr.Zero;
		}

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		// If we have an incoming offer from another app, read it
		if (_currentOffer != IntPtr.Zero && _offerMimeTypes.Contains("text/plain;charset=utf-8"))
		{
			var text = ReadOfferText("text/plain;charset=utf-8");
			if (text != null)
			{
				var package = new DataPackage();
				package.SetText(text);
				return package.GetView();
			}
		}
		else if (_currentOffer != IntPtr.Zero && _offerMimeTypes.Contains("text/plain"))
		{
			var text = ReadOfferText("text/plain");
			if (text != null)
			{
				var package = new DataPackage();
				package.SetText(text);
				return package.GetView();
			}
		}

		// Fallback: return our own copied content
		if (_copiedText != null)
		{
			var package = new DataPackage();
			package.SetText(_copiedText);
			return package.GetView();
		}

		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null)
		{
			Clear();
			return;
		}

		// Extract text synchronously
		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				_copiedText = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
			}
			else
			{
				_copiedText = null;
			}
		}
		catch
		{
			_copiedText = null;
		}

		if (_copiedText == null || _wlDataDeviceManager == IntPtr.Zero || _wlDataDevice == IntPtr.Zero)
		{
			ContentChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		// Destroy previous source
		if (_currentSource != IntPtr.Zero)
		{
			if (_dataSourceListenerHandle.IsAllocated)
			{
				_dataSourceListenerHandle.Free();
			}
			WaylandBindings.wl_proxy_destroy(_currentSource);
		}

		// wl_data_device_manager.create_data_source opcode = 0
		_currentSource = WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDeviceManager, 0, WaylandInterfaces.wl_data_source_interface,
			WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0, IntPtr.Zero);

		if (_currentSource == IntPtr.Zero)
		{
			ContentChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		// Set up data source listener
		_sourceTargetDelegate = OnSourceTarget;
		_sourceSendDelegate = OnSourceSend;
		_sourceCancelledDelegate = OnSourceCancelled;
		var sourceListener = new WlDataSourceListener
		{
			target = Marshal.GetFunctionPointerForDelegate(_sourceTargetDelegate),
			send = Marshal.GetFunctionPointerForDelegate(_sourceSendDelegate),
			cancelled = Marshal.GetFunctionPointerForDelegate(_sourceCancelledDelegate),
		};
		_dataSourceListenerHandle = GCHandle.Alloc(sourceListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_currentSource, _dataSourceListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// wl_data_source.offer opcode = 0, args: mime_type (string)
		OfferMimeType(_currentSource, "text/plain;charset=utf-8");
		OfferMimeType(_currentSource, "text/plain");

		// wl_data_device.set_selection opcode = 1, args: source, serial
		// Serial 0 is acceptable for set_selection
		WaylandBindings.wl_proxy_marshal_flags(
			_wlDataDevice, 1, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(_wlDataDevice), 0,
			_currentSource, IntPtr.Zero);

		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	private static void OfferMimeType(IntPtr source, string mimeType)
	{
		var ptr = Marshal.StringToHGlobalAnsi(mimeType);
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(
				source, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(source), 0, ptr);
		}
		finally
		{
			Marshal.FreeHGlobal(ptr);
		}
	}

	private string? ReadOfferText(string mimeType)
	{
		if (_currentOffer == IntPtr.Zero)
		{
			return null;
		}

		// Create a pipe
		var fds = new int[2];
		if (Pipe(fds) != 0)
		{
			return null;
		}

		var readFd = fds[0];
		var writeFd = fds[1];

		// wl_data_offer.receive opcode = 0, args: mime_type (string), fd (fd)
		var mimePtr = Marshal.StringToHGlobalAnsi(mimeType);
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(
				_currentOffer, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(_currentOffer), 0,
				mimePtr, writeFd);
		}
		finally
		{
			Marshal.FreeHGlobal(mimePtr);
		}

		// Close write end — the compositor will write to it
		_ = WaylandBindings.close(writeFd);

		// Flush to send the receive request
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

		// Read from the pipe
		var buffer = new byte[65536];
		var sb = new StringBuilder();
		int bytesRead;

		while ((bytesRead = Read(readFd, buffer, buffer.Length)) > 0)
		{
			sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
		}

		_ = WaylandBindings.close(readFd);

		return sb.Length > 0 ? sb.ToString() : null;
	}

	// --- Wayland data device callbacks ---

	private void OnDataOffer(IntPtr data, IntPtr dataDevice, IntPtr offer)
	{
		// A new data offer is being introduced. Set up listener to receive mime types.
		_offerMimeTypes.Clear();

		if (_dataOfferListenerHandle.IsAllocated)
		{
			_dataOfferListenerHandle.Free();
		}

		_offerOfferDelegate = OnOfferMimeType;
		_offerSourceActionsDelegate = OnOfferSourceActions;
		_offerActionDelegate = OnOfferAction;
		var listener = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(_offerOfferDelegate),
			source_actions = Marshal.GetFunctionPointerForDelegate(_offerSourceActionsDelegate),
			action = Marshal.GetFunctionPointerForDelegate(_offerActionDelegate),
		};
		_dataOfferListenerHandle = GCHandle.Alloc(listener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(offer, _dataOfferListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
	}

	private void OnOfferMimeType(IntPtr data, IntPtr offer, string mimeType)
	{
		_offerMimeTypes.Add(mimeType);
	}

	private void OnOfferSourceActions(IntPtr data, IntPtr offer, uint sourceActions) { }
	private void OnOfferAction(IntPtr data, IntPtr offer, uint dndAction) { }

	private void OnSelection(IntPtr data, IntPtr dataDevice, IntPtr offer)
	{
		// The clipboard selection changed
		if (_currentOffer != IntPtr.Zero && _currentOffer != offer)
		{
			WaylandBindings.wl_proxy_destroy(_currentOffer);
		}

		_currentOffer = offer;
		_incomingText = null; // Will be read lazily in GetContent()

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	private void OnSourceTarget(IntPtr data, IntPtr source, string mimeType) { }

	private void OnSourceSend(IntPtr data, IntPtr source, string mimeType, int fd)
	{
		// Another app is requesting our clipboard content
		if (_copiedText != null)
		{
			var bytes = Encoding.UTF8.GetBytes(_copiedText);
			var written = Write(fd, bytes, bytes.Length);

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"Clipboard send: wrote {written} bytes for {mimeType}");
			}
		}

		_ = WaylandBindings.close(fd);
	}

	private void OnSourceCancelled(IntPtr data, IntPtr source)
	{
		// Another app took ownership of the clipboard
		if (source == _currentSource)
		{
			_currentSource = IntPtr.Zero;
		}
		WaylandBindings.wl_proxy_destroy(source);
	}

	// DnD callbacks — no-op for clipboard
	private void OnDndEnter(IntPtr data, IntPtr dataDevice, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private void OnDndLeave(IntPtr data, IntPtr dataDevice) { }
	private void OnDndMotion(IntPtr data, IntPtr dataDevice, uint time, int x, int y) { }
	private void OnDndDrop(IntPtr data, IntPtr dataDevice) { }

	// --- Native helpers ---

	[DllImport("libc", EntryPoint = "pipe")]
	private static extern int Pipe(int[] fds);

	[DllImport("libc", EntryPoint = "read")]
	private static extern int Read(int fd, byte[] buf, int count);

	[DllImport("libc", EntryPoint = "write")]
	private static extern int Write(int fd, byte[] buf, int count);
}
