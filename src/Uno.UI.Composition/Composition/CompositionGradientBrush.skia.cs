#nullable enable

using System.Numerics;
using SkiaSharp;

namespace Microsoft.UI.Composition
{
	public partial class CompositionGradientBrush
	{
		private static readonly SKPaint _tempPaint = new();
		private bool _isColorStopsValid;

		private SKColor[]? _colors;
		private float[]? _colorPositions;
		private SKShaderTileMode _tileMode;

		private protected SKColor[]? Colors => _colors;
		private protected float[]? ColorPositions => _colorPositions;
		private protected SKShaderTileMode TileMode => _tileMode;

		internal override bool CanPaint() => true;

		internal override void Paint(SKCanvas canvas, float opacity, SKRect bounds)
		{
			if (!_isColorStopsValid)
			{
				UpdateColorStops(ColorStops);
			}
			var (shader, color) = GetPaintingParameters(bounds);
			_tempPaint.Reset();
			_tempPaint.IsAntialias = true;
			_tempPaint.Shader = shader;
			_tempPaint.Color = color;
			_tempPaint.ColorFilter = opacity.ToColorFilter();
			canvas.DrawRect(bounds, _tempPaint);
		}

		private protected virtual (SKShader? shader, SKColor color) GetPaintingParameters(SKRect bounds) => (null, SKColors.Transparent);

		private protected SKMatrix CreateTransformMatrix(SKRect bounds)
		{
			var transform = SKMatrix.Identity;

			// Translate to origin
			if (CenterPoint != Vector2.Zero)
			{
				transform = SKMatrix.CreateTranslation(-CenterPoint.X, -CenterPoint.Y);
			}

			// Scaling
			if (Scale != Vector2.One)
			{
				transform = transform.PostConcat(SKMatrix.CreateScale(Scale.X, Scale.Y));
			}

			// Rotating
			if (RotationAngle != 0)
			{
				transform = transform.PostConcat(SKMatrix.CreateRotation(RotationAngle));
			}

			// Translating
			if (Offset != Vector2.Zero)
			{
				transform = transform.PostConcat(SKMatrix.CreateTranslation(Offset.X, Offset.Y));
			}

			// Translate back
			if (CenterPoint != Vector2.Zero)
			{
				transform = transform.PostConcat(SKMatrix.CreateTranslation(CenterPoint.X, CenterPoint.Y));
			}

			if (!TransformMatrix.IsIdentity)
			{
				transform = transform.PostConcat(TransformMatrix.ToSKMatrix());
			}

			var relativeTransform = RelativeTransformMatrix.IsIdentity ? SKMatrix.Identity : RelativeTransformMatrix.ToSKMatrix();
			if (!relativeTransform.IsIdentity)
			{
				relativeTransform.TransX *= bounds.Width;
				relativeTransform.TransY *= bounds.Height;

				transform = transform.PostConcat(relativeTransform);
			}

			return transform;
		}

		// Applies the brush's transform (Scale/Rotation/Offset + TransformMatrix + RelativeTransform) to a linear
		// gradient's endpoints — the WebGPU path needs the transformed device-space axis, the same way Skia sets
		// this matrix as the shader's local matrix. <paramref name="bounds"/> is the fill region size (used to
		// scale the relative transform's translation, matching CreateTransformMatrix).
		internal void TransformGradientPoints(System.Numerics.Vector2 bounds, ref System.Numerics.Vector2 start, ref System.Numerics.Vector2 end)
		{
			var m = CreateTransformMatrix(new SKRect(0, 0, bounds.X, bounds.Y));
			if (m.IsIdentity)
			{
				return;
			}
			var s = m.MapPoint(start.X, start.Y);
			var e = m.MapPoint(end.X, end.Y);
			start = new System.Numerics.Vector2(s.X, s.Y);
			end = new System.Numerics.Vector2(e.X, e.Y);
		}

		// Radial counterpart of TransformGradientPoints: applies the same CreateTransformMatrix pipeline that Skia
		// sets as the shader's local matrix. Center/origin are mapped as points and the radii are scaled by the
		// matrix's per-axis magnitude — exact for translate/scale/flip (the common cases), approximate for a rotated
		// or sheared ellipse (the WebGPU radial shader evaluates an axis-aligned ellipse).
		internal void TransformRadialGradient(System.Numerics.Vector2 bounds, ref System.Numerics.Vector2 center, ref System.Numerics.Vector2 radius, ref System.Numerics.Vector2 origin)
		{
			var m = CreateTransformMatrix(new SKRect(0, 0, bounds.X, bounds.Y));
			if (m.IsIdentity)
			{
				return;
			}
			var c = m.MapPoint(center.X, center.Y);
			var o = m.MapPoint(origin.X, origin.Y);
			center = new System.Numerics.Vector2(c.X, c.Y);
			origin = new System.Numerics.Vector2(o.X, o.Y);
			float sx = System.MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
			float sy = System.MathF.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY);
			radius = new System.Numerics.Vector2(radius.X * sx, radius.Y * sy);
		}

		private void UpdateColorStops(CompositionColorGradientStopCollection colorStops)
		{
			var stopCount = colorStops.Count;
			var colors = _colors;
			var colorPositions = _colorPositions;

			if (colors == null || colors.Length != stopCount)
			{
				colors = new SKColor[stopCount];
				colorPositions = new float[stopCount];
			}

			for (int i = 0; i < colorStops.Count; i++)
			{
				var gradientStop = colorStops[i];

				colors[i] = gradientStop.Color.ToSKColor();
				colorPositions![i] = gradientStop.Offset;
			}

			_colors = colors;
			_colorPositions = colorPositions;
			_isColorStopsValid = true;
		}

		partial void OnColorStopsChanged(CompositionColorGradientStopCollection colorStops) => _isColorStopsValid = false;

		partial void OnExtendModeChanged(CompositionGradientExtendMode extendMode)
		{
			SKShaderTileMode tileMode;
			switch (extendMode)
			{
				default:
				case CompositionGradientExtendMode.Clamp:
					tileMode = SKShaderTileMode.Clamp;
					break;
				case CompositionGradientExtendMode.Mirror:
					tileMode = SKShaderTileMode.Mirror;
					break;
				case CompositionGradientExtendMode.Wrap:
					tileMode = SKShaderTileMode.Repeat;
					break;
			}

			_tileMode = tileMode;
		}
	}
}
