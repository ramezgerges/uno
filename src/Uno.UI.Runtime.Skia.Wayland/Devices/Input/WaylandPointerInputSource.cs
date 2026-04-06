using System;
using System.Runtime.CompilerServices;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;

namespace Uno.WinUI.Runtime.Skia.Wayland;

#pragma warning disable IDE0051 // Remove unused private members
internal class WaylandPointerInputSource : IUnoCorePointerInputSource
{
#pragma warning disable CS0067 // Some events are not raised on Wayland yet
	public event TypedEventHandler<object, PointerEventArgs>? PointerCaptureLost;
	public event TypedEventHandler<object, PointerEventArgs>? PointerEntered;
	public event TypedEventHandler<object, PointerEventArgs>? PointerExited;
	public event TypedEventHandler<object, PointerEventArgs>? PointerMoved;
	public event TypedEventHandler<object, PointerEventArgs>? PointerPressed;
	public event TypedEventHandler<object, PointerEventArgs>? PointerReleased;
	public event TypedEventHandler<object, PointerEventArgs>? PointerWheelChanged;
	public event TypedEventHandler<object, PointerEventArgs>? PointerCancelled;
#pragma warning restore CS0067

	// Wayland button codes (same as Linux input event codes)
	private const uint BTN_LEFT = 0x110;
	private const uint BTN_RIGHT = 0x111;
	private const uint BTN_MIDDLE = 0x112;

	// Wayland pointer axis
	internal const uint WL_POINTER_AXIS_VERTICAL_SCROLL = 0;
	internal const uint WL_POINTER_AXIS_HORIZONTAL_SCROLL = 1;

	// Wayland button state
	internal const uint WL_POINTER_BUTTON_STATE_RELEASED = 0;
	internal const uint WL_POINTER_BUTTON_STATE_PRESSED = 1;

	private const int ScrollWheelDelta = 120; // Standard mouse wheel delta

	private readonly WaylandXamlRootHost _host;
	private CoreCursor _pointerCursor;
	private Point _mousePosition;
	private bool _isLeftButtonPressed;
	private bool _isMiddleButtonPressed;
	private bool _isRightButtonPressed;
	private uint _lastPointerSerial;
	private PointerPointProperties? _previousPointerPointProperties;

	public WaylandPointerInputSource(IXamlRootHost host)
	{
		if (host is not WaylandXamlRootHost)
		{
			throw new ArgumentException($"{nameof(host)} must be a Wayland host instance");
		}

		_host = (WaylandXamlRootHost)host;
		_host.SetPointerSource(this);

		_pointerCursor = new CoreCursor(CoreCursorType.Arrow, 0);
	}

	[NotImplemented]
	public bool HasCapture => false;

	public CoreCursor PointerCursor
	{
		get => _pointerCursor;
		set
		{
			_pointerCursor = value;

			var shape = value.Type switch
			{
				CoreCursorType.Arrow => WpCursorShapeDeviceV1Shape.Default,
				CoreCursorType.Cross => WpCursorShapeDeviceV1Shape.Crosshair,
				CoreCursorType.Hand => WpCursorShapeDeviceV1Shape.Pointer,
				CoreCursorType.Help => WpCursorShapeDeviceV1Shape.Help,
				CoreCursorType.IBeam => WpCursorShapeDeviceV1Shape.Text,
				CoreCursorType.SizeAll => WpCursorShapeDeviceV1Shape.AllScroll,
				CoreCursorType.SizeNortheastSouthwest => WpCursorShapeDeviceV1Shape.NESWResize,
				CoreCursorType.SizeNorthSouth => WpCursorShapeDeviceV1Shape.NSResize,
				CoreCursorType.SizeNorthwestSoutheast => WpCursorShapeDeviceV1Shape.NWSEResize,
				CoreCursorType.SizeWestEast => WpCursorShapeDeviceV1Shape.EWResize,
				CoreCursorType.UniversalNo => WpCursorShapeDeviceV1Shape.NotAllowed,
				CoreCursorType.UpArrow => WpCursorShapeDeviceV1Shape.NResize,
				CoreCursorType.Wait => WpCursorShapeDeviceV1Shape.Wait,
				_ => WpCursorShapeDeviceV1Shape.Default
			};

			if (_host.CursorShapeDevice != IntPtr.Zero && _lastPointerSerial != 0)
			{
				// wp_cursor_shape_device_v1.set_shape opcode = 1, args: serial, shape
				WaylandBindings.wl_proxy_marshal_flags(
					_host.CursorShapeDevice, CursorShape.WP_CURSOR_SHAPE_DEVICE_V1_SET_SHAPE,
					IntPtr.Zero, WaylandBindings.wl_proxy_get_version(_host.CursorShapeDevice), 0,
					(IntPtr)_lastPointerSerial, (IntPtr)(uint)shape);
			}
		}
	}

