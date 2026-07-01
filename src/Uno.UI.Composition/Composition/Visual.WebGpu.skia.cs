#nullable enable

using System.Numerics;
using SkiaSharp;
using Uno.UI.Composition;

namespace Microsoft.UI.Composition;

// EXPERIMENTAL WebGPU render path. Mirrors the structure of Visual.Render (Visual.skia.cs)
// but, instead of painting onto an SKCanvas, walks the tree reusing TotalMatrix and the
// existing clip machinery and emits abstract draw commands into an IWebGpuDrawList.
// Scoped to solid-color rectangles for now (SpriteVisual + CompositionColorBrush).
public partial class Visual
{
	// Per-thread scratch path for pre-painting clip extraction in the WebGPU walk (avoids a native SKPath
	// allocation per visual per frame). Single-threaded build, rewound per use, recursion-safe (see RenderWebGpu).
	[System.ThreadStatic] private static SKPath? _spareWebGpuClip;

	// Per-visual cached own-paint geometry for the WebGPU path (opaque CachedVisual owned by the backend draw
	// list). Mirrors the Skia _picture lifetime: replayed under the current matrix each frame, nulled by
	// InvalidatePaint when this visual's content changes. A matrix-only change (scroll) does NOT invalidate it.
	private object? _webGpuPaintCache;

	internal void InvalidateWebGpuPaintCache() => _webGpuPaintCache = null;

	// Stable per-visual id for the slab allocator (UNO_WEBGPU_SLAB): a visual's geometry keeps the same slab slice
	// across frames so a content change re-uploads only that slice. Lazily assigned on the (single-threaded) UI build.
	private static long _webGpuNextVisualId;
	private long _webGpuVisualId;
	internal long WebGpuVisualId => _webGpuVisualId != 0 ? _webGpuVisualId : (_webGpuVisualId = ++_webGpuNextVisualId);

	/// <summary>Entry point: render this visual as the root of a WebGPU frame.</summary>
	internal void RenderRootVisualWebGpu(IWebGpuDrawList draw)
	{
		RenderWebGpu(draw, InfiniteClipRect, 1.0f);
		draw.FinalizeBuild();
	}

