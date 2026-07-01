#nullable enable

using System.Collections.Generic;
using SkiaSharp;
using Uno.UI.Composition;

using Color = global::Windows.UI.Color;

namespace Microsoft.UI.Composition
{
	public partial class SpriteVisual : ContainerVisual
	{
		internal override void Paint(in PaintingSession session) => Brush?.Paint(session.Canvas, session.Opacity, new SKRect(left: 0, top: 0, right: Size.X, bottom: Size.Y));

		// EXPERIMENTAL WebGPU path.
		internal override void PaintWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity)
		{
			if (Size.X <= 0 || Size.Y <= 0)
			{
				return;
			}

			var size = new System.Numerics.Vector2(Size.X, Size.Y);
			var clip = new System.Numerics.Vector4(clipInRoot.Left, clipInRoot.Top, clipInRoot.Right, clipInRoot.Bottom);

			WebGpuBrushPainter.FillRect(draw, Brush, TotalMatrix, System.Numerics.Vector2.Zero, size, System.Numerics.Vector4.Zero, opacity, clip);
		}

		internal override bool CanPaint() => Brush?.CanPaint() ?? false;

		// Paint draws the Brush across (0, 0, Size.X, Size.Y) — by construction it doesn't go outside.
		internal override bool PaintsWithinOwnSize => true;

		internal override bool RequiresRepaintOnEveryFrame => Brush?.RequiresRepaintOnEveryFrame ?? false;

		private protected override bool TryAddShadowPaths(List<(SKPath path, float alpha)> output)
		{
			// SpriteVisual fills its bounds with its Brush. Only solid-color brushes are describable
			// analytically
			if (Brush is null)
			{
				return true;
			}
			if (Brush is not CompositionColorBrush color)
			{
				return false;
			}
			if (color.Color.A == 0 || Size.X <= 0 || Size.Y <= 0)
			{
				return true;
			}

			var path = new SKPath();
			path.AddRect(new SKRect(0, 0, Size.X, Size.Y));
			output.Add((path, color.Color.A / 255f));
			return true;
		}
	}
}
