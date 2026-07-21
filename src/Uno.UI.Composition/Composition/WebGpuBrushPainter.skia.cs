#nullable enable

using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using Color = global::Windows.UI.Color;

namespace Microsoft.UI.Composition;

/// <summary>
/// EXPERIMENTAL WebGPU path. Central place that turns a <see cref="CompositionBrush"/> into draw
/// commands for a rectangular (optionally rounded) region. Any visual that paints a brush over a
/// rect — <see cref="SpriteVisual"/>, <see cref="BorderVisual"/> background/border, … — routes through
/// here so brush-type handling (color, gradient, acrylic, wrappers) lives in one spot rather than
/// being duplicated per visual.
/// </summary>
internal static class WebGpuBrushPainter
{
	/// <summary>
	/// Fills the local rect (<paramref name="offset"/>, <paramref name="size"/>) with optional per-corner
	/// <paramref name="radii"/> (TopLeft, TopRight, BottomRight, BottomLeft) using <paramref name="brush"/>.
	/// </summary>
	internal static void FillRect(IWebGpuDrawList draw, CompositionBrush? brush, Matrix4x4 totalMatrix,
		Vector2 offset, Vector2 size, Vector4 radii, float opacity, Vector4 clip)
	{
		switch (Unwrap(brush))
		{
			case CompositionColorBrush color:
				draw.AddRoundedRect(totalMatrix, offset, size, radii, color.Color, opacity, clip);
				break;

			case CompositionLinearGradientBrush gradient:
				FillGradient(draw, gradient, totalMatrix, offset, size, radii, opacity, clip);
				break;

			case CompositionRadialGradientBrush radial:
				FillRadialGradient(draw, radial, totalMatrix, offset, size, radii, opacity, clip);
				break;

			case SkiaAcrylicBrush acrylic:
				draw.AddAcrylic(totalMatrix, offset, size, radii,
					ToColor(acrylic.TintColor), ToColor(acrylic.LuminosityColor),
					acrylic.BlurSigma, acrylic.NoiseOpacity, acrylic.IsOpaque, opacity, clip);
				break;

			case CompositionSurfaceBrush surfaceBrush:
				// The image is a rectangle; when the region has rounded corners it must be masked to the
				// rounded shape (otherwise the image overflows the corners — e.g. a rounded Border background).
				if (radii != default)
				{
					draw.PushClipPath(totalMatrix, RoundedRectContour(offset, size, radii));
					FillSurface(draw, surfaceBrush, totalMatrix, offset, size, opacity, clip);
					draw.PopClip();
				}
				else
				{
					FillSurface(draw, surfaceBrush, totalMatrix, offset, size, opacity, clip);
				}
				break;

			case CompositionEffectBrush effect:
				FillEffect(draw, effect, totalMatrix, offset, size, radii, opacity, clip);
				break;

			case CompositionMaskBrush { Source: { } maskSource, Mask: { } maskMask }:
				// Paint the source, then mask it by the mask brush's alpha — mirrors Skia's SrcOver + DstIn layers.
				// Opacity is applied to both (as Skia does), and each is filled over the same region.
				int maskHandle = draw.BeginMask(clip);
				FillRect(draw, maskSource, totalMatrix, offset, size, radii, opacity, clip);
				draw.MaskSeparator(maskHandle);
				FillRect(draw, maskMask, totalMatrix, offset, size, radii, opacity, clip);
				draw.EndMask(maskHandle);
				break;
		}
	}

	// EXPERIMENTAL. An effect brush is reduced to a recipe (source + composed 4×5 color matrix). We render the
	// source with the matrix applied per-pixel: a solid color source → tinted rect; an image source → the image
	// with the color matrix in its shader. Backdrop sources / blur / multi-source / lighting are not handled.
	private static void FillEffect(IWebGpuDrawList draw, CompositionEffectBrush effect, Matrix4x4 totalMatrix,
		Vector2 offset, Vector2 size, Vector4 radii, float opacity, Vector4 clip)
	{
		if (!effect.TryGetWebGpuEffectRecipe(out var source, out _, out var solidColor, out var colorMatrix))
		{
			return;
		}

		if (solidColor is { } sc)
		{
			draw.AddRoundedRect(totalMatrix, offset, size, radii, Color.FromArgb(sc.A, sc.R, sc.G, sc.B), opacity, clip);
			return;
		}

		// Solid-color source: apply the color matrix on the CPU and fill the rect with the result.
		if (Unwrap(source) is CompositionColorBrush colorSource)
		{
			draw.AddRoundedRect(totalMatrix, offset, size, radii, ApplyColorMatrix(colorSource.Color, colorMatrix), opacity, clip);
			return;
		}

		if (Unwrap(source) is CompositionSurfaceBrush surface)
		{
			if (radii != default)
			{
				draw.PushClipPath(totalMatrix, RoundedRectContour(offset, size, radii));
				FillSurface(draw, surface, totalMatrix, offset, size, opacity, clip, colorMatrix);
				draw.PopClip();
			}
			else
			{
				FillSurface(draw, surface, totalMatrix, offset, size, opacity, clip, colorMatrix);
			}
		}
	}

