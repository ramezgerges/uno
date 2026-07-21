#nullable enable

using System;
using System.Collections.Generic;
using SkiaSharp;
using Silk.NET.WebGPU;
using Texture = Silk.NET.WebGPU.Texture;
using TextureView = Silk.NET.WebGPU.TextureView;

namespace Common;

/// <summary>
/// EXPERIMENTAL glyph atlas (opt-in: UNO_WEBGPU_GLYPHATLAS=1). Each unique glyph — keyed by (font, glyph id,
/// quantized device scale) — is rasterized ONCE by Skia into an R8 coverage tile; text then draws as batched
/// textured quads that sample the tile, instead of an even-odd stencil fan per glyph per frame. The fanning
/// overdraw (one triangle per outline edge, all overlapping) collapses to a single quad, and text no longer
/// needs MSAA — the atlas coverage carries the anti-aliasing.
///
/// Allocation is a simple shelf packer over a fixed page. Raster + allocation happen on the build (UI) thread
/// inside <see cref="GetOrAdd"/> (guarded by a lock, since the atlas is shared with the render thread); the GPU
/// upload of newly-added tiles happens on the render thread in <see cref="Flush"/>. Tiles are keyed and never
/// individually evicted — when the page fills, <see cref="GetOrAdd"/> returns a miss (caller keeps the fan path).
/// </summary>
public sealed unsafe class GlyphAtlas : IDisposable
{
	public const int Size = 2048;
	private const int Pad = 1; // transparent gutter so bilinear taps at a tile edge don't bleed a neighbour

	public readonly record struct Tile(float U0, float V0, float U1, float V1, float LocalL, float LocalT, float LocalR, float LocalB, bool Empty);

	private readonly object _lock = new();
	private readonly Dictionary<(nint font, ushort glyph, int scaleQ), Tile> _tiles = new();
	private readonly List<(int x, int y, int w, int h, byte[] bytes)> _pending = new();

	private int _shelfX, _shelfY, _shelfH; // shelf packer cursor
	private bool _full;

	/// <summary>The page has no room left — callers should fall back to the stencil-fan path to keep text visible.</summary>
	public bool Full => _full;

	private WebGPU? _wgpu;
	private Texture* _tex;
	private TextureView* _view;

	/// <summary>Quantized scale bucket for keying (8 steps/unit) — different device sizes get distinct tiles.</summary>
	public static int ScaleQ(float scale) => Math.Max(1, (int)MathF.Round(scale * 8f));

	/// <summary>Look up (or rasterize + pack) the glyph tile. Returns false on a page-full miss or non-drawable size.</summary>
	public bool GetOrAdd(SKFont font, ushort glyph, float scale, out Tile tile)
	{
		int sQ = ScaleQ(scale);
		var key = ((nint)font.Handle, glyph, sQ);
		lock (_lock)
		{
			if (_tiles.TryGetValue(key, out tile))
			{
				return !tile.Empty;
			}
			bool ok = Raster(font, glyph, sQ / 8f, out tile);
			_tiles[key] = tile; // cache the miss too (Empty) so we don't re-raster whitespace/full pages
			return ok && !tile.Empty;
		}
	}

	private bool Raster(SKFont font, ushort glyph, float s, out Tile tile)
	{
		tile = new Tile(0, 0, 0, 0, 0, 0, 0, 0, true);
		using var path = font.GetGlyphPath(glyph);
		if (path is null || path.IsEmpty)
		{
			return false; // whitespace / no outline
		}
		var b = path.TightBounds; // glyph-local (font px)
		int w = (int)MathF.Ceiling(b.Width * s) + 2 * Pad;
		int h = (int)MathF.Ceiling(b.Height * s) + 2 * Pad;
		if (w <= 2 * Pad || h <= 2 * Pad || w > Size || h > Size)
		{
			return false;
		}

		int x, y;
		lock (_lock) // packer cursor is shared state (this method already runs under _lock, but keep intent explicit)
		{
			if (_shelfX + w > Size) { _shelfX = 0; _shelfY += _shelfH; _shelfH = 0; } // next shelf
			if (_shelfY + h > Size) { _full = true; return false; }                    // page full
			x = _shelfX; y = _shelfY;
			_shelfX += w; if (h > _shelfH) { _shelfH = h; }
		}

		// Rasterize the glyph coverage into an Alpha8 bitmap: translate so the padded ink box starts at (Pad,Pad).
		var bytes = new byte[w * h];
		using (var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul)))
		{
			using (var canvas = new SKCanvas(bmp))
			using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill })
			{
				canvas.Clear(SKColors.Transparent);
				canvas.Translate(Pad - b.Left * s, Pad - b.Top * s);
				canvas.Scale(s, s);
				canvas.DrawPath(path, paint);
			}
			var span = bmp.GetPixelSpan();
			// SKBitmap rows may be padded (RowBytes); copy row by row into a tight w×h buffer.
			int rb = bmp.RowBytes;
			for (int row = 0; row < h; row++)
			{
				span.Slice(row * rb, w).CopyTo(bytes.AsSpan(row * w));
			}
		}
		_pending.Add((x, y, w, h, bytes));

		// The bitmap top-left corresponds to glyph-local (font px) (b.Left - Pad/s, b.Top - Pad/s); the quad the
		// caller draws spans that local rect, positioned by the visual matrix. UVs are the tile within the page.
		float ll = b.Left - Pad / s, lt = b.Top - Pad / s;
		tile = new Tile(
			x / (float)Size, y / (float)Size, (x + w) / (float)Size, (y + h) / (float)Size,
			ll, lt, ll + w / s, lt + h / s, false);
		return true;
	}

	/// <summary>Render-thread: create the page if needed and upload any tiles added since the last flush.</summary>
	public TextureView* Flush(WebGpuContext ctx)
	{
		lock (_lock)
		{
			if (_tex is null)
			{
				_wgpu = ctx.Wgpu;
				var td = new TextureDescriptor
				{
					Size = new Extent3D(Size, Size, 1), Format = TextureFormat.R8Unorm, MipLevelCount = 1, SampleCount = 1,
					Dimension = TextureDimension.Dimension2D, Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
				};
				_tex = _wgpu.DeviceCreateTexture(ctx.Device, ref td);
				_view = _wgpu.TextureCreateView(_tex, null);
			}
			var queue = ctx.GpuQueue;
			foreach (var (x, y, w, h, bytes) in _pending)
			{
				var dst = new ImageCopyTexture { Texture = _tex, Aspect = TextureAspect.All, MipLevel = 0, Origin = new Origin3D((uint)x, (uint)y, 0) };
				var layout = new TextureDataLayout { BytesPerRow = (uint)w, RowsPerImage = (uint)h };
				fixed (byte* p = bytes)
				{
					queue.WriteTexture(dst, p, (nuint)bytes.Length, layout, new Extent3D((uint)w, (uint)h, 1));
				}
			}
			_pending.Clear();
			return _view;
		}
	}

	public void Dispose()
	{
		if (_view is not null && _wgpu is not null) { _wgpu.TextureViewRelease(_view); _view = null; }
		if (_tex is not null && _wgpu is not null) { _wgpu.TextureRelease(_tex); _tex = null; }
	}
}
