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

	// Current state
	private string? _copiedText;
	private IntPtr _currentOffer;
	private readonly List<string> _offerMimeTypes = new();

	// All delegates must be stored as fields to prevent GC collection.
	// Data device listener (6 slots, all v1)
	private WlDataDeviceDataOfferDelegate? _ddDataOffer;
	private WlDataDeviceEnterDelegate? _ddEnter;
	private WlDataDeviceLeaveDelegate? _ddLeave;
	private WlDataDeviceMotionDelegate? _ddMotion;
	private WlDataDeviceDropDelegate? _ddDrop;
	private WlDataDeviceSelectionDelegate? _ddSelection;
	private GCHandle _ddListenerHandle;

	// Data offer listener (3 slots: v1 offer, v3 source_actions, v3 action)
	private WlDataOfferOfferDelegate? _doOffer;
	private WlDataOfferSourceActionsDelegate? _doSourceActions;
	private WlDataOfferActionDelegate? _doAction;
	private GCHandle _doListenerHandle;

	// Data source listener (6 slots: v1 target/send/cancelled, v3 dnd_drop_performed/dnd_finished/action)
	private WlDataSourceTargetDelegate? _dsTarget;
	private WlDataSourceSendDelegate? _dsSend;
	private WlDataSourceCancelledDelegate? _dsCancelled;
	private WlDataSourceDndDropPerformedDelegate? _dsDndDropPerformed;
	private WlDataSourceDndFinishedDelegate? _dsDndFinished;
	private WlDataSourceActionDelegate? _dsAction;
	private GCHandle _dsListenerHandle;
	private IntPtr _currentSource;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	internal void Initialize(IntPtr wlDisplay, IntPtr wlDataDeviceManager, IntPtr wlSeat)
	{
		_wlDisplay = wlDisplay;
		_wlDataDeviceManager = wlDataDeviceManager;

		if (wlDataDeviceManager == IntPtr.Zero || wlSeat == IntPtr.Zero)
		{
			return;
		}

		// Create data device: wl_data_device_manager.get_data_device opcode=1
		_wlDataDevice = WaylandBindings.wl_proxy_marshal_flags(
			wlDataDeviceManager, 1, WaylandInterfaces.wl_data_device_interface,
			WaylandBindings.wl_proxy_get_version(wlDataDeviceManager), 0,
			IntPtr.Zero, wlSeat);

		if (_wlDataDevice == IntPtr.Zero)
		{
			return;
		}

		// Pre-create ALL delegates so they're never null
		_ddDataOffer = OnDataOffer;
		_ddEnter = OnDndEnter;
		_ddLeave = OnDndLeave;
		_ddMotion = OnDndMotion;
		_ddDrop = OnDndDrop;
		_ddSelection = OnSelection;

		var ddListener = new WlDataDeviceListener
		{
			data_offer = Marshal.GetFunctionPointerForDelegate(_ddDataOffer),
			enter = Marshal.GetFunctionPointerForDelegate(_ddEnter),
			leave = Marshal.GetFunctionPointerForDelegate(_ddLeave),
			motion = Marshal.GetFunctionPointerForDelegate(_ddMotion),
			drop = Marshal.GetFunctionPointerForDelegate(_ddDrop),
			selection = Marshal.GetFunctionPointerForDelegate(_ddSelection),
		};
		_ddListenerHandle = GCHandle.Alloc(ddListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlDataDevice, _ddListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// Pre-create offer delegates (reused for every incoming offer)
		_doOffer = OnOfferMimeType;
		_doSourceActions = OnOfferSourceActions;
		_doAction = OnOfferAction;

		// Pre-create source delegates (reused for every outgoing source)
		_dsTarget = OnSourceTarget;
		_dsSend = OnSourceSend;
		_dsCancelled = OnSourceCancelled;
		_dsDndDropPerformed = OnSourceDndDropPerformed;
		_dsDndFinished = OnSourceDndFinished;
		_dsAction = OnSourceAction;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug("Wayland clipboard initialized with wl_data_device");
		}
	}

	public void Clear()
	{
		_copiedText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		if (_currentOffer != IntPtr.Zero)
		{
			string? mimeType = null;
			if (_offerMimeTypes.Contains("text/plain;charset=utf-8"))
			{
				mimeType = "text/plain;charset=utf-8";
			}
			else if (_offerMimeTypes.Contains("text/plain"))
			{
				mimeType = "text/plain";
			}

			if (mimeType != null)
			{
				var text = ReadOfferText(mimeType);
				if (text != null)
				{
					var pkg = new DataPackage();
					pkg.SetText(text);
					return pkg.GetView();
				}
			}
		}

		if (_copiedText != null)
		{
			var pkg = new DataPackage();
			pkg.SetText(_copiedText);
			return pkg.GetView();
		}

		return null;
	}

	public void SetContent(DataPackage? content)
	{
		if (content == null)
		{
			_copiedText = null;
			ContentChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				_copiedText = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
			}
		}
		catch
		{
			_copiedText = null;
		}

		if (_copiedText != null && _wlDataDeviceManager != IntPtr.Zero && _wlDataDevice != IntPtr.Zero)
		{
			// Clean up previous source
			if (_currentSource != IntPtr.Zero)
			{
				if (_dsListenerHandle.IsAllocated) { _dsListenerHandle.Free(); }
				WaylandBindings.wl_proxy_destroy(_currentSource);
			}

			// Create data source: wl_data_device_manager.create_data_source opcode=0
			_currentSource = WaylandBindings.wl_proxy_marshal_flags(
				_wlDataDeviceManager, 0, WaylandInterfaces.wl_data_source_interface,
				WaylandBindings.wl_proxy_get_version(_wlDataDeviceManager), 0, IntPtr.Zero);

			if (_currentSource != IntPtr.Zero)
			{
				// Set up source listener with ALL 6 slots
				var dsListener = new WlDataSourceListener
				{
					target = Marshal.GetFunctionPointerForDelegate(_dsTarget!),
					send = Marshal.GetFunctionPointerForDelegate(_dsSend!),
					cancelled = Marshal.GetFunctionPointerForDelegate(_dsCancelled!),
					dnd_drop_performed = Marshal.GetFunctionPointerForDelegate(_dsDndDropPerformed!),
					dnd_finished = Marshal.GetFunctionPointerForDelegate(_dsDndFinished!),
					action = Marshal.GetFunctionPointerForDelegate(_dsAction!),
				};
				_dsListenerHandle = GCHandle.Alloc(dsListener, GCHandleType.Pinned);
				_ = WaylandBindings.wl_proxy_add_listener(_currentSource, _dsListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

				// Offer MIME types: wl_data_source.offer opcode=0
				OfferMimeType(_currentSource, "text/plain;charset=utf-8");
				OfferMimeType(_currentSource, "text/plain");

				// Set selection: wl_data_device.set_selection opcode=1
				WaylandBindings.wl_proxy_marshal_flags(
					_wlDataDevice, 1, IntPtr.Zero,
					WaylandBindings.wl_proxy_get_version(_wlDataDevice), 0,
					_currentSource, IntPtr.Zero);

				_ = WaylandBindings.wl_display_flush(_wlDisplay);
			}
		}

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// --- Helpers ---

	private static void OfferMimeType(IntPtr source, string mimeType)
	{
		var ptr = Marshal.StringToHGlobalAnsi(mimeType);
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(source, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(source), 0, ptr);
		}
		finally { Marshal.FreeHGlobal(ptr); }
	}

	private string? ReadOfferText(string mimeType)
	{
		var fds = new int[2];
		if (Pipe(fds) != 0) { return null; }

		var mimePtr = Marshal.StringToHGlobalAnsi(mimeType);
		try
		{
			// wl_data_offer.receive opcode=0
			WaylandBindings.wl_proxy_marshal_flags(_currentOffer, 0, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(_currentOffer), 0, mimePtr, fds[1]);
		}
		finally { Marshal.FreeHGlobal(mimePtr); }

		_ = WaylandBindings.close(fds[1]);
		_ = WaylandBindings.wl_display_flush(_wlDisplay);

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

	// --- Data device callbacks ---
	private void OnDataOffer(IntPtr data, IntPtr dataDevice, IntPtr offer)
	{
		_offerMimeTypes.Clear();

		if (_doListenerHandle.IsAllocated) { _doListenerHandle.Free(); }

		var doListener = new WlDataOfferListener
		{
			offer = Marshal.GetFunctionPointerForDelegate(_doOffer!),
			source_actions = Marshal.GetFunctionPointerForDelegate(_doSourceActions!),
			action = Marshal.GetFunctionPointerForDelegate(_doAction!),
		};
		_doListenerHandle = GCHandle.Alloc(doListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(offer, _doListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
	}

	private void OnSelection(IntPtr data, IntPtr dataDevice, IntPtr offer)
	{
		if (_currentOffer != IntPtr.Zero && _currentOffer != offer)
		{
			WaylandBindings.wl_proxy_destroy(_currentOffer);
		}
		_currentOffer = offer;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	private void OnDndEnter(IntPtr data, IntPtr dd, uint serial, IntPtr surface, int x, int y, IntPtr offer) { }
	private void OnDndLeave(IntPtr data, IntPtr dd) { }
	private void OnDndMotion(IntPtr data, IntPtr dd, uint time, int x, int y) { }
	private void OnDndDrop(IntPtr data, IntPtr dd) { }

	// --- Data offer callbacks ---
	private void OnOfferMimeType(IntPtr data, IntPtr offer, string mimeType) => _offerMimeTypes.Add(mimeType);
	private void OnOfferSourceActions(IntPtr data, IntPtr offer, uint sourceActions) { }
	private void OnOfferAction(IntPtr data, IntPtr offer, uint dndAction) { }

	// --- Data source callbacks ---
	private void OnSourceTarget(IntPtr data, IntPtr source, string mimeType) { }
	private void OnSourceSend(IntPtr data, IntPtr source, string mimeType, int fd)
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
	private void OnSourceDndDropPerformed(IntPtr data, IntPtr source) { }
	private void OnSourceDndFinished(IntPtr data, IntPtr source) { }
	private void OnSourceAction(IntPtr data, IntPtr source, uint dndAction) { }

	[DllImport("libc", EntryPoint = "pipe")]
	private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")]
	private static extern int Read(int fd, byte[] buf, int count);
	[DllImport("libc", EntryPoint = "write")]
	private static extern int Write(int fd, byte[] buf, int count);
}
