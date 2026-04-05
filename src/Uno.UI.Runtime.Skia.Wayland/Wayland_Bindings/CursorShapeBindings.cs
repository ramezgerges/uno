namespace Uno.WinUI.Runtime.Skia.Wayland;

internal enum WpCursorShapeDeviceV1Shape : uint
{
	Default = 1,
	ContextMenu = 2,
	Help = 3,
	Pointer = 4,
	Progress = 5,
	Wait = 6,
	Cell = 7,
	Crosshair = 8,
	Text = 9,
	VerticalText = 10,
	Alias = 11,
	Copy = 12,
	Move = 13,
	NoDrop = 14,
	NotAllowed = 15,
	Grab = 16,
	Grabbing = 17,
	EResize = 18,
	NResize = 19,
	NEResize = 20,
	NWResize = 21,
	SResize = 22,
	SEResize = 23,
	SWResize = 24,
	WResize = 25,
	EWResize = 26,
	NSResize = 27,
	NESWResize = 28,
	NWSEResize = 29,
	ColResize = 30,
	RowResize = 31,
	AllScroll = 32,
	ZoomIn = 33,
	ZoomOut = 34,
}

internal static class CursorShape
{
	internal const uint WP_CURSOR_SHAPE_MANAGER_V1_DESTROY = 0;
	internal const uint WP_CURSOR_SHAPE_MANAGER_V1_GET_POINTER = 1;
	internal const uint WP_CURSOR_SHAPE_DEVICE_V1_DESTROY = 0;
	internal const uint WP_CURSOR_SHAPE_DEVICE_V1_SET_SHAPE = 1;
}
