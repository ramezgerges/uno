#nullable enable

using System;
using System.Collections.Generic;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Input;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Headless;

// The input source contract is the Windows.UI.Core one; Microsoft.UI.Input brings its own.
using PointerEventArgs = Windows.UI.Core.PointerEventArgs;
using KeyEventArgs = Windows.UI.Core.KeyEventArgs;
using PointerDeviceType = Windows.Devices.Input.PointerDeviceType;

namespace Uno.UI.Runtime.Skia;

/// <summary>A pointer button, as reported by an external input source.</summary>
public enum HeadlessPointerButton
{
	Left,
	Middle,
	Right,
	XButton1,
	XButton2,
}

/// <summary>
/// Feeds external pointer and keyboard input into a headless window. Pass an instance to
/// <see cref="HeadlessHostBuilder.WithInput"/> (or <see cref="HeadlessWindowOptions.Input"/>), then call
/// the inject methods from whatever produces events: a remote protocol, a test driver, a script.
/// </summary>
/// <remarks>
/// Callers report raw transitions, such as "left button went down at 40,80". This class keeps the state a
/// WinUI pointer or key event needs (cursor position, held buttons, held modifiers) and synthesises the
/// events from it. The inject methods are safe to call from any thread: each marshals onto the UI thread.
/// </remarks>
public sealed class HeadlessInput
{
	private readonly object _gate = new();
	private readonly HashSet<HeadlessPointerButton> _pressedButtons = new();
	private readonly HashSet<VirtualKey> _pressedKeys = new();

	private HeadlessPointerInputSource? _pointer;
	private HeadlessKeyboardInputSource? _keyboard;
	private IXamlRootHost? _host;
	private Point _position;

	/// <summary>Current pointer position, in logical pixels.</summary>
	public Point PointerPosition
	{
		get
		{
			lock (_gate)
			{
				return _position;
			}
		}
	}

	/// <summary>Modifier keys currently held down.</summary>
	public VirtualKeyModifiers Modifiers
	{
		get
		{
			lock (_gate)
			{
				return GetModifiers();
			}
		}
	}

	/// <summary>True once a window has picked this instance up, so injection will be delivered.</summary>
	public bool IsConnected => _host is not null;

	internal void Connect(HeadlessPointerInputSource pointer, HeadlessKeyboardInputSource keyboard, IXamlRootHost host)
	{
		_pointer = pointer;
		_keyboard = keyboard;
		_host = host;
	}

	/// <summary>Moves the pointer, keeping any held buttons held.</summary>
	public void MovePointer(double x, double y)
		=> RaisePointer(x, y, PointerUpdateKind.Other, static (source, args) => source.RaiseMoved(args));

	/// <summary>Presses a button at the given position.</summary>
	public void PressPointer(HeadlessPointerButton button, double x, double y)
	{
		lock (_gate)
		{
			_pressedButtons.Add(button);
		}

		RaisePointer(x, y, ToPressedKind(button), static (source, args) => source.RaisePressed(args));
	}

	/// <summary>Releases a button at the given position.</summary>
	public void ReleasePointer(HeadlessPointerButton button, double x, double y)
	{
		// The update kind is computed before the button leaves the set, so the event still reports which
		// button changed while the IsXButtonPressed flags already reflect the release.
		var kind = ToReleasedKind(button);

		lock (_gate)
		{
			_pressedButtons.Remove(button);
		}

		RaisePointer(x, y, kind, static (source, args) => source.RaiseReleased(args));
	}

	/// <summary>
	/// Scrolls at the given position. The delta follows the WinUI convention of 120 units per notch,
	/// positive being up, or right when horizontal.
	/// </summary>
	public void ScrollPointer(double delta, double x, double y, bool isHorizontal = false)
		=> RaisePointer(x, y, PointerUpdateKind.Other, static (source, args) => source.RaiseWheelChanged(args), delta, isHorizontal);

	/// <summary>Raises pointer-entered, so hover states behave as they would with a real device.</summary>
	public void EnterPointer(double x, double y)
		=> RaisePointer(x, y, PointerUpdateKind.Other, static (source, args) => source.RaiseEntered(args));

	/// <summary>Raises pointer-exited at the last known position.</summary>
	public void ExitPointer()
	{
		var position = PointerPosition;
		RaisePointer(position.X, position.Y, PointerUpdateKind.Other, static (source, args) => source.RaiseExited(args));
	}

	/// <summary>
	/// Presses a key. Pass a character for keys that produce text so text input receives it: the source
	/// cannot derive one, since that needs a keyboard layout the host does not have.
	/// </summary>
	public void PressKey(VirtualKey key, char? character = null)
	{
		bool repeat;

		lock (_gate)
		{
			repeat = !_pressedKeys.Add(key);
		}

		RaiseKey(key, character, repeat, isDown: true);
	}

	/// <summary>Releases a key.</summary>
	public void ReleaseKey(VirtualKey key)
	{
		lock (_gate)
		{
			_pressedKeys.Remove(key);
		}

		RaiseKey(key, character: null, repeat: false, isDown: false);
	}

