using System;
using System.Globalization;
using Windows.Graphics.Display;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandDisplayInformationExtension : IDisplayInformationExtension
{
	private const string EnvironmentUnoDisplayScaleOverride = "UNO_DISPLAY_SCALE_OVERRIDE";

	private readonly DisplayInformation _owner;
	private double _rawPixelsPerViewPixel = 1.0;

	public WaylandDisplayInformationExtension(object owner)
	{
		_owner = (DisplayInformation)owner;

		if (float.TryParse(
			Environment.GetEnvironmentVariable(EnvironmentUnoDisplayScaleOverride),
			NumberStyles.Any,
			CultureInfo.InvariantCulture,
			out var environmentScaleOverride))
		{
			_rawPixelsPerViewPixel = environmentScaleOverride;
		}
	}

	public DisplayOrientations CurrentOrientation => DisplayOrientations.Landscape;

	public uint ScreenHeightInRawPixels => 1080;

	public uint ScreenWidthInRawPixels => 1920;

	public float LogicalDpi => (float)(_rawPixelsPerViewPixel * DisplayInformation.BaseDpi);

	public double RawPixelsPerViewPixel => _rawPixelsPerViewPixel;

	public ResolutionScale ResolutionScale => (ResolutionScale)(int)(_rawPixelsPerViewPixel * 100);

	public double? DiagonalSizeInInches => null;

	internal void UpdateScale(double scale)
	{
		if (Math.Abs(_rawPixelsPerViewPixel - scale) > 0.001)
		{
			_rawPixelsPerViewPixel = scale;
			_owner.NotifyDpiChanged();
		}
	}
}
