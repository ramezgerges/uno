using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.Graphics.Display;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandDisplayInformationExtension : IDisplayInformationExtension
{
	private const string EnvironmentUnoDisplayScaleOverride = "UNO_DISPLAY_SCALE_OVERRIDE";

	private static readonly List<WeakReference<WaylandDisplayInformationExtension>> _instances = new();

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

		lock (_instances)
		{
			_instances.Add(new WeakReference<WaylandDisplayInformationExtension>(this));
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

	/// <summary>
	/// Updates the scale factor on all tracked instances from a wl_output scale event.
	/// </summary>
	internal static void SetOutputScale(double scale)
	{
		lock (_instances)
		{
			for (var i = _instances.Count - 1; i >= 0; i--)
			{
				if (_instances[i].TryGetTarget(out var instance))
				{
					instance.UpdateScale(scale);
				}
				else
				{
					_instances.RemoveAt(i);
				}
			}
		}
	}
}
