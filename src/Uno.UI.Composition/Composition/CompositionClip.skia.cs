#nullable enable
using System;
using System.Linq;
using SkiaSharp;
using Uno.Extensions;
using Windows.Foundation;

namespace Microsoft.UI.Composition;

partial class CompositionClip
{
	/// <summary>
	/// Returns the bounds of the clip. The clip itself could be non-rectangular, e.g, rounded rectangle or path.
	/// Note that this already handles TransformMatrix
	/// </summary>
	internal bool GetBounds(Visual visual, out Rect bounds)
	{
		if (GetBoundsCore(visual, out var untransformedBounds))
		{
			bounds = TransformMatrix.Transform(untransformedBounds);
			return true;
		}
		else
		{
			bounds = default;
			return false;
		}
	}

	/// <summary>
	/// Returns the bounds of the clip. The clip itself could be non-rectangular, e.g, rounded rectangle or path.
	/// Note that implementors should not handle TransformMatrix. The result is already transformed by <see cref="GetBounds"/>.
	/// </summary>
	private protected virtual bool GetBoundsCore(Visual visual, out Rect bounds)
	{
		bounds = Rect.Empty;
		return false;
	}

	internal virtual SKPath? GetClipPath(Visual visual) => null;
	/// <summary>
	/// Optionally overridable if the clip path can be provided as a rounded rect.
	/// </summary>
	private protected virtual SKRoundRect? GetClipRoundedRect(Visual visual) => null;
	/// <summary>
	/// Optionally overridable if the clip path can be provided as a rect.
	/// </summary>
	private protected virtual bool GetClipRect(Visual visual, out SKRect rect)
	{
		rect = default;
		return false;
	}

	internal void ApplyClip(Visual visual, SKCanvas canvas)
	{
		if (GetClipRect(visual, out var clipRect))
		{
			canvas.ClipRect(clipRect, antialias: true);
		}
		else if (GetClipRoundedRect(visual) is { } roundedRect)
		{
			canvas.ClipRoundRect(roundedRect, antialias: true);
		}
		else if (GetClipPath(visual) is { } clipPath)
		{
			canvas.ClipPath(clipPath, antialias: true);
		}
	}
}