	internal void RenderWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity)
	{
		if (this is { Opacity: 0 } or { IsVisible: false })
		{
			return;
		}

		// Opacity multiplies straight into every descendant's paint — EXACTLY like Skia's PaintingSession.Opacity
		// chain (Visual.CreateLocalSession). Skia uses no isolation layer for group opacity, so overlapping
		// translucent content double-blends; matching that (rather than compositing the subtree once through a
		// layer) is what keeps the two backends pixel-identical.
		var children = GetChildrenInRenderOrder();
		opacity *= Opacity;

		var totalMatrix = TotalMatrix.ToSKMatrix();

		// Pre-painting clip (the Clip property), in root/device space, intersected with the inherited clip.
		// Reuse a per-thread scratch SKPath instead of allocating one per visual per frame. GetPrePaintingClipping
		// Reset()s the path itself when it populates it (and returns false, leaving it untouched, when the visual has
		// no clip — the common case), so we must NOT pre-Rewind it here: the unconditional native SKPath.Rewind() per
		// visual per frame was the single largest managed cost in the build (per profiler). Safe under recursion: we
		// extract Bounds into `clip` before descending into children.
		var clip = clipInRoot;
		var localClip = _spareWebGpuClip ??= new SKPath();
		if (GetPrePaintingClipping(localClip))
		{
			localClip.Transform(in totalMatrix);
			clip = Intersect(clip, localClip.Bounds);
		}

		// Empty clip → this visual AND its whole subtree are scissored to nothing: emit no geometry, no commands,
		// no shadow, and don't even walk the children (their clip is a subset of this one, so also empty). Skips the
		// per-frame build + vertex upload + GPU draw for fully-clipped subtrees (e.g. collapsed/zero-size content).
		if (clip.Width <= 0 || clip.Height <= 0)
		{
			return;
		}

		// Post-painting clip affects children only.
		var childClip = clip;
		if (GetPostPaintingClipping() is { } postClip)
		{
			postClip.Transform(in totalMatrix);
			childClip = Intersect(childClip, postClip.Bounds);
		}

		// A drop shadow brackets this visual's whole subtree (own content + children): the bracketed
		// commands are re-rendered to an offscreen texture whose actual alpha is the shadow coverage, so
		// real spatially-varying transparency and multi-visual silhouettes are handled naturally.
		var shadow = ShadowState;
		int shadowHandle = shadow is not null
			? draw.BeginShadow(shadow.Color, shadow.Dx, shadow.Dy, shadow.SigmaX, new Vector4(clip.Left, clip.Top, clip.Right, clip.Bottom))
			: -1;

		// Non-rectangular clips become a real depth-buffer mask (not just an axis-aligned scissor): pre-painting
		// clip (the Clip property) wraps this visual + children; post-painting (corner) clip wraps children.
		bool prePushed = false;
		if (Clip is RectangleClip preRc)
		{
			prePushed = TryPushRoundedClip(draw, preRc, in totalMatrix);
		}
		else if (Clip is { } otherClip && otherClip.GetClipPath(this) is { } clipPath && !clipPath.IsRect)
		{
			// Arbitrary geometry clip → even-odd path clip (a plain rect is already handled by the scissor).
			var contours = WebGpuBrushPainter.FlattenPath(clipPath);
			if (contours.Length > 0)
			{
				draw.PushClipPath(TotalMatrix, contours);
				prePushed = true;
			}
		}

		// This visual's own content, then children. The own-paint geometry is cached per-visual (like Skia's
		// _picture): on a frame where this visual hasn't changed and only its matrix translated (scroll), the
		// cached geometry is replayed under the new transform instead of re-emitting it (notably re-walking glyph
		// outlines). Invalidated by InvalidatePaint; a non-translation matrix change or non-cacheable paint
		// re-records. Opt-in (UNO_WEBGPU_CACHE=1) inside the draw list.
		var clipVec = new Vector4(clip.Left, clip.Top, clip.Right, clip.Bottom);
		draw.BeginVisual(WebGpuVisualId);
		if (!draw.BeginCachedVisualReplay(_webGpuPaintCache, TotalMatrix, opacity, clipVec))
		{
			PaintWebGpu(draw, clip, opacity);
			_webGpuPaintCache = draw.EndCachedVisualRecord(TotalMatrix, opacity);
		}
		draw.EndVisual();

		bool postPushed = GetWebGpuPostRoundedClip() is { } post && TryPushRoundedClip(draw, post, in totalMatrix);
		foreach (var child in children)
		{
			child.RenderWebGpu(draw, childClip, opacity);
		}
		if (postPushed) { draw.PopClip(); }

		// Content painted OVER children, outside the child (corner) clip — mirrors Skia drawing e.g. a Border's
		// border ring after base.Paint(children). Stays inside this visual's own pre-clip/opacity/shadow scope.
		// Its own slab unit (a distinct id from PaintWebGpu) so any path geometry it emits is relocated too.
		draw.BeginVisual(WebGpuVisualId ^ (1L << 62));
		PaintOverChildrenWebGpu(draw, clip, opacity);
		draw.EndVisual();

		if (prePushed) { draw.PopClip(); }

		if (shadow is not null)
		{
			draw.EndShadow(shadowHandle);
		}
	}

	/// <summary>WebGPU counterpart of <see cref="Paint"/>. Default draws nothing.</summary>
	internal virtual void PaintWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity) { }

	/// <summary>WebGPU content drawn AFTER this visual's children (outside the child clip) — e.g. a Border's
	/// border ring, which Skia paints after base.Paint(children). Default draws nothing.</summary>
	internal virtual void PaintOverChildrenWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity) { }

	/// <summary>The rounded-rect clip this visual applies to its children (post-painting), or null.</summary>
	private protected virtual RectangleClip? GetWebGpuPostRoundedClip() => null;

	// Transforms a RectangleClip's rect + per-corner radii to device space and pushes a rounded clip.
	// Returns false (no push) for a non-rounded clip — the axis-aligned scissor already covers that.
	private static bool TryPushRoundedClip(IWebGpuDrawList draw, RectangleClip rc, in SKMatrix m)
	{
		float maxRadius = System.MathF.Max(System.MathF.Max(rc.TopLeftRadius.X, rc.TopRightRadius.X), System.MathF.Max(rc.BottomRightRadius.X, rc.BottomLeftRadius.X));
		if (maxRadius <= 0)
		{
			return false;
		}
		// Bake the clip's own TransformMatrix (clip-local → clip space) ahead of the visual's TotalMatrix, the way
		// Skia's CompositionClip.GetClipPath does. Identity in the common case (so no change), but honoured when set.
		var ct = rc.TransformMatrix;
		var mm = m;
		SKPoint MapClip(float x, float y)
		{
			var p = Vector2.Transform(new Vector2(x, y), ct);
			return mm.MapPoint(p.X, p.Y);
		}
		var corners = new[]
		{
			MapClip(rc.Left, rc.Top), MapClip(rc.Right, rc.Top),
			MapClip(rc.Left, rc.Bottom), MapClip(rc.Right, rc.Bottom),
		};
		float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
		foreach (var c in corners) { l = System.MathF.Min(l, c.X); t = System.MathF.Min(t, c.Y); r = System.MathF.Max(r, c.X); b = System.MathF.Max(b, c.Y); }
		float ctScale = System.MathF.Sqrt(System.MathF.Abs(ct.M11 * ct.M22 - ct.M12 * ct.M21));
		float scale = System.MathF.Sqrt(System.MathF.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY)) * ctScale;
		draw.PushClip(new Vector4(l, t, r, b),
			new Vector4(rc.TopLeftRadius.X * scale, rc.TopRightRadius.X * scale, rc.BottomRightRadius.X * scale, rc.BottomLeftRadius.X * scale));
		return true;
	}

	private static SKRect Intersect(SKRect a, SKRect b)
	{
		var r = a;
		r.Intersect(b);
		return r;
	}
}