	/// <summary>
	/// Releases every held button and key. Worth calling when an external source disconnects, so the app
	/// is not left believing a button or modifier is still down.
	/// </summary>
	public void ReleaseAll()
	{
		HeadlessPointerButton[] buttons;
		VirtualKey[] keys;
		Point position;

		lock (_gate)
		{
			buttons = new HeadlessPointerButton[_pressedButtons.Count];
			_pressedButtons.CopyTo(buttons);
			keys = new VirtualKey[_pressedKeys.Count];
			_pressedKeys.CopyTo(keys);
			position = _position;
		}

		foreach (var button in buttons)
		{
			ReleasePointer(button, position.X, position.Y);
		}

		foreach (var key in keys)
		{
			ReleaseKey(key);
		}
	}

	private void RaisePointer(
		double x,
		double y,
		PointerUpdateKind kind,
		Action<HeadlessPointerInputSource, PointerEventArgs> raise,
		double wheelDelta = 0,
		bool isHorizontalWheel = false)
	{
		if (_pointer is not { } pointer)
		{
			return;
		}

		PointerEventArgs args;

		lock (_gate)
		{
			_position = new Point(x, y);

			var properties = new PointerPointProperties
			{
				PointerUpdateKind = kind,
				IsLeftButtonPressed = _pressedButtons.Contains(HeadlessPointerButton.Left),
				IsMiddleButtonPressed = _pressedButtons.Contains(HeadlessPointerButton.Middle),
				IsRightButtonPressed = _pressedButtons.Contains(HeadlessPointerButton.Right),
				IsXButton1Pressed = _pressedButtons.Contains(HeadlessPointerButton.XButton1),
				IsXButton2Pressed = _pressedButtons.Contains(HeadlessPointerButton.XButton2),
				MouseWheelDelta = (int)wheelDelta,
				IsHorizontalMouseWheel = isHorizontalWheel,
			};

			var point = new PointerPoint(
				frameId: 0,
				timestamp: (ulong)Environment.TickCount64 * 1000,
				device: PointerDevice.For(PointerDeviceType.Mouse),
				pointerId: 0,
				rawPosition: _position,
				position: _position,
				isInContact: properties.HasPressedButton,
				properties: properties);

			args = new PointerEventArgs(point, GetModifiers());
		}

		pointer.PointerPosition = new Point(x, y);
		Dispatch(() => raise(pointer, args));
	}

	private void RaiseKey(VirtualKey key, char? character, bool repeat, bool isDown)
	{
		if (_keyboard is not { } keyboard)
		{
			return;
		}

		KeyEventArgs args;

		lock (_gate)
		{
			args = new KeyEventArgs(
				"keyboard",
				key,
				GetModifiers(),
				new CorePhysicalKeyStatus
				{
					ScanCode = (uint)key,
					RepeatCount = 1,
					WasKeyDown = repeat,
					IsKeyReleased = !isDown,
				},
				character);
		}

		if (isDown)
		{
			Dispatch(() => keyboard.RaiseKeyDown(args));
		}
		else
		{
			Dispatch(() => keyboard.RaiseKeyUp(args));
		}
	}

	/// <remarks>Must be called while holding the gate.</remarks>
	private VirtualKeyModifiers GetModifiers()
	{
		var modifiers = VirtualKeyModifiers.None;

		if (_pressedKeys.Contains(VirtualKey.Shift) || _pressedKeys.Contains(VirtualKey.LeftShift) || _pressedKeys.Contains(VirtualKey.RightShift))
		{
			modifiers |= VirtualKeyModifiers.Shift;
		}

		if (_pressedKeys.Contains(VirtualKey.Control) || _pressedKeys.Contains(VirtualKey.LeftControl) || _pressedKeys.Contains(VirtualKey.RightControl))
		{
			modifiers |= VirtualKeyModifiers.Control;
		}

		if (_pressedKeys.Contains(VirtualKey.Menu) || _pressedKeys.Contains(VirtualKey.LeftMenu) || _pressedKeys.Contains(VirtualKey.RightMenu))
		{
			modifiers |= VirtualKeyModifiers.Menu;
		}

		if (_pressedKeys.Contains(VirtualKey.LeftWindows) || _pressedKeys.Contains(VirtualKey.RightWindows))
		{
			modifiers |= VirtualKeyModifiers.Windows;
		}

		return modifiers;
	}

	private void Dispatch(Action action)
	{
		if (_host?.RootElement is { } rootElement)
		{
			_ = rootElement.Dispatcher.RunAsync(CoreDispatcherPriority.High, () => action());
		}
	}

	private static PointerUpdateKind ToPressedKind(HeadlessPointerButton button) => button switch
	{
		HeadlessPointerButton.Left => PointerUpdateKind.LeftButtonPressed,
		HeadlessPointerButton.Middle => PointerUpdateKind.MiddleButtonPressed,
		HeadlessPointerButton.Right => PointerUpdateKind.RightButtonPressed,
		HeadlessPointerButton.XButton1 => PointerUpdateKind.XButton1Pressed,
		HeadlessPointerButton.XButton2 => PointerUpdateKind.XButton2Pressed,
		_ => PointerUpdateKind.Other,
	};

	private static PointerUpdateKind ToReleasedKind(HeadlessPointerButton button) => button switch
	{
		HeadlessPointerButton.Left => PointerUpdateKind.LeftButtonReleased,
		HeadlessPointerButton.Middle => PointerUpdateKind.MiddleButtonReleased,
		HeadlessPointerButton.Right => PointerUpdateKind.RightButtonReleased,
		HeadlessPointerButton.XButton1 => PointerUpdateKind.XButton1Released,
		HeadlessPointerButton.XButton2 => PointerUpdateKind.XButton2Released,
		_ => PointerUpdateKind.Other,
	};
}
