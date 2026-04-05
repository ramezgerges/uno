using System.Collections.Generic;
using Windows.System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static class WaylandKeyTransform
{
	// XKB keysyms use the same values as X11 keysyms.
	// See /usr/include/xkbcommon/xkbcommon-keysyms.h
	private static readonly Dictionary<uint, VirtualKey> s_keyFromKeySym = new()
	{
		{ 0xff08, VirtualKey.Back },          // XKB_KEY_BackSpace
		{ 0xff09, VirtualKey.Tab },           // XKB_KEY_Tab
		{ 0xfe20, VirtualKey.Tab },           // XKB_KEY_ISO_Left_Tab
		{ 0xff0b, VirtualKey.Clear },         // XKB_KEY_Clear
		{ 0xff0d, VirtualKey.Enter },         // XKB_KEY_Return
		{ 0xff8d, VirtualKey.Enter },         // XKB_KEY_KP_Enter
		{ 0xff13, VirtualKey.Pause },         // XKB_KEY_Pause
		{ 0xff14, VirtualKey.Scroll },        // XKB_KEY_Scroll_Lock
		{ 0xffe5, VirtualKey.CapitalLock },   // XKB_KEY_Caps_Lock
		{ 0xff1b, VirtualKey.Escape },        // XKB_KEY_Escape
		{ 0x0020, VirtualKey.Space },         // XKB_KEY_space
		{ 0xff55, VirtualKey.PageUp },        // XKB_KEY_Page_Up
		{ 0xff9a, VirtualKey.PageUp },        // XKB_KEY_KP_Page_Up
		{ 0xff56, VirtualKey.PageDown },      // XKB_KEY_Page_Down
		{ 0xff9b, VirtualKey.PageDown },      // XKB_KEY_KP_Page_Down
		{ 0xff57, VirtualKey.End },           // XKB_KEY_End
		{ 0xff9c, VirtualKey.End },           // XKB_KEY_KP_End
		{ 0xff50, VirtualKey.Home },          // XKB_KEY_Home
		{ 0xff95, VirtualKey.Home },          // XKB_KEY_KP_Home
		{ 0xff51, VirtualKey.Left },          // XKB_KEY_Left
		{ 0xff96, VirtualKey.Left },          // XKB_KEY_KP_Left
		{ 0xff52, VirtualKey.Up },            // XKB_KEY_Up
		{ 0xff97, VirtualKey.Up },            // XKB_KEY_KP_Up
		{ 0xff53, VirtualKey.Right },         // XKB_KEY_Right
		{ 0xff98, VirtualKey.Right },         // XKB_KEY_KP_Right
		{ 0xff54, VirtualKey.Down },          // XKB_KEY_Down
		{ 0xff99, VirtualKey.Down },          // XKB_KEY_KP_Down
		{ 0xff60, VirtualKey.Select },        // XKB_KEY_Select
		{ 0xff61, VirtualKey.Print },         // XKB_KEY_Print
		{ 0xff62, VirtualKey.Execute },       // XKB_KEY_Execute
		{ 0xff63, VirtualKey.Insert },        // XKB_KEY_Insert
		{ 0xff9e, VirtualKey.Insert },        // XKB_KEY_KP_Insert
		{ 0xffff, VirtualKey.Delete },        // XKB_KEY_Delete
		{ 0xff9f, VirtualKey.Delete },        // XKB_KEY_KP_Delete
		{ 0xff6a, VirtualKey.Help },          // XKB_KEY_Help
		{ 0xff7f, VirtualKey.NumberKeyLock }, // XKB_KEY_Num_Lock
		{ 0xff69, VirtualKey.Cancel },        // XKB_KEY_Cancel

		// Letters (uppercase)
		{ 0x0041, VirtualKey.A },
		{ 0x0042, VirtualKey.B },
		{ 0x0043, VirtualKey.C },
		{ 0x0044, VirtualKey.D },
		{ 0x0045, VirtualKey.E },
		{ 0x0046, VirtualKey.F },
		{ 0x0047, VirtualKey.G },
		{ 0x0048, VirtualKey.H },
		{ 0x0049, VirtualKey.I },
		{ 0x004a, VirtualKey.J },
		{ 0x004b, VirtualKey.K },
		{ 0x004c, VirtualKey.L },
		{ 0x004d, VirtualKey.M },
		{ 0x004e, VirtualKey.N },
		{ 0x004f, VirtualKey.O },
		{ 0x0050, VirtualKey.P },
		{ 0x0051, VirtualKey.Q },
		{ 0x0052, VirtualKey.R },
		{ 0x0053, VirtualKey.S },
		{ 0x0054, VirtualKey.T },
		{ 0x0055, VirtualKey.U },
		{ 0x0056, VirtualKey.V },
		{ 0x0057, VirtualKey.W },
		{ 0x0058, VirtualKey.X },
		{ 0x0059, VirtualKey.Y },
		{ 0x005a, VirtualKey.Z },

		// Letters (lowercase)
		{ 0x0061, VirtualKey.A },
		{ 0x0062, VirtualKey.B },
		{ 0x0063, VirtualKey.C },
		{ 0x0064, VirtualKey.D },
		{ 0x0065, VirtualKey.E },
		{ 0x0066, VirtualKey.F },
		{ 0x0067, VirtualKey.G },
		{ 0x0068, VirtualKey.H },
		{ 0x0069, VirtualKey.I },
		{ 0x006a, VirtualKey.J },
		{ 0x006b, VirtualKey.K },
		{ 0x006c, VirtualKey.L },
		{ 0x006d, VirtualKey.M },
		{ 0x006e, VirtualKey.N },
		{ 0x006f, VirtualKey.O },
		{ 0x0070, VirtualKey.P },
		{ 0x0071, VirtualKey.Q },
		{ 0x0072, VirtualKey.R },
		{ 0x0073, VirtualKey.S },
		{ 0x0074, VirtualKey.T },
		{ 0x0075, VirtualKey.U },
		{ 0x0076, VirtualKey.V },
		{ 0x0077, VirtualKey.W },
		{ 0x0078, VirtualKey.X },
		{ 0x0079, VirtualKey.Y },
		{ 0x007a, VirtualKey.Z },

		// Numbers
		{ 0x0030, VirtualKey.Number0 },
		{ 0x0031, VirtualKey.Number1 },
		{ 0x0032, VirtualKey.Number2 },
		{ 0x0033, VirtualKey.Number3 },
		{ 0x0034, VirtualKey.Number4 },
		{ 0x0035, VirtualKey.Number5 },
		{ 0x0036, VirtualKey.Number6 },
		{ 0x0037, VirtualKey.Number7 },
		{ 0x0038, VirtualKey.Number8 },
		{ 0x0039, VirtualKey.Number9 },

		// Numpad
		{ 0xffb0, VirtualKey.NumberPad0 },    // XKB_KEY_KP_0
		{ 0xffb1, VirtualKey.NumberPad1 },    // XKB_KEY_KP_1
		{ 0xffb2, VirtualKey.NumberPad2 },    // XKB_KEY_KP_2
		{ 0xffb3, VirtualKey.NumberPad3 },    // XKB_KEY_KP_3
		{ 0xffb4, VirtualKey.NumberPad4 },    // XKB_KEY_KP_4
		{ 0xffb5, VirtualKey.NumberPad5 },    // XKB_KEY_KP_5
		{ 0xffb6, VirtualKey.NumberPad6 },    // XKB_KEY_KP_6
		{ 0xffb7, VirtualKey.NumberPad7 },    // XKB_KEY_KP_7
		{ 0xffb8, VirtualKey.NumberPad8 },    // XKB_KEY_KP_8
		{ 0xffb9, VirtualKey.NumberPad9 },    // XKB_KEY_KP_9
		{ 0xffaa, VirtualKey.Multiply },      // XKB_KEY_KP_Multiply
		{ 0xffab, VirtualKey.Add },           // XKB_KEY_KP_Add
		{ 0xffad, VirtualKey.Subtract },      // XKB_KEY_KP_Subtract
		{ 0xffae, VirtualKey.Decimal },       // XKB_KEY_KP_Decimal
		{ 0xffaf, VirtualKey.Divide },        // XKB_KEY_KP_Divide

		// Function keys
		{ 0xffbe, VirtualKey.F1 },
		{ 0xffbf, VirtualKey.F2 },
		{ 0xffc0, VirtualKey.F3 },
		{ 0xffc1, VirtualKey.F4 },
		{ 0xffc2, VirtualKey.F5 },
		{ 0xffc3, VirtualKey.F6 },
		{ 0xffc4, VirtualKey.F7 },
		{ 0xffc5, VirtualKey.F8 },
		{ 0xffc6, VirtualKey.F9 },
		{ 0xffc7, VirtualKey.F10 },
		{ 0xffc8, VirtualKey.F11 },
		{ 0xffc9, VirtualKey.F12 },
		{ 0xffca, VirtualKey.F13 },
		{ 0xffcb, VirtualKey.F14 },
		{ 0xffcc, VirtualKey.F15 },
		{ 0xffcd, VirtualKey.F16 },
		{ 0xffce, VirtualKey.F17 },
		{ 0xffcf, VirtualKey.F18 },
		{ 0xffd0, VirtualKey.F19 },
		{ 0xffd1, VirtualKey.F20 },
		{ 0xffd2, VirtualKey.F21 },
		{ 0xffd3, VirtualKey.F22 },
		{ 0xffd4, VirtualKey.F23 },
		{ 0xffd5, VirtualKey.F24 },

		// Modifier keys
		{ 0xffe1, VirtualKey.LeftShift },     // XKB_KEY_Shift_L
		{ 0xffe2, VirtualKey.RightShift },    // XKB_KEY_Shift_R
		{ 0xffe3, VirtualKey.LeftControl },   // XKB_KEY_Control_L
		{ 0xffe4, VirtualKey.RightControl },  // XKB_KEY_Control_R
		{ 0xffe9, VirtualKey.LeftMenu },      // XKB_KEY_Alt_L
		{ 0xffea, VirtualKey.RightMenu },     // XKB_KEY_Alt_R
		{ 0xffeb, VirtualKey.LeftWindows },   // XKB_KEY_Super_L
		{ 0xffec, VirtualKey.RightWindows },  // XKB_KEY_Super_R
		{ 0xff67, VirtualKey.Menu },          // XKB_KEY_Menu

		// Punctuation / symbols
		{ 0x00d7, VirtualKey.Multiply },      // XKB_KEY_multiply
	};

	public static VirtualKey VirtualKeyFromKeySym(uint keySym)
		=> s_keyFromKeySym.TryGetValue(keySym, out var result) ? result : VirtualKey.None;
}
