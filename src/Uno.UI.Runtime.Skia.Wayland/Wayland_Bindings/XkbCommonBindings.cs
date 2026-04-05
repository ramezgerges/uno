using System;
using System.Runtime.InteropServices;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal static partial class XkbCommonBindings
{
	private const string LibXkbCommon = "libxkbcommon.so.0";

	// Context
	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_context_new")]
	internal static partial IntPtr xkb_context_new(int flags);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_context_unref")]
	internal static partial void xkb_context_unref(IntPtr context);

	// Keymap
	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_keymap_new_from_string", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial IntPtr xkb_keymap_new_from_string(IntPtr context, string str, int format, int flags);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_keymap_new_from_buffer")]
	internal static partial IntPtr xkb_keymap_new_from_buffer(IntPtr context, IntPtr buffer, nuint length, int format, int flags);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_keymap_unref")]
	internal static partial void xkb_keymap_unref(IntPtr keymap);

	// State
	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_new")]
	internal static partial IntPtr xkb_state_new(IntPtr keymap);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_unref")]
	internal static partial void xkb_state_unref(IntPtr state);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_key_get_one_sym")]
	internal static partial uint xkb_state_key_get_one_sym(IntPtr state, uint key);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_key_get_utf8")]
	internal static partial int xkb_state_key_get_utf8(IntPtr state, uint key, IntPtr buffer, nuint size);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_update_mask")]
	internal static partial int xkb_state_update_mask(IntPtr state, uint modsDepressed, uint modsLatched, uint modsLocked, uint layoutDepressed, uint layoutLatched, uint layoutLocked);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_state_mod_name_is_active", StringMarshalling = StringMarshalling.Utf8)]
	internal static partial int xkb_state_mod_name_is_active(IntPtr state, string name, uint type);

	[LibraryImport(LibXkbCommon, EntryPoint = "xkb_keysym_to_utf32")]
	internal static partial uint xkb_keysym_to_utf32(uint keysym);

	// Constants
	internal const int XKB_CONTEXT_NO_FLAGS = 0;
	internal const int XKB_KEYMAP_FORMAT_TEXT_V1 = 1;
	internal const int XKB_KEYMAP_COMPILE_NO_FLAGS = 0;
	internal const uint XKB_STATE_MODS_EFFECTIVE = 8;

	// Modifier names
	internal const string XKB_MOD_NAME_SHIFT = "Shift";
	internal const string XKB_MOD_NAME_CAPS = "Lock";
	internal const string XKB_MOD_NAME_CTRL = "Control";
	internal const string XKB_MOD_NAME_ALT = "Mod1";
	internal const string XKB_MOD_NAME_NUM = "Mod2";
	internal const string XKB_MOD_NAME_LOGO = "Mod4";
}