	/// <summary>Device-space axis-aligned bounds (left, top, right, bottom) of a local rect under <paramref name="m"/>.</summary>
	private static Vector4 DeviceAabb(Matrix4x4 m, Vector2 offset, Vector2 size)
	{
		Vector2 P(float x, float y) => new(x * m.M11 + y * m.M21 + m.M41, x * m.M12 + y * m.M22 + m.M42);
		var c0 = P(offset.X, offset.Y);
		var c1 = P(offset.X + size.X, offset.Y);
		var c2 = P(offset.X, offset.Y + size.Y);
		var c3 = P(offset.X + size.X, offset.Y + size.Y);
		float l = System.MathF.Min(System.MathF.Min(c0.X, c1.X), System.MathF.Min(c2.X, c3.X));
		float t = System.MathF.Min(System.MathF.Min(c0.Y, c1.Y), System.MathF.Min(c2.Y, c3.Y));
		float r = System.MathF.Max(System.MathF.Max(c0.X, c1.X), System.MathF.Max(c2.X, c3.X));
		float b = System.MathF.Max(System.MathF.Max(c0.Y, c1.Y), System.MathF.Max(c2.Y, c3.Y));
		return new Vector4(l, t, r, b);
	}

	/// <summary>Intersection of two (left, top, right, bottom) device-space clip rects.</summary>
	private static Vector4 IntersectClip(Vector4 a, Vector4 b) => new(
		System.MathF.Max(a.X, b.X), System.MathF.Max(a.Y, b.Y),
		System.MathF.Min(a.Z, b.Z), System.MathF.Min(a.W, b.W));

	/// <summary>
	/// Builds a closed polyline contour for a rounded rect (corner arcs flattened) in local coordinates.
	/// <paramref name="radii"/> is (TopLeft, TopRight, BottomRight, BottomLeft).
	/// </summary>
	private static Vector2[][] RoundedRectContour(Vector2 offset, Vector2 size, Vector4 radii)
	{
		float l = offset.X, t = offset.Y, r = offset.X + size.X, b = offset.Y + size.Y;
		float maxR = System.MathF.Min(size.X, size.Y) * 0.5f;
		float tl = System.MathF.Min(radii.X, maxR), tr = System.MathF.Min(radii.Y, maxR);
		float br = System.MathF.Min(radii.Z, maxR), bl = System.MathF.Min(radii.W, maxR);
		const int seg = 8;
		var pts = new List<Vector2>();
		void Arc(float cx, float cy, float rad, float a0, float a1)
		{
			if (rad <= 0) { pts.Add(new Vector2(cx, cy)); return; }
			for (int i = 0; i <= seg; i++)
			{
				float a = a0 + (a1 - a0) * (i / (float)seg);
				pts.Add(new Vector2(cx + rad * System.MathF.Cos(a), cy + rad * System.MathF.Sin(a)));
			}
		}
		const float pi = System.MathF.PI;
		Arc(l + tl, t + tl, tl, pi, 1.5f * pi);       // top-left
		Arc(r - tr, t + tr, tr, 1.5f * pi, 2f * pi);  // top-right
		Arc(r - br, b - br, br, 0f, 0.5f * pi);       // bottom-right
		Arc(l + bl, b - bl, bl, 0.5f * pi, pi);       // bottom-left
		return new[] { pts.ToArray() };
	}

