using SkiaSharp;
using Windows.Foundation;

namespace Microsoft.UI.Composition;

partial class InsetClip
{
	private (Rect? bounds, SKPath path)? _clipPath;

	private protected override bool GetBoundsCore(Visual visual, out Rect bounds)
	{
		bounds = new Rect(
			x: LeftInset,
			y: TopInset,
			width: visual.Size.X - LeftInset - RightInset,
			height: visual.Size.Y - TopInset - BottomInset);
		return true;
	}

	internal override SKPath GetClipPath(Visual visual)
	{
		if (!GetBounds(visual, out var bounds))
		{
			return null;
		}
		if (_clipPath is null || _clipPath.Value.bounds != bounds)
		{
			var path = new SKPath();
			var rect = bounds.ToSKRect();
			path.AddRect(rect);
			_clipPath = (bounds, path);
		}
		return _clipPath.Value.path;
	}

	private protected override bool GetClipRect(Visual visual, out SKRect rect)
	{
		if (GetBounds(visual, out var bounds))
		{
			rect = bounds.ToSKRect();
			return true;
		}
		else
		{
			rect = default;
			return false;
		}
	}
}
