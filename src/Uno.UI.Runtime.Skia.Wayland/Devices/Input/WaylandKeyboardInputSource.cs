using System;
using System.Runtime.InteropServices;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.Wayland;

#pragma warning disable CA1806 // Do not ignore method results
internal partial class WaylandKeyboardInputSource : IUnoKeyboardInputSource
{
	public event TypedEventHandler<object, KeyEventArgs>? KeyDown;
	public event TypedEventHandler<object, KeyEventArgs>? KeyUp;

	// Wayland key state
	internal const uint WL_KEYBOARD_KEY_STATE_PRESSED = 1;

	private readonly WaylandXamlRootHost _host;
	private IntPtr _xkbContext;
	private IntPtr _xkbKeymap;
	private IntPtr _xkbState;

	public WaylandKeyboardInputSource(IXamlRootHost host)
	{
		if (host is not WaylandXamlRootHost)
		{
			throw new ArgumentException($"{nameof(host)} must be a Wayland host instance");
		}

		_host = (WaylandXamlRootHost)host;
		_host.SetKeyboardSource(this);

		_xkbContext = XkbCommonBindings.xkb_context_new(XkbCommonBindings.XKB_CONTEXT_NO_FLAGS);
	}

	internal void ProcessKeymapEvent(int fd, uint size)
	{
		IntPtr mapPtr = IntPtr.Zero;
		try
		{
			mapPtr = NativeMemory_mmap(IntPtr.Zero, (nuint)size, 1 /* PROT_READ */, 2 /* MAP_PRIVATE */, fd, 0);
			if (mapPtr == IntPtr.Zero || mapPtr == new IntPtr(-1))
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error("Failed to mmap keymap from compositor.");
				}
				return;
			}

			// Clean up previous keymap/state
			if (_xkbState != IntPtr.Zero)
			{
				XkbCommonBindings.xkb_state_unref(_xkbState);
				_xkbState = IntPtr.Zero;
			}
			if (_xkbKeymap != IntPtr.Zero)
			{
				XkbCommonBindings.xkb_keymap_unref(_xkbKeymap);
				_xkbKeymap = IntPtr.Zero;
			}

			_xkbKeymap = XkbCommonBindings.xkb_keymap_new_from_buffer(
				_xkbContext,
				mapPtr,
				(nuint)(size - 1), // exclude null terminator
				XkbCommonBindings.XKB_KEYMAP_FORMAT_TEXT_V1,
				XkbCommonBindings.XKB_KEYMAP_COMPILE_NO_FLAGS);

			if (_xkbKeymap != IntPtr.Zero)
			{
				_xkbState = XkbCommonBindings.xkb_state_new(_xkbKeymap);
			}
			else
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error("Failed to create xkb keymap from buffer.");
				}
			}
		}
		finally
		{
			if (mapPtr != IntPtr.Zero && mapPtr != new IntPtr(-1))
			{
				_ = NativeMemory_munmap(mapPtr, (nuint)size);
			}
		}
	}

	internal void ProcessKeyEvent(uint key, uint state, uint serial)
	{
		if (_xkbState == IntPtr.Zero)
		{
			return;
		}

		// Wayland sends evdev keycodes. xkbcommon expects evdev keycode + 8.
		var xkbKeycode = key + 8;
		var keysym = XkbCommonBindings.xkb_state_key_get_one_sym(_xkbState, xkbKeycode);
		var virtualKey = WaylandKeyTransform.VirtualKeyFromKeySym(keysym);

		// Get Unicode character
		char? unicodeKey = null;
		var utf32 = XkbCommonBindings.xkb_keysym_to_utf32(keysym);
		if (utf32 != 0 && !char.IsControl((char)utf32))
		{
			unicodeKey = (char)utf32;
		}
		else if (utf32 == '\r')
		{
			unicodeKey = '\r';
		}

		var modifiers = GetCurrentModifiers();
		var pressed = state == WL_KEYBOARD_KEY_STATE_PRESSED;

		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"ProcessKeyEvent pressed={pressed}: evdev={key} xkb={xkbKeycode} keysym=0x{keysym:X} -> {virtualKey}");
		}

		var args = new KeyEventArgs(
			"keyboard",
			virtualKey,
			modifiers,
			new CorePhysicalKeyStatus
			{
				ScanCode = key,
				RepeatCount = 1,
			},
			unicodeKey: unicodeKey);

		WaylandXamlRootHost.QueueAction(_host, () =>
		{
			if (pressed)
			{
				KeyDown?.Invoke(this, args);
			}
			else
			{
				KeyUp?.Invoke(this, args);
			}
		});
	}

	internal void ProcessModifiers(uint depressed, uint latched, uint locked, uint group)
	{
		if (_xkbState != IntPtr.Zero)
		{
			_ = XkbCommonBindings.xkb_state_update_mask(_xkbState, depressed, latched, locked, 0, 0, group);
		}
	}

	private VirtualKeyModifiers GetCurrentModifiers()
	{
		if (_xkbState == IntPtr.Zero)
		{
			return VirtualKeyModifiers.None;
		}

		var modifiers = VirtualKeyModifiers.None;

		if (XkbCommonBindings.xkb_state_mod_name_is_active(
			_xkbState, XkbCommonBindings.XKB_MOD_NAME_SHIFT, XkbCommonBindings.XKB_STATE_MODS_EFFECTIVE) != 0)
		{
			modifiers |= VirtualKeyModifiers.Shift;
		}

		if (XkbCommonBindings.xkb_state_mod_name_is_active(
			_xkbState, XkbCommonBindings.XKB_MOD_NAME_CTRL, XkbCommonBindings.XKB_STATE_MODS_EFFECTIVE) != 0)
		{
			modifiers |= VirtualKeyModifiers.Control;
		}

		if (XkbCommonBindings.xkb_state_mod_name_is_active(
			_xkbState, XkbCommonBindings.XKB_MOD_NAME_ALT, XkbCommonBindings.XKB_STATE_MODS_EFFECTIVE) != 0)
		{
			modifiers |= VirtualKeyModifiers.Menu;
		}

		if (XkbCommonBindings.xkb_state_mod_name_is_active(
			_xkbState, XkbCommonBindings.XKB_MOD_NAME_LOGO, XkbCommonBindings.XKB_STATE_MODS_EFFECTIVE) != 0)
		{
			modifiers |= VirtualKeyModifiers.Windows;
		}

		return modifiers;
	}

	[LibraryImport("libc", EntryPoint = "mmap")]
	private static partial IntPtr NativeMemory_mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

	[LibraryImport("libc", EntryPoint = "munmap")]
	private static partial int NativeMemory_munmap(IntPtr addr, nuint length);
}