	public Point PointerPosition => _mousePosition;

	public void SetPointerCapture(PointerIdentifier pointer) => LogNotSupported();
	public void ReleasePointerCapture(PointerIdentifier pointer) => LogNotSupported();
	public void ReleasePointerCapture() => LogNotSupported();
	public void SetPointerCapture() => LogNotSupported();

	internal void ProcessPointerEnter(uint serial, double x, double y)
	{
		_lastPointerSerial = serial;
		_mousePosition = new Point(x, y);

		var args = CreatePointerEventArgsFromCurrentState();
		WaylandXamlRootHost.QueueAction(_host, () => PointerEntered?.Invoke(this, args));
	}

	internal void ProcessPointerLeave(uint serial)
	{
		var args = CreatePointerEventArgsFromCurrentState();
		WaylandXamlRootHost.QueueAction(_host, () => PointerExited?.Invoke(this, args));
	}

	internal void ProcessPointerMotion(uint time, double x, double y)
	{
		_mousePosition = new Point(x, y);

		var args = CreatePointerEventArgsFromCurrentState(time);
		WaylandXamlRootHost.QueueAction(_host, () => PointerMoved?.Invoke(this, args));
	}

	internal void ProcessPointerButton(uint serial, uint time, uint button, uint state)
	{
		_lastPointerSerial = serial;
		var isPressed = state == WL_POINTER_BUTTON_STATE_PRESSED;

		switch (button)
		{
			case BTN_LEFT:
				_isLeftButtonPressed = isPressed;
				break;
			case BTN_RIGHT:
				_isRightButtonPressed = isPressed;
				break;
			case BTN_MIDDLE:
				_isMiddleButtonPressed = isPressed;
				break;
		}

		var args = CreatePointerEventArgsFromCurrentState(time);

		if (isPressed)
		{
			WaylandXamlRootHost.QueueAction(_host, () => PointerPressed?.Invoke(this, args));
		}
		else
		{
			WaylandXamlRootHost.QueueAction(_host, () => PointerReleased?.Invoke(this, args));
		}
	}

	internal void ProcessPointerAxis(uint time, uint axis, double value)
	{
		var properties = CreatePointerPointProperties();
		properties.IsHorizontalMouseWheel = axis == WL_POINTER_AXIS_HORIZONTAL_SCROLL;
		// Wayland axis value is in surface-local coordinates. Negative means scroll up/left.
		properties.MouseWheelDelta = -(int)(value * ScrollWheelDelta / 10.0);

		var point = CreatePointerPoint(time, properties);
		var args = new PointerEventArgs(point, VirtualKeyModifiers.None);

		WaylandXamlRootHost.QueueAction(_host, () => PointerWheelChanged?.Invoke(this, args));
	}

	private PointerEventArgs CreatePointerEventArgsFromCurrentState(uint time = 0)
	{
		var properties = CreatePointerPointProperties();
		var point = CreatePointerPoint(time, properties);
		return new PointerEventArgs(point, VirtualKeyModifiers.None);
	}

	private PointerPointProperties CreatePointerPointProperties()
	{
		return new PointerPointProperties
		{
			IsLeftButtonPressed = _isLeftButtonPressed,
			IsMiddleButtonPressed = _isMiddleButtonPressed,
			IsRightButtonPressed = _isRightButtonPressed,
		};
	}

	private PointerPoint CreatePointerPoint(uint time, PointerPointProperties properties)
	{
		var scale = ((IXamlRootHost)_host).RootElement?.XamlRoot is { } root
			? root.RasterizationScale
			: 1;

		var timeInMicroseconds = (ulong)time * 1000;
		var scaledPosition = new Point(_mousePosition.X / scale, _mousePosition.Y / scale);

		var point = new PointerPoint(
			frameId: time,
			timestamp: timeInMicroseconds,
			device: PointerDevice.For(PointerDeviceType.Mouse),
			pointerId: 0,
			rawPosition: scaledPosition,
			position: scaledPosition,
			isInContact: properties.HasPressedButton,
			properties: properties
		);

		_previousPointerPointProperties = properties;

		return point;
	}

	private void LogNotSupported([CallerMemberName] string member = "")
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"{member} not supported on Skia for Wayland.");
		}
	}
}
