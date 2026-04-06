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
/// Clipboard using the wl_data_device protocol with a SEPARATE Wayland connection
/// per operation. This avoids thread safety issues — each operation gets its own
/// wl_display, does roundtrips on that connection, and disconnects when done.
/// Same approach as wl-clipboard.
/// </summary>
internal unsafe class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	// For copy: the text to serve, and a background thread to dispatch events
	private volatile string? _servingText;
	private Thread? _serveThread;

	public event EventHandler<object>? ContentChanged;
	public void StartContentChanged() { }
	public void StopContentChanged() { }

	public void Clear()
	{
		_servingText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		var text = PasteText();
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
		if (content == null)
		{
			_servingText = null;
			ContentChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		try
		{
			var view = content.GetView();
			if (view.Contains(StandardDataFormats.Text))
			{
				var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				if (text != null)
				{
					CopyText(text);
				}
			}
		}
		catch { }

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	// -----------------------------------------------------------------------
	// Paste: open a fresh connection, get the current selection, read it, close
	// -----------------------------------------------------------------------

	private static string? PasteText()
	{
		var display = WaylandBindings.wl_display_connect(null);
		if (display == IntPtr.Zero) { return null; }

		try
		{
			var state = new PasteState();
			var registryListener = new WlRegistryListener
			{
				global = Marshal.GetFunctionPointerForDelegate(state.RegistryGlobal = state.OnRegistryGlobal),
				global_remove = Marshal.GetFunctionPointerForDelegate(state.RegistryRemove = state.OnRegistryRemove),
			};
			var regHandle = GCHandle.Alloc(registryListener, GCHandleType.Pinned);

			var registry = WaylandBindings.wl_display_get_registry(display);
			_ = WaylandBindings.wl_proxy_add_listener(registry, regHandle.AddrOfPinnedObject(), IntPtr.Zero);
			_ = WaylandBindings.wl_display_roundtrip(display);

			if (state.Seat == IntPtr.Zero || state.Manager == IntPtr.Zero)
			{
				regHandle.Free();
				return null;
			}

			// Create data device
			var device = WaylandBindings.wl_proxy_marshal_flags(
				state.Manager, 1, WaylandInterfaces.wl_data_device_interface,
				WaylandBindings.wl_proxy_get_version(state.Manager), 0,
				IntPtr.Zero, state.Seat);

			var deviceListener = new WlDataDeviceListener
			{
				data_offer = Marshal.GetFunctionPointerForDelegate(state.DataOfferDel = state.OnDataOffer),
				enter = IntPtr.Zero,
				leave = IntPtr.Zero,
				motion = IntPtr.Zero,
				drop = IntPtr.Zero,
				selection = Marshal.GetFunctionPointerForDelegate(state.SelectionDel = state.OnSelection),
			};
			var devHandle = GCHandle.Alloc(deviceListener, GCHandleType.Pinned);
			_ = WaylandBindings.wl_proxy_add_listener(device, devHandle.AddrOfPinnedObject(), IntPtr.Zero);

			// Pre-create offer listener
			var offerListener = new WlDataOfferListener
			{
				offer = Marshal.GetFunctionPointerForDelegate(state.OfferOfferDel = state.OnOfferOffer),
				source_actions = IntPtr.Zero,
				action = IntPtr.Zero,
			};
			state.OfferListenerHandle = GCHandle.Alloc(offerListener, GCHandleType.Pinned);

			// Roundtrip to receive the selection
			_ = WaylandBindings.wl_display_roundtrip(display);

			string? result = null;
			if (state.Offer != IntPtr.Zero && state.Done)
			{
				// Find text mime
				string? mime = null;
				foreach (var m in state.MimeTypes)
				{
					if (m == "text/plain;charset=utf-8") { mime = m; break; }
					if (m == "text/plain" && mime == null) { mime = m; }
				}

				if (mime != null)
				{
					var fds = new int[2];
					if (Pipe(fds) == 0)
					{
						// Send receive request
						var mimePtr = Marshal.StringToHGlobalAnsi(mime);
						WaylandBindings.wl_proxy_marshal_flags(
							state.Offer, 0, IntPtr.Zero,
							WaylandBindings.wl_proxy_get_version(state.Offer), 0,
							mimePtr, fds[1]);
						Marshal.FreeHGlobal(mimePtr);

						_ = WaylandBindings.close(fds[1]);
						_ = WaylandBindings.wl_display_flush(display);

						// Read from pipe
						var buf = new byte[65536];
						var sb = new StringBuilder();
						int n;
						while ((n = LibcRead(fds[0], buf, buf.Length)) > 0)
						{
							sb.Append(Encoding.UTF8.GetString(buf, 0, n));
						}
						_ = WaylandBindings.close(fds[0]);
						if (sb.Length > 0) { result = sb.ToString(); }
					}
				}
			}

			state.OfferListenerHandle.Free();
			devHandle.Free();
			regHandle.Free();
			return result;
		}
		finally
		{
			WaylandBindings.wl_display_disconnect(display);
		}
	}

	// -----------------------------------------------------------------------
	// Copy: open a fresh connection, set selection, serve data on background thread
	// -----------------------------------------------------------------------

	private void CopyText(string text)
	{
		_servingText = text;

		// Stop previous serve thread
		_serveThread?.Interrupt();

		_serveThread = new Thread(() => ServeClipboard(text))
		{
			IsBackground = true,
			Name = "WaylandClipboardServe",
		};
		_serveThread.Start();
	}

	private static void ServeClipboard(string text)
	{
		var display = WaylandBindings.wl_display_connect(null);
		if (display == IntPtr.Zero) { return; }

		try
		{
			var state = new CopyState { Text = text };

			var registryListener = new WlRegistryListener
			{
				global = Marshal.GetFunctionPointerForDelegate(state.RegistryGlobal = state.OnRegistryGlobal),
				global_remove = Marshal.GetFunctionPointerForDelegate(state.RegistryRemove = state.OnRegistryRemove),
			};
			var regHandle = GCHandle.Alloc(registryListener, GCHandleType.Pinned);

			var registry = WaylandBindings.wl_display_get_registry(display);
			_ = WaylandBindings.wl_proxy_add_listener(registry, regHandle.AddrOfPinnedObject(), IntPtr.Zero);
			_ = WaylandBindings.wl_display_roundtrip(display);

			if (state.Seat == IntPtr.Zero || state.Manager == IntPtr.Zero)
			{
				regHandle.Free();
				return;
			}

			var device = WaylandBindings.wl_proxy_marshal_flags(
				state.Manager, 1, WaylandInterfaces.wl_data_device_interface,
				WaylandBindings.wl_proxy_get_version(state.Manager), 0,
				IntPtr.Zero, state.Seat);

			var source = WaylandBindings.wl_proxy_marshal_flags(
				state.Manager, 0, WaylandInterfaces.wl_data_source_interface,
				WaylandBindings.wl_proxy_get_version(state.Manager), 0, IntPtr.Zero);

			var sourceListener = new WlDataSourceListener
			{
				target = IntPtr.Zero,
				send = Marshal.GetFunctionPointerForDelegate(state.SendDel = state.OnSend),
				cancelled = Marshal.GetFunctionPointerForDelegate(state.CancelledDel = state.OnCancelled),
				dnd_drop_performed = IntPtr.Zero,
				dnd_finished = IntPtr.Zero,
				action = IntPtr.Zero,
			};
			var srcHandle = GCHandle.Alloc(sourceListener, GCHandleType.Pinned);
			_ = WaylandBindings.wl_proxy_add_listener(source, srcHandle.AddrOfPinnedObject(), IntPtr.Zero);

			// Offer mime types
			OfferMime(source, "text/plain;charset=utf-8");
			OfferMime(source, "text/plain");

			// Set selection
			WaylandBindings.wl_proxy_marshal_flags(
				device, 1, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(device), 0,
				source, IntPtr.Zero);

			_ = WaylandBindings.wl_display_flush(display);

			// Serve until cancelled or interrupted (max 30 seconds)
			var fd = WaylandBindings.wl_display_get_fd(display);
			var pollFd = new PollFd { fd = fd, events = WaylandBindings.POLLIN };
			var iterations = 0;
			while (!state.Cancelled && iterations < 300)
			{
				_ = WaylandBindings.wl_display_flush(display);
				pollFd.revents = 0;
				var ret = WaylandBindings.poll(&pollFd, 1, 100);
				if (ret > 0)
				{
					_ = WaylandBindings.wl_display_dispatch(display);
				}
				iterations++;
			}

			srcHandle.Free();
			regHandle.Free();
		}
		catch (ThreadInterruptedException) { }
		finally
		{
			WaylandBindings.wl_display_disconnect(display);
		}
	}

	private static void OfferMime(IntPtr source, string mime)
	{
		var p = Marshal.StringToHGlobalAnsi(mime);
		try { WaylandBindings.wl_proxy_marshal_flags(source, 0, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(source), 0, p); }
		finally { Marshal.FreeHGlobal(p); }
	}

	// -----------------------------------------------------------------------
	// Callback state classes — prevent delegates from being GC'd
	// -----------------------------------------------------------------------

	private class PasteState
	{
		public IntPtr Seat, Manager, Offer;
		public bool Done;
		public List<string> MimeTypes = new();
		public GCHandle OfferListenerHandle;

		// Prevent GC of delegates
		public WlRegistryGlobalDelegate? RegistryGlobal;
		public WlRegistryGlobalRemoveDelegate? RegistryRemove;
		public WlDataDeviceDataOfferDelegate? DataOfferDel;
		public WlDataDeviceSelectionDelegate? SelectionDel;
		public WlDataOfferOfferDelegate? OfferOfferDel;

		public void OnRegistryGlobal(IntPtr data, IntPtr registry, uint name, string iface, uint version)
		{
			if (iface == "wl_seat")
			{
				Seat = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_seat_interface, 1);
			}
			else if (iface == "wl_data_device_manager")
			{
				Manager = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
			}
		}

		public void OnRegistryRemove(IntPtr data, IntPtr registry, uint name) { }

		public void OnDataOffer(IntPtr data, IntPtr dd, IntPtr offer)
		{
			MimeTypes.Clear();
			Offer = offer;
			_ = WaylandBindings.wl_proxy_add_listener(offer, OfferListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
		}

		public void OnSelection(IntPtr data, IntPtr dd, IntPtr offer) { Done = true; }
		public void OnOfferOffer(IntPtr data, IntPtr offer, string mime) { MimeTypes.Add(mime); }
	}

	private class CopyState
	{
		public IntPtr Seat, Manager;
		public string Text = "";
		public volatile bool Cancelled;

		public WlRegistryGlobalDelegate? RegistryGlobal;
		public WlRegistryGlobalRemoveDelegate? RegistryRemove;
		public WlDataSourceSendDelegate? SendDel;
		public WlDataSourceCancelledDelegate? CancelledDel;

		public void OnRegistryGlobal(IntPtr data, IntPtr registry, uint name, string iface, uint version)
		{
			if (iface == "wl_seat")
			{
				Seat = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_seat_interface, 1);
			}
			else if (iface == "wl_data_device_manager")
			{
				Manager = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_data_device_manager_interface, 1);
			}
		}

		public void OnRegistryRemove(IntPtr data, IntPtr registry, uint name) { }

		public void OnSend(IntPtr data, IntPtr source, string mime, int fd)
		{
			var bytes = Encoding.UTF8.GetBytes(Text);
			int written = 0;
			while (written < bytes.Length)
			{
				var n = LibcWrite(fd, bytes, written, bytes.Length - written);
				if (n <= 0) { break; }
				written += n;
			}
			_ = WaylandBindings.close(fd);
		}

		public void OnCancelled(IntPtr data, IntPtr source) { Cancelled = true; }
	}

	[DllImport("libc", EntryPoint = "pipe")] private static extern int Pipe(int[] fds);
	[DllImport("libc", EntryPoint = "read")] private static extern int LibcRead(int fd, byte[] buf, int count);

	[DllImport("libc", EntryPoint = "write")]
	private static extern int LibcWriteRaw(int fd, IntPtr buf, int count);

	private static int LibcWrite(int fd, byte[] buf, int offset, int count)
	{
		fixed (byte* p = &buf[offset])
		{
			return LibcWriteRaw(fd, (IntPtr)p, count);
		}
	}
}
