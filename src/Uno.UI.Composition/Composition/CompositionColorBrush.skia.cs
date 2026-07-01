using System;
using System.Collections.Generic;
using SkiaSharp;
using Uno.Disposables;
using Windows.UI;

namespace Microsoft.UI.Composition
{
	public partial class CompositionColorBrush
	{
		// We don't call SKPaint.Reset() after usage, so make sure
		// that only SKPaint.Color is being set
		private static readonly SKPaint _tempPaint = new() { IsAntialias = true };

		internal override void Paint(SKCanvas canvas, float opacity, SKRect bounds)
		{
			_tempPaint.Color = Color.ToSKColor(opacity);
			canvas.DrawRect(bounds, _tempPaint);
		}

		// A zero-alpha brush paints nothing regardless of its RGB. Testing alpha (rather than equality with the
		// single Colors.Transparent constant, #00FFFFFF) also treats #00000000 — the common hit-test fill on a
		// Grid/Border — as non-painting, which matters because a transparent fill counted as a painting layer
		// forces a needless offscreen opacity layer around e.g. every FontIcon.
		internal override bool CanPaint() => Color.A != 0;
	}
}