	private static void FillSurface(IWebGpuDrawList draw, CompositionSurfaceBrush brush, Matrix4x4 totalMatrix,
		Vector2 offset, Vector2 size, float opacity, Vector4 clip, float[]? colorMatrix = null)
	{
		if (brush.Surface is not SkiaCompositionSurface { Image: { } image })
		{
			return;
		}

		// Stretch/alignment can arrange the image LARGER than the region (e.g. Stretch=None, AlignmentX=Right
		// with an oversized image overflows up/left). The shape/rounded clip only masks within the region's
		// bbox, so scissor the image to the region in device space — it must never paint outside the region.
		clip = IntersectClip(clip, DeviceAabb(totalMatrix, offset, size));

		int iw = image.Width, ih = image.Height;
		if (iw <= 0 || ih <= 0) { return; }

		// Arranged rect (respects Stretch + alignment) within the region.
		var target = new SKRect(offset.X, offset.Y, offset.X + size.X, offset.Y + size.Y);
		var arranged = brush.GetArrangedImageRect(new global::Windows.Foundation.Size(iw, ih), target);
		if (arranged.Width <= 0 || arranged.Height <= 0) { return; }

		// Image-space → local-space transform, composed exactly as CompositionSurfaceBrush.Paint. This MUST include
		// the brush's TransformMatrix/RelativeTransform: the Image control sets Stretch=None and conveys its display
		// scale (image px → arranged px) purely through TransformMatrix, so drawing the arranged rect alone paints
		// the image at its native size and the region clip then crops it to a corner.
		var b = Matrix3x2.CreateScale((float)(arranged.Width / iw), (float)(arranged.Height / ih));
		b *= Matrix3x2.CreateTranslation((float)arranged.Left, (float)arranged.Top);
		b *= brush.TransformMatrix;
		if (Matrix3x2.Invert(Matrix3x2.CreateScale(size.X, size.Y), out var invBounds))
		{
			b *= invBounds;
			b *= brush.RelativeTransform;
			b *= Matrix3x2.CreateScale(size.X, size.Y);
		}
		var imageMatrix = new Matrix4x4(b) * totalMatrix;

		// Read the image as unpremultiplied RGBA8 (Skia used only to read pixels, not to draw).
		var pixels = new byte[iw * ih * 4];
		var info = new SKImageInfo(iw, ih, SKColorType.Rgba8888, SKAlphaType.Unpremul);
		var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), iw * 4, 0, 0)) { return; }
		}
		finally
		{
			handle.Free();
		}

		// The source SKImage instance is stable across frames, so its identity hash keys the backend's texture
		// cache — the pixels are uploaded once and the GPU texture reused, not recreated every frame.
		// MonochromeColor (used e.g. for monochrome icon surfaces) replaces RGB with a constant colour while keeping
		// the image's alpha — Skia does this with an SrcIn blend (CompositionSurfaceBrush.Paint). It maps exactly to
		// a 4×5 colour matrix (constant-colour offsets, alpha scaled by the mono alpha), reusing the image shader's
		// existing matrix path. Only applied when no effect colour matrix is already in play.
		if (colorMatrix is null && brush.MonochromeColor is { } mono)
		{
			float mr = mono.Red / 255f, mg = mono.Green / 255f, mb = mono.Blue / 255f, ma = mono.Alpha / 255f;
			colorMatrix = new float[]
			{
				0, 0, 0, 0, mr,
				0, 0, 0, 0, mg,
				0, 0, 0, 0, mb,
				0, 0, 0, ma, 0,
			};
		}

		long imageKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(image);
		draw.AddImage(imageMatrix, Vector2.Zero, new Vector2(iw, ih), pixels, iw, ih, opacity, clip, colorMatrix, imageKey);
	}

	/// <summary>
	/// Fills an arbitrary path (<paramref name="contours"/>, already flattened) with <paramref name="brush"/>.
	/// The path itself is the mask — solid for color brushes, the backdrop region for acrylic.
	/// </summary>
	internal static void FillPath(IWebGpuDrawList draw, CompositionBrush? brush, Matrix4x4 totalMatrix,
		Vector2[][] contours, float opacity, Vector4 clip)
	{
		switch (Unwrap(brush))
		{
			case CompositionColorBrush color:
				draw.AddPath(totalMatrix, contours, color.Color, opacity, clip);
				break;

			case SkiaAcrylicBrush acrylic:
				draw.AddAcrylicPath(totalMatrix, contours,
					ToColor(acrylic.TintColor), ToColor(acrylic.LuminosityColor),
					acrylic.BlurSigma, acrylic.NoiseOpacity, acrylic.IsOpaque, opacity, clip);
				break;

			// Image / gradient / effect fill of an arbitrary shape: fill over the shape's bounds, clipped to the path
			// (otherwise the rectangular fill overflows the shape — e.g. a gradient- or image-filled Ellipse).
			case CompositionSurfaceBrush:
			case CompositionLinearGradientBrush:
			case CompositionRadialGradientBrush:
			case CompositionEffectBrush:
				if (ContourBounds(contours, out var bOffset, out var bSize))
				{
					draw.PushClipPath(totalMatrix, contours);
					switch (Unwrap(brush))
					{
						case CompositionSurfaceBrush surfaceBrush:
							FillSurface(draw, surfaceBrush, totalMatrix, bOffset, bSize, opacity, clip);
							break;
						case CompositionLinearGradientBrush linear:
							FillGradient(draw, linear, totalMatrix, bOffset, bSize, default, opacity, clip);
							break;
						case CompositionRadialGradientBrush radial:
							FillRadialGradient(draw, radial, totalMatrix, bOffset, bSize, default, opacity, clip);
							break;
						case CompositionEffectBrush effect:
							FillEffect(draw, effect, totalMatrix, bOffset, bSize, default, opacity, clip);
							break;
					}
					draw.PopClip();
				}
				break;
		}
	}

	/// <summary>Local-space bounds (offset, size) of flattened contours; false if degenerate/empty.</summary>
	private static bool ContourBounds(Vector2[][] contours, out Vector2 offset, out Vector2 size)
	{
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		foreach (var ct in contours)
		{
			foreach (var p in ct)
			{
				minX = System.MathF.Min(minX, p.X); minY = System.MathF.Min(minY, p.Y);
				maxX = System.MathF.Max(maxX, p.X); maxY = System.MathF.Max(maxY, p.Y);
			}
		}
		offset = new Vector2(minX, minY);
		size = new Vector2(maxX - minX, maxY - minY);
		return maxX > minX && maxY > minY;
	}

	private static void FillGradient(IWebGpuDrawList draw, CompositionLinearGradientBrush gradient,
		Matrix4x4 totalMatrix, Vector2 offset, Vector2 size, Vector4 radii, float opacity, Vector4 clip)
	{
		var offsets = new List<float>();
		var colors = new List<Color>();
		foreach (var stop in gradient.ColorStops)
		{
			offsets.Add(stop.Offset);
			colors.Add(stop.Color);
		}
		if (offsets.Count == 0)
		{
			return;
		}

		var start = gradient.StartPoint;
		var end = gradient.EndPoint;
		if (gradient.MappingMode == CompositionMappingMode.Relative)
		{
			start = new Vector2(start.X * size.X, start.Y * size.Y);
			end = new Vector2(end.X * size.X, end.Y * size.Y);
		}
		// Apply the brush's transform (e.g. the Fluent TextBox border brush flips the gradient with a ScaleY=-1
		// RelativeTransform to put its dark accent at the bottom). Without this the gradient renders un-flipped.
		gradient.TransformGradientPoints(size, ref start, ref end);

		// AddLinearGradient spans local (0,0)-(size); bake the region offset into the matrix.
		var m = (Matrix4x4.Identity with { M41 = offset.X, M42 = offset.Y }) * totalMatrix;
		draw.AddLinearGradient(m, size, start, end, offsets.ToArray(), colors.ToArray(), opacity, clip, radii, (int)gradient.ExtendMode);
	}

	private static void FillRadialGradient(IWebGpuDrawList draw, CompositionRadialGradientBrush gradient,
		Matrix4x4 totalMatrix, Vector2 offset, Vector2 size, Vector4 radii, float opacity, Vector4 clip)
	{
		var offsets = new List<float>();
		var colors = new List<Color>();
		foreach (var stop in gradient.ColorStops)
		{
			offsets.Add(stop.Offset);
			colors.Add(stop.Color);
		}
		if (offsets.Count == 0)
		{
			return;
		}

		var center = gradient.EllipseCenter;
		var radius = gradient.EllipseRadius;
		var origin = gradient.GradientOriginOffset;
		if (gradient.MappingMode == CompositionMappingMode.Relative)
		{
			center = new Vector2(center.X * size.X, center.Y * size.Y);
			radius = new Vector2(radius.X * size.X, radius.Y * size.Y);
			origin = new Vector2(origin.X * size.X, origin.Y * size.Y);
		}

		// Apply the brush transform (Scale/Rotation/Offset/CenterPoint/TransformMatrix/RelativeTransform), the
		// same pipeline Skia folds into the radial shader's local matrix — without this radial brushes ignore it.
		gradient.TransformRadialGradient(size, ref center, ref radius, ref origin);

		// AddRadialGradient spans local (0,0)-(size); bake the region offset into the matrix.
		var m = (Matrix4x4.Identity with { M41 = offset.X, M42 = offset.Y }) * totalMatrix;
		draw.AddRadialGradient(m, size, center, radius, origin, offsets.ToArray(), colors.ToArray(), opacity, clip, radii, (int)gradient.ExtendMode);
	}

	/// <summary>
	/// Flattens an <see cref="SKPath"/> to closed polyline contours in its local coordinates (Béziers/conics
	/// subdivided — Skia is used only to read geometry, not to draw). Shared by shape fills and shadows.
	/// </summary>
	internal static Vector2[][] FlattenPath(SKPath path)
	{
		const int steps = 12;
		var contours = new List<Vector2[]>();
		List<Vector2>? cur = null;
		var pts = new SKPoint[4];
		using var it = path.CreateIterator(false);
		SKPathVerb verb;
		while ((verb = it.Next(pts)) != SKPathVerb.Done)
		{
			switch (verb)
			{
				case SKPathVerb.Move:
					if (cur is { Count: >= 2 }) { contours.Add(cur.ToArray()); }
					cur = new() { new Vector2(pts[0].X, pts[0].Y) };
					break;
				case SKPathVerb.Line:
					cur!.Add(new Vector2(pts[1].X, pts[1].Y));
					break;
				case SKPathVerb.Quad:
					for (int i = 1; i <= steps; i++)
					{
						float t = i / (float)steps, u = 1 - t;
						cur!.Add(new Vector2(u * u * pts[0].X + 2 * u * t * pts[1].X + t * t * pts[2].X, u * u * pts[0].Y + 2 * u * t * pts[1].Y + t * t * pts[2].Y));
					}
					break;
				case SKPathVerb.Conic:
					float w = it.ConicWeight();
					for (int i = 1; i <= steps; i++)
					{
						float t = i / (float)steps, u = 1 - t;
						float b0 = u * u, b1 = 2 * u * t * w, b2 = t * t, denom = b0 + b1 + b2;
						cur!.Add(new Vector2((b0 * pts[0].X + b1 * pts[1].X + b2 * pts[2].X) / denom, (b0 * pts[0].Y + b1 * pts[1].Y + b2 * pts[2].Y) / denom));
					}
					break;
				case SKPathVerb.Cubic:
					for (int i = 1; i <= steps; i++)
					{
						float t = i / (float)steps, u = 1 - t;
						cur!.Add(new Vector2(
							u * u * u * pts[0].X + 3 * u * u * t * pts[1].X + 3 * u * t * t * pts[2].X + t * t * t * pts[3].X,
							u * u * u * pts[0].Y + 3 * u * u * t * pts[1].Y + 3 * u * t * t * pts[2].Y + t * t * t * pts[3].Y));
					}
					break;
				case SKPathVerb.Close:
					if (cur is { Count: >= 2 }) { contours.Add(cur.ToArray()); }
					cur = null;
					break;
			}
		}
		if (cur is { Count: >= 2 }) { contours.Add(cur.ToArray()); }
		return contours.ToArray();
	}

	/// <summary>Brushes reaching the composition layer may be wrapped (acrylic, transition brushes, …).</summary>
	private static CompositionBrush? Unwrap(CompositionBrush? brush)
	{
		while (brush is CompositionBrushWrapper wrapper)
		{
			brush = wrapper.WrappedBrush;
		}
		return brush;
	}

	/// <summary>True (with the brush colour) when the brush resolves to a plain solid colour.</summary>
	internal static bool TryGetSolidColor(CompositionBrush? brush, out Color color)
	{
		if (Unwrap(brush) is CompositionColorBrush c) { color = c.Color; return true; }
		color = default;
		return false;
	}

	private static Color ToColor(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

	/// <summary>Applies a 4×5 (row-major) color matrix to a color (unpremultiplied, 0..1), clamped.</summary>
	private static Color ApplyColorMatrix(Color c, float[] m)
	{
		float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
		float R = System.Math.Clamp(m[0] * r + m[1] * g + m[2] * b + m[3] * a + m[4], 0, 1);
		float G = System.Math.Clamp(m[5] * r + m[6] * g + m[7] * b + m[8] * a + m[9], 0, 1);
		float B = System.Math.Clamp(m[10] * r + m[11] * g + m[12] * b + m[13] * a + m[14], 0, 1);
		float A = System.Math.Clamp(m[15] * r + m[16] * g + m[17] * b + m[18] * a + m[19], 0, 1);
		return Color.FromArgb((byte)(A * 255), (byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
	}
}
