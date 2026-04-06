using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal partial class WaylandXamlRootHost : IXamlRootHost
{
	private static readonly ConcurrentDictionary<Window, WaylandXamlRootHost> _windowToHost = new();
	private static bool _firstWindowCreated;

	private readonly Window _window;
	private readonly WaylandWindowWrapper _wrapper;
	private readonly TaskCompletionSource _closedTcs = new();
	private readonly AutoResetEvent _renderEvent = new(false);

	private WaylandPointerInputSource? _pointerSource;
	private WaylandKeyboardInputSource? _keyboardSource;

	// Wayland protocol objects
	private IntPtr _wlDisplay;
	private IntPtr _wlRegistry;
	private IntPtr _wlCompositor;
	private IntPtr _wlShm;
	private IntPtr _wlSeat;
	private IntPtr _wlPointer;
	private IntPtr _wlKeyboard;
	private IntPtr _xdgWmBase;
	private IntPtr _xdgDecorationManager;
	private IntPtr _cursorShapeManager;
	private IntPtr _cursorShapeDevice;
	private IntPtr _wlSurface;
	private IntPtr _xdgSurface;
	private IntPtr _xdgToplevel;

	// Render thread + event thread
	private WaylandRenderer? _renderer;
	private Thread? _renderThread;
	private Thread? _eventThread;
	private volatile bool _renderLoopRunning = true;

	// Window state
	private int _width;
	private int _height;
#pragma warning disable CS0414
	private bool _configured;
#pragma warning restore CS0414

	// GC preventing delegate collection
	private WlRegistryGlobalDelegate? _registryGlobalDelegate;
	private WlRegistryGlobalRemoveDelegate? _registryGlobalRemoveDelegate;
	private XdgWmBasePingDelegate? _xdgWmBasePingDelegate;
	private XdgSurfaceConfigureDelegate? _xdgSurfaceConfigureDelegate;
	private XdgToplevelConfigureDelegate? _xdgToplevelConfigureDelegate;
	private XdgToplevelCloseDelegate? _xdgToplevelCloseDelegate;
	private XdgToplevelConfigureBoundsDelegate? _xdgToplevelConfigureBoundsDelegate;
	private XdgToplevelWmCapabilitiesDelegate? _xdgToplevelWmCapabilitiesDelegate;
	private WlSeatCapabilitiesDelegate? _seatCapabilitiesDelegate;
	private WlSeatNameDelegate? _seatNameDelegate;
	// Pointer listener delegates
	private WlPointerEnterDelegate? _pointerEnterDelegate;
	private WlPointerLeaveDelegate? _pointerLeaveDelegate;
	private WlPointerMotionDelegate? _pointerMotionDelegate;
	private WlPointerButtonDelegate? _pointerButtonDelegate;
	private WlPointerAxisDelegate? _pointerAxisDelegate;
	private WlPointerFrameDelegate? _pointerFrameDelegate;
	private WlPointerAxisSourceDelegate? _pointerAxisSourceDelegate;
	private WlPointerAxisStopDelegate? _pointerAxisStopDelegate;
	private WlPointerAxisDiscreteDelegate? _pointerAxisDiscreteDelegate;
	// Keyboard listener delegates
	private WlKeyboardKeymapDelegate? _keyboardKeymapDelegate;
	private WlKeyboardEnterDelegate? _keyboardEnterDelegate;
	private WlKeyboardLeaveDelegate? _keyboardLeaveDelegate;
	private WlKeyboardKeyDelegate? _keyboardKeyDelegate;
	private WlKeyboardModifiersDelegate? _keyboardModifiersDelegate;
	private WlKeyboardRepeatInfoDelegate? _keyboardRepeatInfoDelegate;

	// Pinned listener structs
	private GCHandle _registryListenerHandle;
	private GCHandle _xdgWmBaseListenerHandle;
	private GCHandle _xdgSurfaceListenerHandle;
	private GCHandle _xdgToplevelListenerHandle;
	private GCHandle _seatListenerHandle;
	private GCHandle _pointerListenerHandle;
	private GCHandle _keyboardListenerHandle;

	internal WaylandXamlRootHost(WaylandWindowWrapper wrapper, Window window, XamlRoot xamlRoot)
	{
		_wrapper = wrapper;
		_window = window;

		var size = ApplicationView.PreferredLaunchViewSize;
		_width = size != Size.Empty ? (int)size.Width : 1280;
		_height = size != Size.Empty ? (int)size.Height : 800;

		Initialize();

		_windowToHost[window] = this;
		_firstWindowCreated = true;

		XamlRootMap.Register(xamlRoot, this);

		// Start event dispatch thread (reads from Wayland socket and processes events)
		_eventThread = new Thread(EventLoop)
		{
			IsBackground = true,
			Name = "WaylandEventThread",
		};
		_eventThread.Start();

		// Start render thread
		_renderThread = new Thread(RenderLoop)
		{
			IsBackground = true,
			Name = "WaylandRenderThread",
			Priority = ThreadPriority.AboveNormal
		};
		_renderThread.Start();
	}

	public Task Closed => _closedTcs.Task;

	internal IntPtr EglDisplay { get; set; }
	internal IntPtr WlDisplay => _wlDisplay;
	internal IntPtr WlSurface => _wlSurface;
	internal IntPtr WlShm => _wlShm;
	internal IntPtr CursorShapeDevice => _cursorShapeDevice;
	internal int Width => _width;
	internal int Height => _height;

	UIElement? IXamlRootHost.RootElement => _window.RootElement;

	void IXamlRootHost.InvalidateRender()
	{
		if (!_closedTcs.Task.IsCompleted)
		{
			_renderEvent.Set();
		}
	}

	private void Initialize()
	{
		_wlDisplay = WaylandBindings.wl_display_connect(null);
		if (_wlDisplay == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to connect to Wayland display");
		}

		_wlRegistry = WaylandBindings.wl_display_get_registry(_wlDisplay);

		// Set up registry listener
		_registryGlobalDelegate = OnRegistryGlobal;
		_registryGlobalRemoveDelegate = OnRegistryGlobalRemove;
		var registryListener = new WlRegistryListener
		{
			global = Marshal.GetFunctionPointerForDelegate(_registryGlobalDelegate),
			global_remove = Marshal.GetFunctionPointerForDelegate(_registryGlobalRemoveDelegate),
		};
		_registryListenerHandle = GCHandle.Alloc(registryListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_wlRegistry, _registryListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// Roundtrip to receive all globals
		_ = WaylandBindings.wl_display_roundtrip(_wlDisplay);

		if (_wlCompositor == IntPtr.Zero)
		{
			throw new InvalidOperationException("Wayland compositor not found");
		}
		if (_xdgWmBase == IntPtr.Zero)
		{
			throw new InvalidOperationException("xdg_wm_base not found — compositor doesn't support xdg-shell");
		}

		// Create surface
		// wl_compositor.create_surface opcode = 0
		_wlSurface = WaylandBindings.wl_proxy_marshal_flags(
			_wlCompositor, 0, WaylandInterfaces.wl_surface_interface, WaylandBindings.wl_proxy_get_version(_wlCompositor), 0, IntPtr.Zero);

		if (_wlSurface == IntPtr.Zero)
		{
			throw new InvalidOperationException("Failed to create Wayland surface");
		}

		// Create xdg_surface
		// xdg_wm_base.get_xdg_surface opcode = 2, args: new_id, surface
		_xdgSurface = WaylandBindings.wl_proxy_marshal_flags(
			_xdgWmBase, XdgShell.XDG_WM_BASE_GET_XDG_SURFACE, WaylandInterfaces.xdg_surface_interface,
			WaylandBindings.wl_proxy_get_version(_xdgWmBase), 0, IntPtr.Zero, _wlSurface);

		// Set up xdg_surface listener
		_xdgSurfaceConfigureDelegate = OnXdgSurfaceConfigure;
		var xdgSurfaceListener = new XdgSurfaceListener
		{
			configure = Marshal.GetFunctionPointerForDelegate(_xdgSurfaceConfigureDelegate),
		};
		_xdgSurfaceListenerHandle = GCHandle.Alloc(xdgSurfaceListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_xdgSurface, _xdgSurfaceListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// Create xdg_toplevel
		// xdg_surface.get_toplevel opcode = 1
		_xdgToplevel = WaylandBindings.wl_proxy_marshal_flags(
			_xdgSurface, XdgShell.XDG_SURFACE_GET_TOPLEVEL, WaylandInterfaces.xdg_toplevel_interface,
			WaylandBindings.wl_proxy_get_version(_xdgSurface), 0, IntPtr.Zero);

		// Set up xdg_toplevel listener
		_xdgToplevelConfigureDelegate = OnXdgToplevelConfigure;
		_xdgToplevelCloseDelegate = OnXdgToplevelClose;
		_xdgToplevelConfigureBoundsDelegate = OnXdgToplevelConfigureBounds;
		_xdgToplevelWmCapabilitiesDelegate = OnXdgToplevelWmCapabilities;
		var xdgToplevelListener = new XdgToplevelListener
		{
			configure = Marshal.GetFunctionPointerForDelegate(_xdgToplevelConfigureDelegate),
			close = Marshal.GetFunctionPointerForDelegate(_xdgToplevelCloseDelegate),
			configure_bounds = Marshal.GetFunctionPointerForDelegate(_xdgToplevelConfigureBoundsDelegate),
			wm_capabilities = Marshal.GetFunctionPointerForDelegate(_xdgToplevelWmCapabilitiesDelegate),
		};
		_xdgToplevelListenerHandle = GCHandle.Alloc(xdgToplevelListener, GCHandleType.Pinned);
		_ = WaylandBindings.wl_proxy_add_listener(_xdgToplevel, _xdgToplevelListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

		// Set app ID and title
		// xdg_toplevel.set_app_id opcode = 3
		SetToplevelString(XdgShell.XDG_TOPLEVEL_SET_APP_ID, "uno-platform");
		SetToplevelString(XdgShell.XDG_TOPLEVEL_SET_TITLE, "Uno Platform");

		// Request server-side decorations if the decoration manager is available
		if (_xdgDecorationManager != IntPtr.Zero)
		{
			// zxdg_decoration_manager_v1.get_toplevel_decoration opcode = 1, args: new_id, toplevel
			var decoration = WaylandBindings.wl_proxy_marshal_flags(
				_xdgDecorationManager, XdgDecoration.ZXDG_DECORATION_MANAGER_V1_GET_TOPLEVEL_DECORATION,
				IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_xdgDecorationManager), 0,
				IntPtr.Zero, _xdgToplevel);

			if (decoration != IntPtr.Zero)
			{
				// zxdg_toplevel_decoration_v1.set_mode opcode = 1, args: mode (2 = server_side)
				WaylandBindings.wl_proxy_marshal_flags(
					decoration, XdgDecoration.ZXDG_TOPLEVEL_DECORATION_V1_SET_MODE,
					IntPtr.Zero, WaylandBindings.wl_proxy_get_version(decoration), 0,
					(uint)XdgDecorationMode.ServerSide);
			}
		}

		// Initial commit to get the first configure event
		// wl_surface.commit opcode = 6
		WaylandBindings.wl_proxy_marshal_flags(
			_wlSurface, 6, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlSurface), 0);

		// Roundtrip to receive the configure event
		_ = WaylandBindings.wl_display_roundtrip(_wlDisplay);

		// Try EGL first for GPU acceleration, fall back to software
		try
		{
			_renderer = new WaylandEGLRenderer(this, _wlDisplay, _wlSurface, _width, _height);
			if (this.Log().IsEnabled(LogLevel.Information))
			{
				this.Log().Info("Using EGL GPU renderer");
			}
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().Warn($"EGL renderer failed ({ex.Message}), falling back to software renderer");
			}
			if (_wlShm != IntPtr.Zero)
			{
				_renderer = new WaylandSoftwareRenderer(this, _wlDisplay, _wlSurface, _wlShm);
			}
		}

		if (this.Log().IsEnabled(LogLevel.Information))
		{
			this.Log().Info($"Wayland window initialized: {_width}x{_height}, renderer={_renderer?.GetType().Name ?? "none"}");
		}
	}

	private void SetToplevelString(uint opcode, string value)
	{
		var strPtr = Marshal.StringToHGlobalAnsi(value);
		try
		{
			WaylandBindings.wl_proxy_marshal_flags(
				_xdgToplevel, opcode, IntPtr.Zero,
				WaylandBindings.wl_proxy_get_version(_xdgToplevel), 0, strPtr);
		}
		finally
		{
			Marshal.FreeHGlobal(strPtr);
		}
	}

	internal void Show()
	{
		// The surface is already committed during Initialize().
		// Trigger a render to show content.
		_renderEvent.Set();
	}

	internal void UpdateSizeFromWrapper(int width, int height)
	{
		_width = width;
		_height = height;
		_renderEvent.Set();
	}

	private unsafe void EventLoop()
	{
		var fd = WaylandBindings.wl_display_get_fd(_wlDisplay);
		var pollFd = new PollFd { fd = fd, events = WaylandBindings.POLLIN, revents = 0 };

		while (_renderLoopRunning && !_closedTcs.Task.IsCompleted)
		{
			// Flush outgoing requests
			_ = WaylandBindings.wl_display_flush(_wlDisplay);

			// Poll for incoming events (100ms timeout to check shutdown)
			pollFd.revents = 0;
			var ret = WaylandBindings.poll(&pollFd, 1, 100);

			if (!_renderLoopRunning || _closedTcs.Task.IsCompleted)
			{
				break;
			}

			if (ret > 0)
			{
				// Read and dispatch events from the Wayland socket
				if (WaylandBindings.wl_display_dispatch(_wlDisplay) < 0)
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().Error("Wayland display dispatch error");
					}
					break;
				}
				// Trigger a render after processing events
				_renderEvent.Set();
			}
			else if (ret < 0)
			{
				break; // poll error
			}
		}
	}

	private void RenderLoop()
	{
		var stopwatch = Stopwatch.StartNew();
		var targetInterval = 1000.0 / WaylandApplicationHost.RenderFrameRate;

		while (_renderLoopRunning && !_closedTcs.Task.IsCompleted)
		{
			_renderEvent.WaitOne(TimeSpan.FromMilliseconds(targetInterval));

			if (!_renderLoopRunning || _closedTcs.Task.IsCompleted)
			{
				break;
			}

			// Process any pending events (non-blocking)
			_ = WaylandBindings.wl_display_dispatch_pending(_wlDisplay);

			var frameStart = stopwatch.Elapsed.TotalMilliseconds;

			try
			{
				_renderer?.Render();
			}
			catch (Exception ex)
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"Render error: {ex.Message}");
				}
			}

			// Flush after render (sends the surface commit)
			_ = WaylandBindings.wl_display_flush(_wlDisplay);

			var elapsed = stopwatch.Elapsed.TotalMilliseconds - frameStart;
			var remaining = targetInterval - elapsed;
			if (remaining > 1)
			{
				Thread.Sleep((int)remaining);
			}
		}
	}

	// --- Wayland event callbacks ---

	private void OnRegistryGlobal(IntPtr data, IntPtr registry, uint name, string iface, uint version)
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"Registry global: {iface} v{version} name={name}");
		}

		switch (iface)
		{
			case "wl_compositor":
				_wlCompositor = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_compositor_interface, Math.Min(version, 6u));
				break;
			case "wl_shm":
				_wlShm = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_shm_interface, Math.Min(version, 1u));
				break;
			case "wl_seat":
				_wlSeat = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.wl_seat_interface, Math.Min(version, 5u));
				// Set up seat listener to track capabilities (pointer, keyboard, touch)
				_seatCapabilitiesDelegate = OnSeatCapabilities;
				_seatNameDelegate = OnSeatName;
				var seatListener = new WlSeatListener
				{
					capabilities = Marshal.GetFunctionPointerForDelegate(_seatCapabilitiesDelegate),
					name = Marshal.GetFunctionPointerForDelegate(_seatNameDelegate),
				};
				_seatListenerHandle = GCHandle.Alloc(seatListener, GCHandleType.Pinned);
				_ = WaylandBindings.wl_proxy_add_listener(_wlSeat, _seatListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
				break;
			case "zxdg_decoration_manager_v1":
				_xdgDecorationManager = WaylandBindings.wl_registry_bind_with_name(registry, name, "zxdg_decoration_manager_v1", Math.Min(version, 1u));
				break;
			case "wp_cursor_shape_manager_v1":
				_cursorShapeManager = WaylandBindings.wl_registry_bind_with_name(registry, name, "wp_cursor_shape_manager_v1", Math.Min(version, 1u));
				break;
			case "xdg_wm_base":
				_xdgWmBase = WaylandBindings.wl_registry_bind(registry, name, WaylandInterfaces.xdg_wm_base_interface, Math.Min(version, 4u));
				// Set up xdg_wm_base listener (for ping)
				_xdgWmBasePingDelegate = OnXdgWmBasePing;
				var wmBaseListener = new XdgWmBaseListener
				{
					ping = Marshal.GetFunctionPointerForDelegate(_xdgWmBasePingDelegate),
				};
				_xdgWmBaseListenerHandle = GCHandle.Alloc(wmBaseListener, GCHandleType.Pinned);
				_ = WaylandBindings.wl_proxy_add_listener(_xdgWmBase, _xdgWmBaseListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
				break;
		}
	}

	private void OnRegistryGlobalRemove(IntPtr data, IntPtr registry, uint name)
	{
		// Globals can be removed (e.g., output disconnected). No-op for now.
	}

	private void OnXdgWmBasePing(IntPtr data, IntPtr xdgWmBase, uint serial)
	{
		// Must pong to keep the connection alive
		// xdg_wm_base.pong opcode = 3
		WaylandBindings.wl_proxy_marshal_flags(
			xdgWmBase, XdgShell.XDG_WM_BASE_PONG, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(xdgWmBase), 0, serial);
	}

	private void OnXdgSurfaceConfigure(IntPtr data, IntPtr xdgSurface, uint serial)
	{
		// Acknowledge the configure
		// xdg_surface.ack_configure opcode = 4
		WaylandBindings.wl_proxy_marshal_flags(
			xdgSurface, XdgShell.XDG_SURFACE_ACK_CONFIGURE, IntPtr.Zero,
			WaylandBindings.wl_proxy_get_version(xdgSurface), 0, serial);

		_configured = true;

		// Commit after ack
		WaylandBindings.wl_proxy_marshal_flags(
			_wlSurface, 6, IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_wlSurface), 0);

		_renderEvent.Set();
	}

	private void OnXdgToplevelConfigure(IntPtr data, IntPtr toplevel, int width, int height, IntPtr states)
	{
		if (width > 0 && height > 0)
		{
			_width = width;
			_height = height;
		}
		// The actual ack happens in OnXdgSurfaceConfigure
	}

	private void OnXdgToplevelConfigureBounds(IntPtr data, IntPtr toplevel, int width, int height)
	{
		// Compositor suggests maximum bounds — informational, no action needed
	}

	private void OnXdgToplevelWmCapabilities(IntPtr data, IntPtr toplevel, IntPtr capabilities)
	{
		// Compositor advertises which operations it supports — informational
	}

	private void OnXdgToplevelClose(IntPtr data, IntPtr toplevel)
	{
		_renderLoopRunning = false;
		Close();
	}

	private void OnSeatCapabilities(IntPtr data, IntPtr seat, uint capabilities)
	{
		var caps = (WlSeatCapability)capabilities;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"Seat capabilities: {caps}");
		}

		// Pointer
		if ((caps & WlSeatCapability.Pointer) != 0 && _wlPointer == IntPtr.Zero)
		{
			// wl_seat.get_pointer opcode = 0
			_wlPointer = WaylandBindings.wl_proxy_marshal_flags(
				seat, 0, WaylandInterfaces.wl_pointer_interface,
				WaylandBindings.wl_proxy_get_version(seat), 0, IntPtr.Zero);

			// Set up pointer listener
			_pointerEnterDelegate = OnPointerEnter;
			_pointerLeaveDelegate = OnPointerLeave;
			_pointerMotionDelegate = OnPointerMotion;
			_pointerButtonDelegate = OnPointerButton;
			_pointerAxisDelegate = OnPointerAxis;
			_pointerFrameDelegate = OnPointerFrame;
			_pointerAxisSourceDelegate = OnPointerAxisSource;
			_pointerAxisStopDelegate = OnPointerAxisStop;
			_pointerAxisDiscreteDelegate = OnPointerAxisDiscrete;
			var pointerListener = new WlPointerListener
			{
				enter = Marshal.GetFunctionPointerForDelegate(_pointerEnterDelegate),
				leave = Marshal.GetFunctionPointerForDelegate(_pointerLeaveDelegate),
				motion = Marshal.GetFunctionPointerForDelegate(_pointerMotionDelegate),
				button = Marshal.GetFunctionPointerForDelegate(_pointerButtonDelegate),
				axis = Marshal.GetFunctionPointerForDelegate(_pointerAxisDelegate),
				frame = Marshal.GetFunctionPointerForDelegate(_pointerFrameDelegate),
				axis_source = Marshal.GetFunctionPointerForDelegate(_pointerAxisSourceDelegate),
				axis_stop = Marshal.GetFunctionPointerForDelegate(_pointerAxisStopDelegate),
				axis_discrete = Marshal.GetFunctionPointerForDelegate(_pointerAxisDiscreteDelegate),
			};
			_pointerListenerHandle = GCHandle.Alloc(pointerListener, GCHandleType.Pinned);
			_ = WaylandBindings.wl_proxy_add_listener(_wlPointer, _pointerListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

			if (_cursorShapeManager != IntPtr.Zero)
			{
				// wp_cursor_shape_manager_v1.get_pointer opcode = 1, args: new_id, pointer
				_cursorShapeDevice = WaylandBindings.wl_proxy_marshal_flags(
					_cursorShapeManager, CursorShape.WP_CURSOR_SHAPE_MANAGER_V1_GET_POINTER,
					IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_cursorShapeManager), 0,
					IntPtr.Zero, _wlPointer);
			}

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Created wl_pointer with listener: {_wlPointer}");
			}
		}
		else if ((caps & WlSeatCapability.Pointer) == 0 && _wlPointer != IntPtr.Zero)
		{
			if (_pointerListenerHandle.IsAllocated) { _pointerListenerHandle.Free(); }
			WaylandBindings.wl_proxy_destroy(_wlPointer);
			_wlPointer = IntPtr.Zero;
		}

		// Keyboard
		if ((caps & WlSeatCapability.Keyboard) != 0 && _wlKeyboard == IntPtr.Zero)
		{
			// wl_seat.get_keyboard opcode = 1
			_wlKeyboard = WaylandBindings.wl_proxy_marshal_flags(
				seat, 1, WaylandInterfaces.wl_keyboard_interface,
				WaylandBindings.wl_proxy_get_version(seat), 0, IntPtr.Zero);

			// Set up keyboard listener
			_keyboardKeymapDelegate = OnKeyboardKeymap;
			_keyboardEnterDelegate = OnKeyboardEnter;
			_keyboardLeaveDelegate = OnKeyboardLeave;
			_keyboardKeyDelegate = OnKeyboardKey;
			_keyboardModifiersDelegate = OnKeyboardModifiers;
			_keyboardRepeatInfoDelegate = OnKeyboardRepeatInfo;
			var keyboardListener = new WlKeyboardListener
			{
				keymap = Marshal.GetFunctionPointerForDelegate(_keyboardKeymapDelegate),
				enter = Marshal.GetFunctionPointerForDelegate(_keyboardEnterDelegate),
				leave = Marshal.GetFunctionPointerForDelegate(_keyboardLeaveDelegate),
				key = Marshal.GetFunctionPointerForDelegate(_keyboardKeyDelegate),
				modifiers = Marshal.GetFunctionPointerForDelegate(_keyboardModifiersDelegate),
				repeat_info = Marshal.GetFunctionPointerForDelegate(_keyboardRepeatInfoDelegate),
			};
			_keyboardListenerHandle = GCHandle.Alloc(keyboardListener, GCHandleType.Pinned);
			_ = WaylandBindings.wl_proxy_add_listener(_wlKeyboard, _keyboardListenerHandle.AddrOfPinnedObject(), IntPtr.Zero);

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Created wl_keyboard with listener: {_wlKeyboard}");
			}
		}
		else if ((caps & WlSeatCapability.Keyboard) == 0 && _wlKeyboard != IntPtr.Zero)
		{
			if (_keyboardListenerHandle.IsAllocated) { _keyboardListenerHandle.Free(); }
			WaylandBindings.wl_proxy_destroy(_wlKeyboard);
			_wlKeyboard = IntPtr.Zero;
		}
	}

	private void OnSeatName(IntPtr data, IntPtr seat, string name)
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"Seat name: {name}");
		}
	}

	// --- Pointer event callbacks ---

	private void OnPointerEnter(IntPtr data, IntPtr pointer, uint serial, IntPtr surface, int sx, int sy)
	{
		// Wayland sends fixed-point 24.8 coordinates for enter
		_pointerSource?.ProcessPointerEnter(serial, sx / 256.0, sy / 256.0);
	}

	private void OnPointerLeave(IntPtr data, IntPtr pointer, uint serial, IntPtr surface)
	{
		_pointerSource?.ProcessPointerLeave(serial);
	}

	private void OnPointerMotion(IntPtr data, IntPtr pointer, uint time, int sx, int sy)
	{
		// Wayland sends fixed-point 24.8 coordinates for motion
		_pointerSource?.ProcessPointerMotion(time, sx / 256.0, sy / 256.0);
	}

	private void OnPointerButton(IntPtr data, IntPtr pointer, uint serial, uint time, uint button, uint state)
	{
		_pointerSource?.ProcessPointerButton(serial, time, button, state);
	}

	private void OnPointerAxis(IntPtr data, IntPtr pointer, uint time, uint axis, int value)
	{
		// value is fixed-point 24.8
		_pointerSource?.ProcessPointerAxis(time, axis, value / 256.0);
	}

	private void OnPointerFrame(IntPtr data, IntPtr pointer) { }
	private void OnPointerAxisSource(IntPtr data, IntPtr pointer, uint axisSource) { }
	private void OnPointerAxisStop(IntPtr data, IntPtr pointer, uint time, uint axis) { }
	private void OnPointerAxisDiscrete(IntPtr data, IntPtr pointer, uint axis, int discrete) { }

	// --- Keyboard event callbacks ---

	private void OnKeyboardKeymap(IntPtr data, IntPtr keyboard, uint format, int fd, uint size)
	{
		_keyboardSource?.ProcessKeymapEvent(fd, size);
	}

	private void OnKeyboardEnter(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface, IntPtr keys)
	{
		// Keyboard focus entered our surface
	}

	private void OnKeyboardLeave(IntPtr data, IntPtr keyboard, uint serial, IntPtr surface)
	{
		// Keyboard focus left our surface
	}

	private void OnKeyboardKey(IntPtr data, IntPtr keyboard, uint serial, uint time, uint key, uint state)
	{
		_keyboardSource?.ProcessKeyEvent(key, state, serial);
	}

	private void OnKeyboardModifiers(IntPtr data, IntPtr keyboard, uint serial, uint modsDepressed, uint modsLatched, uint modsLocked, uint group)
	{
		_keyboardSource?.ProcessModifiers(modsDepressed, modsLatched, modsLocked, group);
	}

	private void OnKeyboardRepeatInfo(IntPtr data, IntPtr keyboard, int rate, int delay)
	{
		// Key repeat rate and delay — can be used for implementing key repeat
	}

	// --- Static helpers ---

	internal static WaylandXamlRootHost? GetHostFromWindow(Window window)
		=> _windowToHost.TryGetValue(window, out var host) ? host : null;

	internal static void CloseAllWindows()
	{
		foreach (var (_, host) in _windowToHost)
		{
			host._renderLoopRunning = false;
			host.Close();
		}
	}

	internal static bool AllWindowsDone()
		=> _firstWindowCreated && _windowToHost.IsEmpty;

	internal void Close()
	{
		_renderLoopRunning = false;

		if (_windowToHost.TryRemove(_window, out _))
		{
			// Destroy Wayland objects in reverse order
			if (_cursorShapeDevice != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_cursorShapeDevice);
			}
			if (_wlPointer != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_wlPointer);
			}
			if (_wlKeyboard != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_wlKeyboard);
			}
			if (_xdgToplevel != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_xdgToplevel);
			}
			if (_xdgSurface != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_xdgSurface);
			}
			if (_wlSurface != IntPtr.Zero)
			{
				WaylandBindings.wl_proxy_destroy(_wlSurface);
			}

			_renderer?.Dispose();

			if (_wlDisplay != IntPtr.Zero)
			{
				WaylandBindings.wl_display_disconnect(_wlDisplay);
			}

			// Free pinned handles
			if (_registryListenerHandle.IsAllocated) { _registryListenerHandle.Free(); }
			if (_xdgWmBaseListenerHandle.IsAllocated) { _xdgWmBaseListenerHandle.Free(); }
			if (_xdgSurfaceListenerHandle.IsAllocated) { _xdgSurfaceListenerHandle.Free(); }
			if (_xdgToplevelListenerHandle.IsAllocated) { _xdgToplevelListenerHandle.Free(); }
			if (_seatListenerHandle.IsAllocated) { _seatListenerHandle.Free(); }

			_closedTcs.TrySetResult();
		}
	}

	// --- Input source management ---

	internal void SetPointerSource(WaylandPointerInputSource source) => _pointerSource = source;
	internal void SetKeyboardSource(WaylandKeyboardInputSource source) => _keyboardSource = source;
	internal WaylandPointerInputSource? PointerSource => _pointerSource;
	internal WaylandKeyboardInputSource? KeyboardSource => _keyboardSource;

	public static void QueueAction(IXamlRootHost host, Action action)
		=> host.RootElement?.Dispatcher.RunAsync(CoreDispatcherPriority.High, new DispatchedHandler(action));
}
