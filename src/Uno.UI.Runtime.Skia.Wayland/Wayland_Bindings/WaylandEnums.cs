using System;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal enum WlShmFormat : uint
{
	ARGB8888 = 0,
	XRGB8888 = 1,
}

[Flags]
internal enum WlSeatCapability : uint
{
	None = 0,
	Pointer = 1,
	Keyboard = 2,
	Touch = 4,
}

internal enum WlPointerButtonState : uint
{
	Released = 0,
	Pressed = 1,
}

internal enum WlKeyboardKeyState : uint
{
	Released = 0,
	Pressed = 1,
}

internal enum WlKeyboardKeymapFormat : uint
{
	NoKeymap = 0,
	XkbV1 = 1,
}

internal enum WlOutputTransform : int
{
	Normal = 0,
	Rotated90 = 1,
	Rotated180 = 2,
	Rotated270 = 3,
	Flipped = 4,
	FlippedRotated90 = 5,
	FlippedRotated180 = 6,
	FlippedRotated270 = 7,
}

internal enum WlDataDeviceManagerDndAction : uint
{
	None = 0,
	Copy = 1,
	Move = 2,
	Ask = 4,
}
