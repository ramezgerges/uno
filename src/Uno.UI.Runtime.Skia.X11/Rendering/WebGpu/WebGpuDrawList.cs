using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Common;
using Microsoft.UI.Composition;
using Silk.NET.WebGPU;
using Color = Windows.UI.Color;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace WebGpuExperiment;

/// <summary>
/// Consumes the abstract draw commands from Visual.RenderRootVisualWebGpu and renders them with
/// WebGPU only. Analytic: solid rects, linear gradients (256px LUT), rounded rects (SDF).
/// Arbitrary paths/glyphs: stencil-then-cover (even-odd) in the same pass when any path is present
/// (the pass then carries a Depth24PlusStencil8 attachment and all pipelines a matching state).
/// </summary>
internal sealed unsafe class WebGpuDrawList : IWebGpuDrawList
{
	private readonly uint _w;
	private readonly uint _h;
	private readonly uint _lw;
	private readonly uint _lh;
	private readonly float _scale;

	private enum Kind { Solid, Gradient, RoundedRect, PathFill, Backdrop, Shadow, Image, ClipPush, ClipPop, Layer, Mask }
	private readonly record struct Cmd(Kind Kind, int VertexStart, int Count, int Aux, uint Sx, uint Sy, uint Sw, uint Sh);
		// Perf diagnostics (UNO_RENDER_PERF): draw calls issued vs passes opened — reveals whether `encode` time is
		// draw-FFI-bound (draws ≈ cmds → coalescing not firing) or pass-bound (blur pyramid).
		private int _dbgDraws, _dbgPasses;
		private double _dbgPassMs; // time in BeginRenderPass+EndPass FFI (pass-overhead vs draw-issue split)
		private int _dbgEmptySkip; // scene segments skipped because they had no drawing commands
		private double _dbgHashMs, _dbgClipMs; private int _dbgClipN; // ContentHash time; ReestablishClip time+count
	private readonly record struct Backdrop(float Sigma, Color Luminosity, Vector4 RadiiPx, float Noise,
		bool IsPath, int FanStart, int FanCount, int CoverStart);
	private readonly List<Backdrop> _backdrops = new();
	private readonly record struct ShadowCmd(int GroupStart, int GroupEnd, Color Color, float Dx, float Dy, float Sigma);
	private readonly List<ShadowCmd> _shadows = new();
	// An opacity group: the bracketed cmd range is rendered to an offscreen texture, then composited once at
	// Opacity — so overlapping children of a translucent visual blend once, not per-primitive (no double-blend).
	private readonly record struct LayerCmd(int GroupStart, int GroupEnd, float Opacity);
	private readonly List<LayerCmd> _layers = new();

	// A CompositionMaskBrush: the SOURCE group [SourceStart, MaskStart) is painted into one offscreen, the MASK
	// group [MaskStart, MaskEnd) into another, then composited as source × mask.alpha (DstIn) over the target.
	private readonly record struct MaskCmd(int SourceStart, int MaskStart, int MaskEnd);
	private readonly List<MaskCmd> _masks = new();

	private readonly List<Cmd> _cmds = new();
	private readonly List<float> _solidVerts = new();   // 6 * (clip.xy, rgba)
	private readonly List<float> _rrVerts = new();      // 6 * (clip.xy, centered.xy, half.xy, radii4, rgba)
	private readonly List<float> _gradVerts = new();    // 6 * (clip.xy, local.xy)
	private readonly List<float> _pathVerts = new();    // fan: clip.xy
	private readonly List<float> _coverVerts = new();   // 6 * (clip.xy, rgba)

	// Linear: Start/End are the gradient endpoints. Radial: Start = ellipse center, End = ellipse radius (rx, ry),
	// Origin = focal point; Radial flips the shader's t computation.
	private readonly record struct Grad(Vector2 Start, Vector2 End, float Opacity, byte[] Lut, Vector2 Half, Vector4 Radii, bool Radial, Vector2 Origin, int Tile);
	private readonly List<Grad> _grads = new();

	private readonly List<float> _imageVerts = new();   // 6 * (clip.xy, uv.xy)
	private readonly record struct Img(byte[] Pixels, int W, int H, float Opacity, float[]? Mat, long Key);
	private readonly List<Img> _images = new();

	private readonly record struct Clip(Vector4 RectPx, Vector4 RadiiPx, bool IsPath, int FanStart, int FanCount, int CoverStart, bool Exclude);
	private readonly List<Clip> _clips = new();   // clip shapes (rounded-rect or arbitrary path), indexed by ClipPush cmd.Aux
	private bool _hasClips;

	private static readonly int[] TriIndices = { 0, 1, 2, 2, 1, 3 };

	public WebGpuDrawList(uint width, uint height, float scale = 1f)
	{
		_scale = scale > 0 ? scale : 1f;
		_lw = width;
		_lh = height;
		_w = (uint)System.Math.Round(width * _scale);
		_h = (uint)System.Math.Round(height * _scale);
	}

	/// <summary>Clears all per-frame state so this instance can be REUSED for the next frame at the same size.
	/// List.Clear keeps the backing arrays, so the large vertex buffers don't re-allocate (and don't churn the
	/// Large Object Heap) — that's what eliminates the Gen2 GC pauses from rebuilding the draw list every frame.
	/// Same size only (the factory makes a fresh instance on resize, so _w/_h stay valid).</summary>
	public void Reset()
	{
		_cmds.Clear();
		_solidVerts.Clear(); _rrVerts.Clear(); _gradVerts.Clear(); _pathVerts.Clear(); _coverVerts.Clear(); _imageVerts.Clear();
		_grads.Clear(); _images.Clear(); _clips.Clear();
		_backdrops.Clear(); _shadows.Clear(); _layers.Clear(); _masks.Clear();
		_hasClips = false;
		_recording = false;
		_xforms.Clear(); _xformIndex.Clear(); _hasLastXform = false;
		_units.Clear(); _slabActive = false; _curId = -1; _visualDepth = 0; _pathDyn.Clear(); _coverDyn.Clear();
	}

	// ---- Arena (UNO_WEBGPU_ARENA): per-draw transform table -------------------------------------------
	// path/cover verts are stored in LOCAL space; this table holds the per-visual local→NDC affine that the
	// arena shaders apply, indexed by a per-vertex float index. RegisterTransform dedups by matrix (consecutive
	// glyphs of a visual share one entry). The table is small and rebuilt each frame; in stage 3 a moved visual
	// only updates its entry here — its vertices never re-upload.
	private static readonly bool _arenaEnabled = Environment.GetEnvironmentVariable("UNO_WEBGPU_ARENA") == "1";
	private readonly List<float> _xforms = new();              // 8 floats/transform (a.xyzw, b.xyzw; 6 used)
	private readonly Dictionary<Matrix4x4, int> _xformIndex = new();
	private Matrix4x4 _lastXformM; private int _lastXformIdx; private bool _hasLastXform;

	private int RegisterTransform(Matrix4x4 m)
	{
		// Single-entry memo: AddPath is called once per glyph with the SAME visual matrix, so the dict lookup
		// (which hashes+compares 16 floats) would run thousands of times/frame. The memo makes the common
		// consecutive-same-matrix case a cheap struct compare and tripled the build cost when it was missing.
		if (_hasLastXform && m == _lastXformM) { return _lastXformIdx; }
		if (!_xformIndex.TryGetValue(m, out var idx))
		{
			float sx = _scale * 2f / _w, sy = _scale * 2f / _h;
			idx = _xforms.Count / 8;
			_xforms.Add(m.M11 * sx); _xforms.Add(m.M21 * sx); _xforms.Add(m.M41 * sx - 1f); _xforms.Add(-m.M12 * sy);
			_xforms.Add(-m.M22 * sy); _xforms.Add(1f - m.M42 * sy); _xforms.Add(0f); _xforms.Add(0f);
			_xformIndex[m] = idx;
		}
		_lastXformM = m; _lastXformIdx = idx; _hasLastXform = true;
		return idx;
	}

	// ---- Per-visual slab (UNO_WEBGPU_SLAB) ------------------------------------------------------------
	// Builds on the arena: relocate each visual's PathFill geometry into a STABLE per-visual slice so that a content
	// change re-uploads only that visual's bytes (the partial-upload diff stays localized) instead of shifting every
	// subsequent visual. Frames containing NON-relocatable path geometry — path-clips / path-acrylic, which are
	// referenced by Clip/Backdrop structs via FanStart rather than by patchable PathFill cmds — fall back to the
	// plain arena layout for that frame. The post-pass (FinalizeBuild) runs on the UI thread at end-of-build, so the
	// SHARED allocator is only ever touched between sequential builds, never concurrently with the render thread.
	private static readonly bool _slabEnabled = _arenaEnabled && Environment.GetEnvironmentVariable("UNO_WEBGPU_SLAB") == "1";
	private static readonly WebGpuVertexSlab _slab = _slabEnabled ? new() : null!;
	private readonly List<float> _pathSlab = new(), _coverSlab = new(); // visual fills, relocated to stable slices; uploaded/drawn
	// Clip-path / acrylic-path geometry: referenced by Clip/Backdrop structs (via FanStart) which we can't patch, so
	// it is NOT relocated — it stays in its own small per-frame buffer at append offsets, re-uploaded each frame.
	private readonly List<float> _pathDyn = new(), _coverDyn = new();
	private bool _slabActive;            // this frame's visual fills were placed into the slab buffers (== _slabEnabled)
	private readonly record struct Unit(long Id, int PStart, int PLen, int CStart, int CLen, int CmdStart, int CmdEnd, bool Dirty);
	// Dirty-range upload (UNO_WEBGPU_DIRTY=1, needs SLAB+CACHE): instead of CommonPrefixLength scanning the whole slab
	// buffer every frame to find what changed, mark a visual's slice dirty AT the mutation — re-emitted content (cache
	// miss) or a changed transform-index/offset — and upload only those float ranges. Everything else clean by default.
	private static readonly bool _dirtyEnabled = _slabEnabled && Environment.GetEnvironmentVariable("UNO_WEBGPU_DIRTY") == "1";
	private readonly List<(int off, int len)> _dirtyPath = new(), _dirtyCover = new(); // float ranges changed this frame
	private static readonly Dictionary<long, (float tf, int pOff, int cOff)> _lastVis = new(); // per-visual last placement (UI thread)
	private bool _curContentDirty;       // set per visual: was its own-paint content re-emitted (cache miss) this frame
	private readonly List<Unit> _units = new();
	private readonly HashSet<long> _liveIds = new();
	private long _curId = -1; private int _curPStart, _curCStart, _curCmdStart, _visualDepth;
	private static readonly bool _slabDiag = Environment.GetEnvironmentVariable("UNO_WEBGPU_SLAB_STATS") == "1";
	private static int _slabDiagN;
	// Coalesce a run of consecutive PathFills into one stencil + one cover draw (cuts the per-glyph encode).
	// Correct only when the glyphs' even-odd fill regions don't overlap (normal text); opt-in for safety.
	private static readonly bool _coalescePaths = Environment.GetEnvironmentVariable("UNO_WEBGPU_COALESCE") == "1";

	// Render bundles (UNO_WEBGPU_BUNDLE=1, fast path only): record the scene's draws into reusable RenderBundles once,
	// then ExecuteBundles each frame instead of re-encoding hundreds of draw calls. Scissor/stencil-ref (not allowed
	// in bundles) become pass-level ops in _prog, which segments the bundles. Re-recorded only when the command
	// STRUCTURE (kinds/offsets/counts/scissors) changes — content/transform/colour flow through the buffers, so
	// scroll/animation/idle replay the same bundles. Default OFF; DrawScene is unchanged for the direct path.
	private static readonly bool _bundleEnabled = _arenaEnabled && Environment.GetEnvironmentVariable("UNO_WEBGPU_BUNDLE") == "1";
	// Incremental clip mask: skip the depth mask for pure-rect clips (scissor handles them) and add/rebuild masks
	// on push/pop instead of redrawing the whole stack each time — cuts the full-screen depth-write overdraw that
	// dominated clipped scenes. Default OFF: has a visual regression under investigation (greyed content); when off
	// we use the legacy full-rebuild-every-change path (correct but O(stack) per clip op).
	private static readonly bool _clipIncremental = Environment.GetEnvironmentVariable("UNO_WEBGPU_CLIPOPT") == "1";
	private readonly record struct POp(byte Kind, uint A, uint B, uint C, uint D); // 0=Scissor(x,y,w,h) 1=StencilRef(A) 2=ExecuteBundle(A)
	private readonly List<POp> _prog = new();
	private readonly List<GpuRenderBundle> _bundles = new();
	private ulong _progHash; private bool _progValid;

	internal bool SlabActive => _slabActive;

	// Nesting-safe: if a visual's PaintWebGpu recursively renders sub-visuals, only the OUTERMOST bracket forms a
	// unit (covering itself + the nested geometry, which is contiguous in the scratch buffer and relocates with the
	// same delta). Otherwise a nested EndVisual would reset _curId and orphan the outer visual's geometry.
	public void BeginVisual(long id)
	{
		if (!_slabEnabled) { return; }
		if (_visualDepth++ == 0)
		{
			_curId = id; _curPStart = _pathVerts.Count / 3; _curCStart = _coverVerts.Count / 7; _curCmdStart = _cmds.Count;
			_curContentDirty = true; // assume re-emitted unless the cache replays it (BeginCachedVisualReplay clears it)
		}
	}

	public void EndVisual()
	{
		if (!_slabEnabled || _visualDepth == 0) { return; }
		if (--_visualDepth == 0 && _curId >= 0)
		{
			int pLen = _pathVerts.Count / 3 - _curPStart;
			if (pLen > 0)
			{
				_units.Add(new Unit(_curId, _curPStart, pLen, _curCStart, _coverVerts.Count / 7 - _curCStart, _curCmdStart, _cmds.Count, _curContentDirty));
			}
			_curId = -1;
		}
	}

	// End-of-build (UI thread): relocate each visual's PathFill geometry to its stable slab slice and patch the
	// visual's PathFill cmds to address the slab buffer. Plain-arena fallback on frames with non-relocatable paths.
	public void FinalizeBuild()
	{
		if (!_slabEnabled) { return; }
		_slabActive = true; // visual fills always go to the slab; clip/acrylic geometry lives in the dyn buffers
		if (_dirtyEnabled) { _dirtyPath.Clear(); _dirtyCover.Clear(); }
		if (_slabDiag && _slabDiagN++ % 120 == 0)
		{
			int total = 0, covered = 0;
			for (int i = 0; i < _cmds.Count; i++)
			{
				if (_cmds[i].Kind != Kind.PathFill) { continue; }
				total++;
				foreach (var u in _units) { if (i >= u.CmdStart && i < u.CmdEnd) { covered++; break; } }
			}
			System.Console.WriteLine($"[SLAB] units={_units.Count} pathFillCmds={total} covered={covered} ORPHANS={total - covered} scratchPathV={_pathVerts.Count / 3} dynPathV={_pathDyn.Count / 3}");
		}
		if (_units.Count == 0) { return; }

		// Return capacity held by visuals not painted this frame (removed/virtualized-out) before reserving slices.
		_liveIds.Clear();
		foreach (var u in _units) { _liveIds.Add(u.Id); }
		_slab.RetainOnly(_liveIds);

		// The slab works in VERTEX units (so offsets are stride-aligned by construction); convert to floats here.
		foreach (var u in _units) { _slab.EnsurePath(u.Id, u.PLen); _slab.EnsureCover(u.Id, u.CLen); }
		int pCapF = _slab.PathCapacity * 3, cCapF = _slab.CoverCapacity * 7;
		if (_pathSlab.Count != pCapF) { CollectionsMarshal.SetCount(_pathSlab, pCapF); }
		if (_coverSlab.Count != cCapF) { CollectionsMarshal.SetCount(_coverSlab, cCapF); }

		var srcP = CollectionsMarshal.AsSpan(_pathVerts);
		var dstP = CollectionsMarshal.AsSpan(_pathSlab);
		var srcC = CollectionsMarshal.AsSpan(_coverVerts);
		var dstC = CollectionsMarshal.AsSpan(_coverSlab);
		foreach (var u in _units)
		{
			int pOff = _slab.PathOff(u.Id), cOff = _slab.CoverOff(u.Id); // vertex offsets
			srcP.Slice(u.PStart * 3, u.PLen * 3).CopyTo(dstP.Slice(pOff * 3, u.PLen * 3));
			if (u.CLen > 0) { srcC.Slice(u.CStart * 7, u.CLen * 7).CopyTo(dstC.Slice(cOff * 7, u.CLen * 7)); }
			if (_dirtyEnabled)
			{
				// Slice is dirty iff its own-paint content was re-emitted, its transform index changed, or it moved
				// (realloc/new). Otherwise its bytes are byte-identical to last frame → don't upload it.
				float tf = srcP[u.PStart * 3 + 2];
				if (u.Dirty || !_lastVis.TryGetValue(u.Id, out var lv) || lv.tf != tf || lv.pOff != pOff || lv.cOff != cOff)
				{
					_dirtyPath.Add((pOff * 3, u.PLen * 3));
					if (u.CLen > 0) { _dirtyCover.Add((cOff * 7, u.CLen * 7)); }
				}
				_lastVis[u.Id] = (tf, pOff, cOff);
			}
			int pDelta = pOff - u.PStart, cDelta = cOff - u.CStart;
			if (pDelta == 0 && cDelta == 0) { continue; }
			for (int i = u.CmdStart; i < u.CmdEnd; i++)
			{
				if (_cmds[i].Kind == Kind.PathFill)
				{
					_cmds[i] = _cmds[i] with { VertexStart = _cmds[i].VertexStart + pDelta, Aux = _cmds[i].Aux + cDelta };
				}
			}
		}
	}

	// ---- Per-visual geometry cache --------------------------------------------------------------------
	// Mirrors Skia's per-Visual _picture: record a visual's OWN-paint geometry once, then on later frames replay
	// it under the current matrix instead of re-walking/re-emitting it (the glyph-outline emission that dominates
	// the UI-thread build). Stored verts are NDC baked at the record matrix, so only a TRANSLATION change (the
	// scroll case) is replayable cheaply — the linear part of the matrix must match, else we re-record. The cache
	// object lives on the Visual (so it survives the double-buffered draw lists and is GC'd with the visual) and
	// is invalidated by InvalidatePaint, exactly like Skia's _picture. Opt-in via UNO_WEBGPU_CACHE=1.
	private static readonly bool _cacheEnabled = Environment.GetEnvironmentVariable("UNO_WEBGPU_CACHE") == "1";
	private bool _recording;
	private int _recSolidV, _recRrV, _recPathV, _recCoverV, _recCmd;
	private Vector4 _recClipDevice;

	private readonly record struct CmdRec(Kind Kind, int RelStart, int Count, int RelAux);
	private sealed class CachedVisual
	{
		public Matrix4x4 Matrix;
		public float Opacity;
		public bool NoSolidRr;
		public float[]? Solid, Rr, Path, Cover;
		public CmdRec[] Cmds = System.Array.Empty<CmdRec>();
	}

	/// <summary>If <paramref name="cache"/> can be replayed under the current matrix/opacity, replays it (so the
	/// caller SKIPS painting) and returns true. Otherwise starts recording and returns false — the caller paints
	/// as usual and must call <see cref="EndCachedVisualRecord"/> with the same matrix/opacity.</summary>
	private static bool LinearMatches(Matrix4x4 a, Matrix4x4 b)
		=> a.M11 == b.M11 && a.M12 == b.M12 && a.M21 == b.M21 && a.M22 == b.M22;

	public bool BeginCachedVisualReplay(object? cache, Matrix4x4 matrix, float opacity, Vector4 clipRect)
	{
		if (!_cacheEnabled) { return false; }
		// ARENA: path/cover verts are LOCAL, so any matrix is replayable by re-stamping the transform index — only a
		// visual mixing rect (solid/rr, which stay NDC-baked) fills needs the matrix's linear part to match. Non-arena
		// verts are NDC-baked, so only a pure-translation change (scroll) is replayable.
		if (cache is CachedVisual c && c.Opacity == opacity
			&& (_arenaEnabled ? (c.NoSolidRr || LinearMatches(c.Matrix, matrix)) : LinearMatches(c.Matrix, matrix)))
		{
			if (_arenaEnabled) { ReplayCachedArena(c, matrix, clipRect); } else { ReplayCached(c, matrix, clipRect); }
			_curContentDirty = false; // replayed unchanged content — its slab slice bytes are the same as last frame
			return true;
		}
		_recording = true;
		_recSolidV = _solidVerts.Count / 6; _recRrV = _rrVerts.Count / 14;
		_recPathV = _pathVerts.Count / (_arenaEnabled ? 3 : 2); _recCoverV = _coverVerts.Count / (_arenaEnabled ? 7 : 6);
		_recCmd = _cmds.Count;
		_recClipDevice = clipRect * _scale;
		return false;
	}

	/// <summary>Finalizes the recording started by <see cref="BeginCachedVisualReplay"/>. Returns a cache object
	/// to store on the visual (replayable next frame), or null if the visual's paint isn't cacheable (anything
	/// beyond solid/rounded/path fills under the visual's own clip — gradients, images, effects, sub-clips).</summary>
	public object? EndCachedVisualRecord(Matrix4x4 matrix, float opacity)
	{
		if (!_recording) { return null; }
		_recording = false;

		// Replay forces the visual's own clip onto every replayed cmd, so a recorded cmd that used a different
		// (tighter) scissor can't be cached. Compute the expected scissor the same way PushCmd does.
		float ecl = Math.Clamp(_recClipDevice.X, 0, _w), ect = Math.Clamp(_recClipDevice.Y, 0, _h);
		float ecr = Math.Clamp(_recClipDevice.Z, 0, _w), ecb = Math.Clamp(_recClipDevice.W, 0, _h);
		uint esx = (uint)MathF.Floor(ecl), esy = (uint)MathF.Floor(ect);
		uint esw = ecr > ecl ? (uint)MathF.Ceiling(ecr - ecl) : 0;
		uint esh = ecb > ect ? (uint)MathF.Ceiling(ecb - ect) : 0;

		int n = _cmds.Count - _recCmd;
		var recs = new CmdRec[n];
		for (int i = 0; i < n; i++)
		{
			var cmd = _cmds[_recCmd + i];
			if (cmd.Kind is not (Kind.Solid or Kind.RoundedRect or Kind.PathFill)) { return null; }
			if (cmd.Sx != esx || cmd.Sy != esy || cmd.Sw != esw || cmd.Sh != esh) { return null; }
			int relStart, relAux = -1;
			switch (cmd.Kind)
			{
				case Kind.Solid: relStart = cmd.VertexStart - _recSolidV; break;
				case Kind.RoundedRect: relStart = cmd.VertexStart - _recRrV; break;
				default: relStart = cmd.VertexStart - _recPathV; relAux = cmd.Aux - _recCoverV; break;
			}
			recs[i] = new CmdRec(cmd.Kind, relStart, cmd.Count, relAux);
		}

		return new CachedVisual
		{
			Matrix = matrix,
			Opacity = opacity,
			Solid = SliceF(_solidVerts, _recSolidV * 6),
			Rr = SliceF(_rrVerts, _recRrV * 14),
			Path = SliceF(_pathVerts, _recPathV * (_arenaEnabled ? 3 : 2)),
			Cover = SliceF(_coverVerts, _recCoverV * (_arenaEnabled ? 7 : 6)),
			// In arena, a visual with no rect (solid/rr) fills is replayable under ANY matrix (local verts + tf restamp).
			NoSolidRr = _solidVerts.Count / 6 == _recSolidV && _rrVerts.Count / 14 == _recRrV,
			Cmds = recs,
		};
	}

	private static float[]? SliceF(List<float> list, int from)
	{
		int len = list.Count - from;
		if (len <= 0) { return null; }
		var a = new float[len];
		CollectionsMarshal.AsSpan(list).Slice(from, len).CopyTo(a);
		return a;
	}

	private void ReplayCached(CachedVisual c, Matrix4x4 matrix, Vector4 clipRect)
	{
		float nx = (matrix.M41 - c.Matrix.M41) * _scale / _w * 2f;
		float ny = -((matrix.M42 - c.Matrix.M42) * _scale) / _h * 2f;
		int baseSolid = _solidVerts.Count / 6, baseRr = _rrVerts.Count / 14, basePath = _pathVerts.Count / 2, baseCover = _coverVerts.Count / 6;
		AppendOffset(_solidVerts, c.Solid, 6, nx, ny);
		AppendOffset(_rrVerts, c.Rr, 14, nx, ny);
		AppendOffset(_pathVerts, c.Path, 2, nx, ny);
		AppendOffset(_coverVerts, c.Cover, 6, nx, ny);

		var clip = clipRect * _scale;
		float l = Math.Clamp(clip.X, 0, _w), t = Math.Clamp(clip.Y, 0, _h);
		float rt = Math.Clamp(clip.Z, 0, _w), bt = Math.Clamp(clip.W, 0, _h);
		uint sx = (uint)MathF.Floor(l), sy = (uint)MathF.Floor(t);
		uint sw = rt > l ? (uint)MathF.Ceiling(rt - l) : 0;
		uint sh = bt > t ? (uint)MathF.Ceiling(bt - t) : 0;

		foreach (var r in c.Cmds)
		{
			int vs, aux = -1;
			switch (r.Kind)
			{
				case Kind.Solid: vs = baseSolid + r.RelStart; break;
				case Kind.RoundedRect: vs = baseRr + r.RelStart; break;
				default: vs = basePath + r.RelStart; aux = baseCover + r.RelAux; break;
			}
			_cmds.Add(new Cmd(r.Kind, vs, r.Count, aux, sx, sy, sw, sh));
		}
	}

	// Appends src to dst, adding (nx,ny) to the position floats (indices 0,1 of each stride). When the visual
	// hasn't moved (nx==ny==0, the common static/idle case) this is a straight memcpy via AddRange.
	private static void AppendOffset(List<float> dst, float[]? src, int stride, float nx, float ny)
	{
		if (src is null) { return; }
		if (nx == 0f && ny == 0f) { dst.AddRange(src); return; }
		for (int i = 0; i < src.Length; i++)
		{
			int m = i % stride;
			dst.Add(m == 0 ? src[i] + nx : m == 1 ? src[i] + ny : src[i]);
		}
	}

	// Arena replay: path/cover verts are LOCAL and reused verbatim (a bulk AddRange), with only the per-vertex
	// transform index re-stamped to the current matrix's slot — so the shader repositions the whole visual without
	// re-walking glyph/shape outlines or re-running PushPathGeometry (the per-frame build cost). Solid/rr fills stay
	// NDC-baked, so they're translation-offset like the non-arena replay (guarded by the linear-match check).
	private void ReplayCachedArena(CachedVisual c, Matrix4x4 matrix, Vector4 clipRect)
	{
		float tf = RegisterTransform(matrix);
		float nx = (matrix.M41 - c.Matrix.M41) * _scale / _w * 2f;
		float ny = -((matrix.M42 - c.Matrix.M42) * _scale) / _h * 2f;
		int baseSolid = _solidVerts.Count / 6, baseRr = _rrVerts.Count / 14, basePath = _pathVerts.Count / 3, baseCover = _coverVerts.Count / 7;
		AppendOffset(_solidVerts, c.Solid, 6, nx, ny);
		AppendOffset(_rrVerts, c.Rr, 14, nx, ny);
		AppendRestampTf(_pathVerts, c.Path, 3, 2, tf);
		AppendRestampTf(_coverVerts, c.Cover, 7, 6, tf);

		var clip = clipRect * _scale;
		float l = Math.Clamp(clip.X, 0, _w), t = Math.Clamp(clip.Y, 0, _h);
		float rt = Math.Clamp(clip.Z, 0, _w), bt = Math.Clamp(clip.W, 0, _h);
		uint sx = (uint)MathF.Floor(l), sy = (uint)MathF.Floor(t);
		uint sw = rt > l ? (uint)MathF.Ceiling(rt - l) : 0;
		uint sh = bt > t ? (uint)MathF.Ceiling(bt - t) : 0;

		foreach (var r in c.Cmds)
		{
			int vs, aux = -1;
			switch (r.Kind)
			{
				case Kind.Solid: vs = baseSolid + r.RelStart; break;
				case Kind.RoundedRect: vs = baseRr + r.RelStart; break;
				default: vs = basePath + r.RelStart; aux = baseCover + r.RelAux; break;
			}
			_cmds.Add(new Cmd(r.Kind, vs, r.Count, aux, sx, sy, sw, sh));
		}
	}

	// Appends src to dst (bulk), then overwrites the transform-index float (at <paramref name="tfOff"/>, every
	// <paramref name="stride"/>) with the current visual's index. The x,y stay local — the shader positions them.
	private static void AppendRestampTf(List<float> dst, float[]? src, int stride, int tfOff, float tf)
	{
		if (src is null) { return; }
		int b = dst.Count; dst.AddRange(src);
		var span = CollectionsMarshal.AsSpan(dst);
		for (int i = b + tfOff; i < span.Length; i += stride) { span[i] = tf; }
	}

	public uint Width => _w;
	public uint Height => _h;

	private Vector2 Apply(Matrix4x4 m, float x, float y) => new((x * m.M11 + y * m.M21 + m.M41) * _scale, (x * m.M12 + y * m.M22 + m.M42) * _scale);
	private Vector2 ToClip(Vector2 d) => new(d.X / _w * 2f - 1f, 1f - d.Y / _h * 2f);

	public void AddRect(Matrix4x4 m, Vector2 size, Color color, float opacity, Vector4 clip)
	{
		int start = _solidVerts.Count / 6;
		float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f, a = color.A / 255f * opacity;
		Span<Vector2> c = stackalloc Vector2[4] { Apply(m, 0, 0), Apply(m, size.X, 0), Apply(m, 0, size.Y), Apply(m, size.X, size.Y) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(c[i]);
			_solidVerts.Add(p.X); _solidVerts.Add(p.Y); _solidVerts.Add(r); _solidVerts.Add(g); _solidVerts.Add(b); _solidVerts.Add(a);
		}
		PushCmd(Kind.Solid, start, 6, -1, clip * _scale);
	}

	public void AddLinearGradient(Matrix4x4 m, Vector2 size, Vector2 start, Vector2 end, float[] offsets, Color[] colors, float opacity, Vector4 clip, Vector4 radii = default, int tileMode = 0)
	{
		int vStart = _gradVerts.Count / 4;
		Span<Vector2> dev = stackalloc Vector2[4] { Apply(m, 0, 0), Apply(m, size.X, 0), Apply(m, 0, size.Y), Apply(m, size.X, size.Y) };
		Span<Vector2> loc = stackalloc Vector2[4] { new(0, 0), new(size.X, 0), new(0, size.Y), new(size.X, size.Y) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(dev[i]);
			_gradVerts.Add(p.X); _gradVerts.Add(p.Y); _gradVerts.Add(loc[i].X); _gradVerts.Add(loc[i].Y);
		}
		var half = new Vector2(size.X * 0.5f, size.Y * 0.5f);
		float maxR = MathF.Min(half.X, half.Y);
		var r = new Vector4(Math.Clamp(radii.X, 0, maxR), Math.Clamp(radii.Y, 0, maxR), Math.Clamp(radii.Z, 0, maxR), Math.Clamp(radii.W, 0, maxR));
		_grads.Add(new Grad(start, end, opacity, BuildLut(offsets, colors), half, r, false, default, tileMode));
		PushCmd(Kind.Gradient, vStart, 6, _grads.Count - 1, clip * _scale);
	}

	public void AddRadialGradient(Matrix4x4 m, Vector2 size, Vector2 center, Vector2 radius, Vector2 origin,
		float[] offsets, Color[] colors, float opacity, Vector4 clip, Vector4 radii = default, int tileMode = 0)
	{
		int vStart = _gradVerts.Count / 4;
		Span<Vector2> dev = stackalloc Vector2[4] { Apply(m, 0, 0), Apply(m, size.X, 0), Apply(m, 0, size.Y), Apply(m, size.X, size.Y) };
		Span<Vector2> loc = stackalloc Vector2[4] { new(0, 0), new(size.X, 0), new(0, size.Y), new(size.X, size.Y) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(dev[i]);
			_gradVerts.Add(p.X); _gradVerts.Add(p.Y); _gradVerts.Add(loc[i].X); _gradVerts.Add(loc[i].Y);
		}
		var half = new Vector2(size.X * 0.5f, size.Y * 0.5f);
		float maxR = MathF.Min(half.X, half.Y);
		var r = new Vector4(Math.Clamp(radii.X, 0, maxR), Math.Clamp(radii.Y, 0, maxR), Math.Clamp(radii.Z, 0, maxR), Math.Clamp(radii.W, 0, maxR));
		_grads.Add(new Grad(center, radius, opacity, BuildLut(offsets, colors), half, r, true, origin, tileMode));
		PushCmd(Kind.Gradient, vStart, 6, _grads.Count - 1, clip * _scale);
	}

	public void AddImage(Matrix4x4 m, Vector2 localOffset, Vector2 size, byte[] rgba, int pw, int ph, float opacity, Vector4 clip, float[]? colorMatrix = null, long imageKey = 0)
	{
		if (pw <= 0 || ph <= 0 || size.X <= 0 || size.Y <= 0) { return; }
		int vStart = _imageVerts.Count / 4;
		Span<Vector2> dev = stackalloc Vector2[4]
		{
			Apply(m, localOffset.X, localOffset.Y), Apply(m, localOffset.X + size.X, localOffset.Y),
			Apply(m, localOffset.X, localOffset.Y + size.Y), Apply(m, localOffset.X + size.X, localOffset.Y + size.Y),
		};
		Span<Vector2> uv = stackalloc Vector2[4] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(dev[i]);
			_imageVerts.Add(p.X); _imageVerts.Add(p.Y); _imageVerts.Add(uv[i].X); _imageVerts.Add(uv[i].Y);
		}
		_images.Add(new Img(rgba, pw, ph, opacity, colorMatrix, imageKey));
		PushCmd(Kind.Image, vStart, 6, _images.Count - 1, clip * _scale);
	}

	public void AddRoundedRect(Matrix4x4 m, Vector2 localOffset, Vector2 size, Vector4 radii, Color color, float opacity, Vector4 clip)
	{
		int vStart = _rrVerts.Count / 14;
		var half = new Vector2(size.X * 0.5f, size.Y * 0.5f);
		float maxR = MathF.Min(half.X, half.Y);
		var r = new Vector4(Math.Clamp(radii.X, 0, maxR), Math.Clamp(radii.Y, 0, maxR), Math.Clamp(radii.Z, 0, maxR), Math.Clamp(radii.W, 0, maxR));
		float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f, ca = color.A / 255f * opacity;
		Span<Vector2> dev = stackalloc Vector2[4]
		{
			Apply(m, localOffset.X, localOffset.Y), Apply(m, localOffset.X + size.X, localOffset.Y),
			Apply(m, localOffset.X, localOffset.Y + size.Y), Apply(m, localOffset.X + size.X, localOffset.Y + size.Y),
		};
		Span<Vector2> ctr = stackalloc Vector2[4] { new(-half.X, -half.Y), new(half.X, -half.Y), new(-half.X, half.Y), new(half.X, half.Y) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(dev[i]);
			_rrVerts.Add(p.X); _rrVerts.Add(p.Y); _rrVerts.Add(ctr[i].X); _rrVerts.Add(ctr[i].Y); _rrVerts.Add(half.X); _rrVerts.Add(half.Y);
			_rrVerts.Add(r.X); _rrVerts.Add(r.Y); _rrVerts.Add(r.Z); _rrVerts.Add(r.W); _rrVerts.Add(cr); _rrVerts.Add(cg); _rrVerts.Add(cb); _rrVerts.Add(ca);
		}
		PushCmd(Kind.RoundedRect, vStart, 6, -1, clip * _scale);
	}

	// <paramref name="localOffset"/> is added to each contour point in the path's local space (e.g. a glyph's
	// pen position) BEFORE the matrix — in arena mode it's baked into the local vertex so all of a visual's glyphs
	// share one transform; in non-arena mode it folds into the matrix (Apply(m, p+off) == Apply(Translate(off)*m, p)).
	public void AddPath(Matrix4x4 m, Vector2[][] contours, Color color, float opacity, Vector4 clip, Vector2 localOffset = default)
	{
		if (!PushPathGeometry(m, contours, color, opacity, localOffset, coverNdc: false, out int fanStart, out int fanCount, out int coverStart, out _))
		{
			return;
		}
		PushCmd(Kind.PathFill, fanStart, fanCount, coverStart, clip * _scale);
	}

	// Pushes the even-odd stencil fan + cover-bbox quad for a path (no command). The cover carries `color` (used by
	// solid fills; ignored when the cover samples a texture). In ARENA mode the fan/cover are LOCAL space + a
	// per-vertex transform index (the shader applies the visual's affine); <paramref name="coverNdc"/> keeps the
	// cover in NDC for the acrylic backdrop (whose shader reads position directly and ignores the index). Returns
	// the device-space bbox (non-arena) / local bbox (arena) and false if the path has no drawable geometry.
	private bool PushPathGeometry(Matrix4x4 m, Vector2[][] contours, Color color, float opacity, Vector2 localOffset, bool coverNdc,
		out int fanStart, out int fanCount, out int coverStart, out Vector4 bboxDevice, bool dyn = false)
	{
		bboxDevice = default;
		float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f, ca = color.A / 255f * opacity;

		if (_arenaEnabled)
		{
			// Non-relocatable clip/acrylic geometry (dyn) goes to the dyn buffers so the slab post-pass leaves it
			// (and the Clip/Backdrop FanStart that references it) untouched; visual fills go to the scratch buffer.
			var pv = (_slabEnabled && dyn) ? _pathDyn : _pathVerts;
			var cv = (_slabEnabled && dyn) ? _coverDyn : _coverVerts;
			float tf = RegisterTransform(m);
			fanStart = pv.Count / 3; fanCount = 0; coverStart = 0;
			Vector2? anchorOpt = null;
			foreach (var ct in contours) { if (ct.Length >= 2) { anchorOpt = new Vector2(ct[0].X + localOffset.X, ct[0].Y + localOffset.Y); break; } }
			if (anchorOpt is null) { return false; }
			var anchor = anchorOpt.Value;
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (var contour in contours)
			{
				int n = contour.Length;
				if (n < 2) { continue; }
				for (int i = 0; i < n; i++)
				{
					float ax = contour[i].X + localOffset.X, ay = contour[i].Y + localOffset.Y;
					float bx = contour[(i + 1) % n].X + localOffset.X, by = contour[(i + 1) % n].Y + localOffset.Y;
					if (ax < minX) { minX = ax; } if (ax > maxX) { maxX = ax; } if (ay < minY) { minY = ay; } if (ay > maxY) { maxY = ay; }
					pv.Add(anchor.X); pv.Add(anchor.Y); pv.Add(tf);
					pv.Add(ax); pv.Add(ay); pv.Add(tf);
					pv.Add(bx); pv.Add(by); pv.Add(tf);
				}
			}
			fanCount = pv.Count / 3 - fanStart;
			if (fanCount == 0) { return false; }
			coverStart = cv.Count / 7;
			Span<Vector2> q = stackalloc Vector2[4] { new(minX, minY), new(maxX, minY), new(minX, maxY), new(maxX, maxY) };
			foreach (var i in TriIndices)
			{
				var p = coverNdc ? ToClip(Apply(m, q[i].X, q[i].Y)) : q[i];
				cv.Add(p.X); cv.Add(p.Y); cv.Add(cr); cv.Add(cg); cv.Add(cb); cv.Add(ca); cv.Add(tf);
			}
			bboxDevice = new Vector4(minX, minY, maxX, maxY);
			return true;
		}

		fanStart = _pathVerts.Count / 2; fanCount = 0; coverStart = 0;
		Vector2? anchorOptN = null;
		foreach (var ct in contours) { if (ct.Length >= 2) { anchorOptN = Apply(m, ct[0].X + localOffset.X, ct[0].Y + localOffset.Y); break; } }
		if (anchorOptN is null) { return false; }
		var anchorN = ToClip(anchorOptN.Value);

		float dminX = float.MaxValue, dminY = float.MaxValue, dmaxX = float.MinValue, dmaxY = float.MinValue;
		foreach (var contour in contours)
		{
			if (contour.Length < 2) { continue; }
			for (int i = 0; i < contour.Length; i++)
			{
				var ad = Apply(m, contour[i].X + localOffset.X, contour[i].Y + localOffset.Y);
				var bd = Apply(m, contour[(i + 1) % contour.Length].X + localOffset.X, contour[(i + 1) % contour.Length].Y + localOffset.Y);
				dminX = MathF.Min(dminX, ad.X); dminY = MathF.Min(dminY, ad.Y); dmaxX = MathF.Max(dmaxX, ad.X); dmaxY = MathF.Max(dmaxY, ad.Y);
				var a = ToClip(ad); var b = ToClip(bd);
				_pathVerts.Add(anchorN.X); _pathVerts.Add(anchorN.Y);
				_pathVerts.Add(a.X); _pathVerts.Add(a.Y);
				_pathVerts.Add(b.X); _pathVerts.Add(b.Y);
			}
		}
		fanCount = _pathVerts.Count / 2 - fanStart;
		if (fanCount == 0) { return false; }

		coverStart = _coverVerts.Count / 6;
		Span<Vector2> qn = stackalloc Vector2[4] { new(dminX, dminY), new(dmaxX, dminY), new(dminX, dmaxY), new(dmaxX, dmaxY) };
		foreach (var i in TriIndices)
		{
			var p = ToClip(qn[i]);
			_coverVerts.Add(p.X); _coverVerts.Add(p.Y); _coverVerts.Add(cr); _coverVerts.Add(cg); _coverVerts.Add(cb); _coverVerts.Add(ca);
		}
		bboxDevice = new Vector4(dminX, dminY, dmaxX, dmaxY);
		return true;
	}

	public void AddAcrylic(Matrix4x4 m, Vector2 localOffset, Vector2 size, Vector4 radii, Color tint, Color luminosity, float blurSigma, float noiseOpacity, bool isOpaque, float opacity, Vector4 clip)
	{
		// Opaque acrylic = solid tint, no backdrop needed.
		if (isOpaque || blurSigma <= 0)
		{
			AddRoundedRect(m, localOffset, size, radii, tint, opacity, clip);
			return;
		}

		// Corner radii in device px (uniform-scale approx) for the backdrop's rounded mask.
		float scale = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12) * _scale;
		float maxR = MathF.Min(size.X, size.Y) * 0.5f * scale;
		var radiiPx = new Vector4(
			Math.Clamp(radii.X * scale, 0, maxR), Math.Clamp(radii.Y * scale, 0, maxR),
			Math.Clamp(radii.Z * scale, 0, maxR), Math.Clamp(radii.W * scale, 0, maxR));

		// Translucent acrylic: (1) a Backdrop command that, at render time, blits the gaussian-blurred
		// content-behind into this region, then (2) the tint composited over it (SrcOver). The region is
		// the device-space AABB of the rect, intersected with the inherited clip.
		Span<Vector2> dev = stackalloc Vector2[4]
		{
			Apply(m, localOffset.X, localOffset.Y), Apply(m, localOffset.X + size.X, localOffset.Y),
			Apply(m, localOffset.X, localOffset.Y + size.Y), Apply(m, localOffset.X + size.X, localOffset.Y + size.Y),
		};
		float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
		foreach (var p in dev) { l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y); r = MathF.Max(r, p.X); b = MathF.Max(b, p.Y); }
		var region = new Vector4(MathF.Max(l, clip.X * _scale), MathF.Max(t, clip.Y * _scale), MathF.Min(r, clip.Z * _scale), MathF.Min(b, clip.W * _scale));
		_backdrops.Add(new Backdrop(blurSigma * _scale, luminosity, radiiPx, noiseOpacity, false, 0, 0, 0));
		PushCmd(Kind.Backdrop, 0, 0, _backdrops.Count - 1, region);

		AddRoundedRect(m, localOffset, size, radii, tint, opacity, clip);
	}

	// Acrylic masked by an arbitrary path: a path-masked blurred backdrop, then the tint filling the path.
	public void AddAcrylicPath(Matrix4x4 m, Vector2[][] contours, Color tint, Color luminosity, float blurSigma, float noiseOpacity, bool isOpaque, float opacity, Vector4 clip)
	{
		if (isOpaque || blurSigma <= 0)
		{
			AddPath(m, contours, tint, opacity, clip);
			return;
		}

		// Dedicated fan + cover for the blurred-backdrop pass (stencil-then-cover, color unused — sampled).
		if (!PushPathGeometry(m, contours, tint, opacity, default, coverNdc: true, out int fanStart, out int fanCount, out int coverStart, out var bbox, dyn: true))
		{
			return;
		}
		var region = new Vector4(MathF.Max(bbox.X, clip.X * _scale), MathF.Max(bbox.Y, clip.Y * _scale), MathF.Min(bbox.Z, clip.Z * _scale), MathF.Min(bbox.W, clip.W * _scale));
		_backdrops.Add(new Backdrop(blurSigma * _scale, luminosity, default, noiseOpacity, true, fanStart, fanCount, coverStart));
		PushCmd(Kind.Backdrop, 0, 0, _backdrops.Count - 1, region);

		// Tint, masked by the same path, composited on top.
		AddPath(m, contours, tint, opacity, clip);
	}

	public void PushClip(Vector4 deviceRect, Vector4 radii)
	{
		_hasClips = true;
		_clips.Add(new Clip(deviceRect * _scale, radii * _scale, false, 0, 0, 0, false));
		// Scissor full-frame-ish: the clip mask itself bounds it; use the rect as the scissor too.
		PushCmd(Kind.ClipPush, 0, 0, _clips.Count - 1, deviceRect * _scale);
	}

	public void PushClipExclude(Vector4 deviceRect, Vector4 radii)
	{
		// Inverse clip: clips OUT the inside of the rounded rect (keeps only outside it). Used to turn a
		// filled rounded rect into a ring (border stroke) — fill the outer, exclude the inner.
		_hasClips = true;
		_clips.Add(new Clip(deviceRect * _scale, radii * _scale, false, 0, 0, 0, true));
		PushCmd(Kind.ClipPush, 0, 0, _clips.Count - 1, new Vector4(0, 0, _w, _h));
	}

	public void PushClipPath(Matrix4x4 m, Vector2[][] contours)
	{
		// Arbitrary-path clip: rasterize the path's even-odd coverage into the clip mask (depth) via the same
		// stencil fan + cover-bbox geometry as a path fill, then write depth=1 OUTSIDE the path at render time.
		if (!PushPathGeometry(m, contours, Color.FromArgb(0, 0, 0, 0), 1f, default, coverNdc: false, out int fanStart, out int fanCount, out int coverStart, out var bbox, dyn: true))
		{
			return;
		}
		_hasClips = true;
		_clips.Add(new Clip(bbox, default, true, fanStart, fanCount, coverStart, false)); // bbox in RectPx so the mask draw can be scissored to it
		PushCmd(Kind.ClipPush, 0, 0, _clips.Count - 1, bbox);
	}

	public void PopClip() => PushCmd(Kind.ClipPop, 0, 0, 0, new Vector4(0, 0, _w, _h));

	public int BeginShadow(Color shadowColor, float dx, float dy, float blurSigma, Vector4 clip)
	{
		int handle = _shadows.Count;
		// The shadow cmd sits before the bracketed content; its coverage is rendered from that range. The
		// composite region is just the inherited clip (coverage is ~0 outside the content, so no tight bbox
		// is needed). GroupStart = the next cmd; GroupEnd is patched in EndShadow.
		_shadows.Add(new ShadowCmd(0, -1, shadowColor, dx * _scale, dy * _scale, blurSigma * _scale));
		PushCmd(Kind.Shadow, 0, 0, handle, clip * _scale);
		_shadows[handle] = _shadows[handle] with { GroupStart = _cmds.Count };
		return handle;
	}

	public void EndShadow(int handle)
	{
		if (handle >= 0 && handle < _shadows.Count)
		{
			_shadows[handle] = _shadows[handle] with { GroupEnd = _cmds.Count };
		}
	}

	public int BeginLayer(float opacity, Vector4 clip)
	{
		int handle = _layers.Count;
		_layers.Add(new LayerCmd(0, -1, opacity));
		PushCmd(Kind.Layer, 0, 0, handle, clip * _scale);
		_layers[handle] = _layers[handle] with { GroupStart = _cmds.Count };
		return handle;
	}

	public void EndLayer(int handle)
	{
		if (handle >= 0 && handle < _layers.Count)
		{
			_layers[handle] = _layers[handle] with { GroupEnd = _cmds.Count };
		}
	}

	// Mask brush: brackets the SOURCE fill, then (after MaskSeparator) the MASK fill. The two are rendered to
	// separate offscreens and combined as source × mask.alpha — the WebGPU equivalent of Skia's SrcOver+DstIn layers.
	public int BeginMask(Vector4 clip)
	{
		int handle = _masks.Count;
		_masks.Add(new MaskCmd(0, -1, -1));
		PushCmd(Kind.Mask, 0, 0, handle, clip * _scale);
		_masks[handle] = _masks[handle] with { SourceStart = _cmds.Count };
		return handle;
	}

	public void MaskSeparator(int handle)
	{
		if (handle >= 0 && handle < _masks.Count)
		{
			_masks[handle] = _masks[handle] with { MaskStart = _cmds.Count };
		}
	}

	public void EndMask(int handle)
	{
		if (handle >= 0 && handle < _masks.Count)
		{
			_masks[handle] = _masks[handle] with { MaskEnd = _cmds.Count };
		}
	}

	private void PushCmd(Kind kind, int vStart, int count, int aux, Vector4 clip)
	{
		float l = Math.Clamp(clip.X, 0, _w), t = Math.Clamp(clip.Y, 0, _h);
		float rt = Math.Clamp(clip.Z, 0, _w), bt = Math.Clamp(clip.W, 0, _h);
		uint sw = rt > l ? (uint)MathF.Ceiling(rt - l) : 0;
		uint sh = bt > t ? (uint)MathF.Ceiling(bt - t) : 0;
		_cmds.Add(new Cmd(kind, vStart, count, aux, (uint)MathF.Floor(l), (uint)MathF.Floor(t), sw, sh));
	}

	private static byte[] BuildLut(float[] offsets, Color[] colors)
	{
		var lut = new byte[256 * 4];
		for (int i = 0; i < 256; i++)
		{
			float t = i / 255f; Color c;
			if (t <= offsets[0]) { c = colors[0]; }
			else if (t >= offsets[^1]) { c = colors[^1]; }
			else
			{
				int s = 0; while (s < offsets.Length - 1 && t > offsets[s + 1]) { s++; }
				float f = (t - offsets[s]) / MathF.Max(offsets[s + 1] - offsets[s], 1e-6f);
				c = Color.FromArgb((byte)(colors[s].A + (colors[s + 1].A - colors[s].A) * f), (byte)(colors[s].R + (colors[s + 1].R - colors[s].R) * f),
					(byte)(colors[s].G + (colors[s + 1].G - colors[s].G) * f), (byte)(colors[s].B + (colors[s + 1].B - colors[s].B) * f));
			}
			lut[i * 4] = c.R; lut[i * 4 + 1] = c.G; lut[i * 4 + 2] = c.B; lut[i * 4 + 3] = c.A;
		}
		return lut;
	}

	private static readonly BlendState SrcOver = new()
	{
		Color = new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
		Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
	};

	// Compositing PREMULTIPLIED content (an offscreen rendered with SrcOver onto transparent black yields
	// premultiplied color): out = src + dst*(1-src.a). Used to composite an opacity layer.
	private static readonly BlendState PremulOver = new()
	{
		Color = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
		Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
	};

	// Depth carries the rounded-clip mask: 0 = inside clip (or no clip), 1 = clipped out. Content vertices
	// have z=0, so GreaterEqual passes inside (0>=0) and fails in clipped corners (0>=1). Independent of the
	// stencil even-odd used for path fills, so both tests AND together.
	private static DepthStencilState StencilState(StencilOperation passOp, CompareFunction compare) => new()
	{
		Format = TextureFormat.Depth24PlusStencil8,
		DepthWriteEnabled = false,
		DepthCompare = CompareFunction.GreaterEqual,
		StencilReadMask = 0xFF,
		StencilWriteMask = 0xFF,
		StencilFront = new StencilFaceState { Compare = compare, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = passOp },
		StencilBack = new StencilFaceState { Compare = compare, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = passOp },
	};

	private const string SolidWgsl = """
		struct VSOut { @builtin(position) pos : vec4f, @location(0) col : vec4f }
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) c : vec4f) -> VSOut { var o:VSOut; o.pos=vec4f(p,0,1); o.col=c; return o; }
		@fragment fn fs_main(@location(0) c : vec4f) -> @location(0) vec4f { return c; }
		""";
	private const string GradientWgsl = """
		struct U { start : vec2f, end : vec2f, opacity : f32, kind : f32, half : vec2f, radii : vec4f, origin : vec2f, tile : f32, pad : f32 }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var lut : texture_2d<f32>;
		struct VSOut { @builtin(position) pos : vec4f, @location(0) local : vec2f }
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) local : vec2f) -> VSOut { var o:VSOut; o.pos=vec4f(p,0,1); o.local=local; return o; }
		@fragment fn fs_main(@location(0) local : vec2f) -> @location(0) vec4f {
			var t : f32;
			if (u.kind < 0.5) {
				// Linear: project onto the start→end axis (raw, unclamped — tile mode handles the ends).
				let dir = u.end - u.start; t = dot(local - u.start, dir) / max(dot(dir, dir), 1e-6);
			} else {
				// Radial: normalize to unit-circle space (ellipse radius → 1), then param along the ray from the
				// focal origin so t=0 at the origin and t=1 at the ellipse edge (centered origin → plain distance).
				let inv = vec2f(1.0) / max(u.end, vec2f(1e-6));
				let pn = (local - u.start) * inv;
				let on = (u.origin - u.start) * inv;
				let dir = pn - on; let a = dot(dir, dir);
				if (a < 1e-9) { t = 0.0; }
				else {
					let b = 2.0 * dot(on, dir); let c = dot(on, on) - 1.0;
					let s = (-b + sqrt(max(b * b - 4.0 * a * c, 0.0))) / (2.0 * a);
					t = 1.0 / max(s, 1e-6);
				}
			}
			// ExtendMode: 0 Clamp, 1 Wrap (repeat), 2 Mirror (reflect). fract handles negatives in WGSL.
			if (u.tile < 0.5) { t = clamp(t, 0.0, 1.0); }
			else if (u.tile < 1.5) { t = fract(t); }
			else { t = 1.0 - abs(1.0 - fract(t * 0.5) * 2.0); }
			let c = textureSample(lut, samp, vec2f(t, 0.5));
			// Rounded-rect mask (radii 0 → plain rect, full coverage).
			let lp = local - u.half;
			let rTop = select(u.radii.x, u.radii.y, lp.x > 0.0); let rBot = select(u.radii.w, u.radii.z, lp.x > 0.0);
			let rad = select(rTop, rBot, lp.y > 0.0);
			let q = abs(lp) - u.half + vec2f(rad);
			let d = min(max(q.x, q.y), 0.0) + length(max(q, vec2f(0.0))) - rad;
			let aa = max(fwidth(d), 1e-4);
			return vec4f(c.rgb, c.a * u.opacity * (1.0 - smoothstep(-aa, aa, d)));
		}
		""";
	private const string RoundedWgsl = """
		struct VSOut { @builtin(position) pos:vec4f, @location(0) p:vec2f, @location(1) half:vec2f, @location(2) radii:vec4f, @location(3) col:vec4f }
		@vertex fn vs_main(@location(0) clip:vec2f, @location(1) p:vec2f, @location(2) half:vec2f, @location(3) radii:vec4f, @location(4) col:vec4f) -> VSOut {
			var o:VSOut; o.pos=vec4f(clip,0,1); o.p=p; o.half=half; o.radii=radii; o.col=col; return o; }
		@fragment fn fs_main(in:VSOut) -> @location(0) vec4f {
			let rTop = select(in.radii.x, in.radii.y, in.p.x > 0.0); let rBot = select(in.radii.w, in.radii.z, in.p.x > 0.0);
			let rad  = select(rTop, rBot, in.p.y > 0.0); let q = abs(in.p) - in.half + vec2f(rad);
			let d = min(max(q.x, q.y), 0.0) + length(max(q, vec2f(0.0))) - rad; let aa = max(fwidth(d), 1e-4);
			return vec4f(in.col.rgb, in.col.a * (1.0 - smoothstep(-aa, aa, d)));
		}
		""";
	// Clip mask write: over the clip's device rect, write depth=1 OUTSIDE the rounded shape (clip those
	// corners out); discard inside (leave depth at 0 = allowed). Depth-only (no color), DepthWriteEnabled.
	private const string ClipWriteWgsl = """
		struct U { rectClip : vec4f, rectPx : vec4f, radii : vec4f, flags : vec4f }
		@group(0) @binding(0) var<uniform> u : U;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var xs = array<f32,6>(u.rectClip.x, u.rectClip.z, u.rectClip.x, u.rectClip.x, u.rectClip.z, u.rectClip.z);
			var ys = array<f32,6>(u.rectClip.y, u.rectClip.y, u.rectClip.w, u.rectClip.w, u.rectClip.y, u.rectClip.w);
			return vec4f(xs[vi], ys[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @builtin(frag_depth) f32 {
			let half = (u.rectPx.zw - u.rectPx.xy) * 0.5;
			let lp = pos.xy - (u.rectPx.xy + u.rectPx.zw) * 0.5;
			let rTop = select(u.radii.x, u.radii.y, lp.x > 0.0);
			let rBot = select(u.radii.w, u.radii.z, lp.x > 0.0);
			var rad = select(rTop, rBot, lp.y > 0.0);
			rad = min(rad, min(half.x, half.y));
			let q = abs(lp) - half + vec2f(rad);
			let d = min(max(q.x, q.y), 0.0) + length(max(q, vec2f(0.0))) - rad;
			// flags.x != 0 → exclude (clip out the inside): mark depth=1 INSIDE, keep outside. Else mark outside.
			let inside = d <= 0.0;
			let exclude = u.flags.x > 0.5;
			if (inside != exclude) { discard; }
			return 1.0;
		}
		""";
	// Path-clip cover: FULLSCREEN — write depth=1 wherever the stencil is 0 (OUTSIDE the even-odd path),
	// excluding those pixels. Fullscreen (not the path bbox) so content overflowing the path's bounds — e.g. an
	// oversized image arranged past the shape — is clipped too. The stencil compare (Equal 0) + reset are in the
	// pipeline state; stencil!=0 inside the path fails the test (FailOp=Zero) so no depth is written there.
	private const string ClipPathCoverWgsl = """
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var p = array<vec2f,3>(vec2f(-1,-1), vec2f(3,-1), vec2f(-1,3));
			return vec4f(p[vi], 0, 1);
		}
		@fragment fn fs_main() -> @builtin(frag_depth) f32 { return 1.0; }
		""";
	// Resets the clip mask: fullscreen, writes depth=0 (everything allowed again).
	private const string ClipClearWgsl = """
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var p = array<vec2f,3>(vec2f(-1,-1), vec2f(3,-1), vec2f(-1,3));
			return vec4f(p[vi], 0, 1);
		}
		@fragment fn fs_main() -> @builtin(frag_depth) f32 { return 0.0; }
		""";
	private const string ImageWgsl = """
		// row0..row3 = rgba coeffs per output channel; off = per-channel offset; flags.x = opacity, flags.y = hasMatrix.
		struct U { row0 : vec4f, row1 : vec4f, row2 : vec4f, row3 : vec4f, off : vec4f, flags : vec4f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		struct VSOut { @builtin(position) pos : vec4f, @location(0) uv : vec2f }
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) uv : vec2f) -> VSOut { var o:VSOut; o.pos=vec4f(p,0,1); o.uv=uv; return o; }
		@fragment fn fs_main(@location(0) uv : vec2f) -> @location(0) vec4f {
			var c = textureSample(tex, samp, uv);
			if (u.flags.y > 0.5) {
				// Color matrix applied on unpremultiplied color (D2D/Skia convention), then clamped.
				let cc = vec4f(c.rgb, c.a);
				c = clamp(vec4f(dot(u.row0, cc) + u.off.x, dot(u.row1, cc) + u.off.y, dot(u.row2, cc) + u.off.z, dot(u.row3, cc) + u.off.w), vec4f(0.0), vec4f(1.0));
			}
			return vec4f(c.rgb, c.a * u.flags.x);
		}
		""";
	private const string StencilWgsl = """
		@vertex fn vs_main(@location(0) p : vec2f) -> @builtin(position) vec4f { return vec4f(p, 0, 1); }
		@fragment fn fs_main() -> @location(0) vec4f { return vec4f(0); }
		""";
	private const string CoverWgsl = """
		struct VSOut { @builtin(position) pos : vec4f, @location(0) col : vec4f }
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) c : vec4f) -> VSOut { var o:VSOut; o.pos=vec4f(p,0,1); o.col=c; return o; }
		@fragment fn fs_main(@location(0) c : vec4f) -> @location(0) vec4f { return c; }
		""";
	// ARENA variants (UNO_WEBGPU_ARENA): vertices are in LOCAL space + a per-vertex transform index; the
	// per-visual local→NDC affine lives in a read-only storage buffer (a=ax,bx,cx,ay  b=by,cy,_,_), applied here.
	// This lets a moved visual update only its (tiny) transform entry instead of re-uploading its vertices.
	private const string StencilArenaWgsl = """
		struct Xf { a : vec4f, b : vec4f }
		@group(0) @binding(0) var<storage, read> xf : array<Xf>;
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) ti : f32) -> @builtin(position) vec4f {
			let t = xf[u32(ti)];
			return vec4f(p.x*t.a.x + p.y*t.a.y + t.a.z, p.x*t.a.w + p.y*t.b.x + t.b.y, 0, 1);
		}
		@fragment fn fs_main() -> @location(0) vec4f { return vec4f(0); }
		""";
	private const string CoverArenaWgsl = """
		struct Xf { a : vec4f, b : vec4f }
		@group(0) @binding(0) var<storage, read> xf : array<Xf>;
		struct VSOut { @builtin(position) pos : vec4f, @location(0) col : vec4f }
		@vertex fn vs_main(@location(0) p : vec2f, @location(1) c : vec4f, @location(2) ti : f32) -> VSOut {
			let t = xf[u32(ti)];
			var o:VSOut; o.pos=vec4f(p.x*t.a.x + p.y*t.a.y + t.a.z, p.x*t.a.w + p.y*t.b.x + t.b.y, 0, 1); o.col=c; return o;
		}
		@fragment fn fs_main(@location(0) c : vec4f) -> @location(0) vec4f { return c; }
		""";
	// Fullscreen-triangle pass over the OUTPUT (u.invOut = 1/outputSize). With step=0 and a half-res
	// output it is an exact 2x2 box downsample (bilinear averages the 4 source texels). With a contiguous
	// step it is a separable 9-tap gaussian. Pyramid downsample + small gaussian avoids undersampling.
	private const string BlurWgsl = """
		struct U { invOut : vec2f, step : vec2f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var p = array<vec2f,3>(vec2f(-1,-1), vec2f(3,-1), vec2f(-1,3));
			return vec4f(p[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let uv = pos.xy * u.invOut;
			// step.x < 0 ⇒ box DOWNSAMPLE (magnitude = factor). 4× uses 4 bilinear taps over the 4×4 source
			// region (each tap averages one 2×2 sub-block), halving the pyramid's pass count vs 2× per pass.
			if (u.step.x < 0.0) {
				if (-u.step.x >= 3.0) {
					let st = u.invOut * 0.25;       // source texel size (output = source / 4)
					let c = uv - st * 0.5;          // uv lands 0.5 texel past the 4×4 block centre — recentre
					var d = textureSampleLevel(tex, samp, c + st * vec2f(-1.0, -1.0), 0.0);
					d = d + textureSampleLevel(tex, samp, c + st * vec2f(1.0, -1.0), 0.0);
					d = d + textureSampleLevel(tex, samp, c + st * vec2f(-1.0, 1.0), 0.0);
					d = d + textureSampleLevel(tex, samp, c + st * vec2f(1.0, 1.0), 0.0);
					return d * 0.25;
				}
				return textureSampleLevel(tex, samp, uv, 0.0); // 2× — bilinear at half-res averages a 2×2 block
			}
			var w = array<f32,5>(0.2270, 0.1940, 0.1216, 0.0540, 0.0162);
			var col = textureSampleLevel(tex, samp, uv, 0.0) * w[0];
			for (var i = 1; i < 5; i = i + 1) {
				let o = u.step * f32(i);
				col = col + textureSampleLevel(tex, samp, uv + o, 0.0) * w[i];
				col = col + textureSampleLevel(tex, samp, uv - o, 0.0) * w[i];
			}
			return col;
		}
		""";
	// Drop-shadow composite: a quad over the (clip-space) shadow region samples the blurred coverage at
	// (pixel − offset) and emits shadowColor with alpha = shadowColor.a × coverage. SrcOver.
	private const string ShadowWgsl = """
		struct U { rectClip : vec4f, params : vec4f, color : vec4f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var xs = array<f32,6>(u.rectClip.x, u.rectClip.z, u.rectClip.x, u.rectClip.x, u.rectClip.z, u.rectClip.z);
			var ys = array<f32,6>(u.rectClip.y, u.rectClip.y, u.rectClip.w, u.rectClip.w, u.rectClip.y, u.rectClip.w);
			return vec4f(xs[vi], ys[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let cov = textureSampleLevel(tex, samp, (pos.xy - u.params.zw) * u.params.xy, 0.0).a;
			return vec4f(u.color.rgb, u.color.a * cov);
		}
		""";
	// Opacity-layer composite: sample the offscreen (premultiplied) group at screen-uv and scale by the group
	// opacity. Composited with PremulOver so overlapping content in the group blends once, then fades as a whole.
	private const string LayerWgsl = """
		struct U { rectClip : vec4f, inv : vec2f, opacity : f32, pad : f32 }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var xs = array<f32,6>(u.rectClip.x, u.rectClip.z, u.rectClip.x, u.rectClip.x, u.rectClip.z, u.rectClip.z);
			var ys = array<f32,6>(u.rectClip.y, u.rectClip.y, u.rectClip.w, u.rectClip.w, u.rectClip.y, u.rectClip.w);
			return vec4f(xs[vi], ys[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let c = textureSampleLevel(tex, samp, pos.xy * u.inv, 0.0);
			return c * u.opacity;
		}
		""";
	// Mask-brush composite: sample the SOURCE offscreen (premultiplied) and the MASK offscreen, output
	// source × mask.alpha — the DstIn masking Skia does with a second SaveLayer. Composited with PremulOver.
	private const string MaskWgsl = """
		struct U { rectClip : vec4f, inv : vec2f, pad : vec2f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var srcTex : texture_2d<f32>;
		@group(0) @binding(3) var mskTex : texture_2d<f32>;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var xs = array<f32,6>(u.rectClip.x, u.rectClip.z, u.rectClip.x, u.rectClip.x, u.rectClip.z, u.rectClip.z);
			var ys = array<f32,6>(u.rectClip.y, u.rectClip.y, u.rectClip.w, u.rectClip.w, u.rectClip.y, u.rectClip.w);
			return vec4f(xs[vi], ys[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let s = textureSampleLevel(srcTex, samp, pos.xy * u.inv, 0.0);
			let m = textureSampleLevel(mskTex, samp, pos.xy * u.inv, 0.0);
			return s * m.a;
		}
		""";
	// Cover step for a PATH-masked acrylic backdrop: the cover bbox quad (clip-space positions) samples
	// the blurred backdrop at screen UV + luminosity/grain; stencil (NotEqual 0) restricts it to the path.
	private const string CoverBackdropWgsl = """
		struct U { params : vec4f, lum : vec4f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		@vertex fn vs_main(@location(0) p : vec2f) -> @builtin(position) vec4f { return vec4f(p, 0, 1); }
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let blurred = textureSampleLevel(tex, samp, pos.xy * u.params.xy, 0.0).rgb;
			var rgb = mix(blurred, u.lum.rgb, u.lum.a);
			let n = (fract(sin(dot(floor(pos.xy), vec2f(12.9898, 78.233))) * 43758.5453) - 0.5) * 2.0 * u.params.z;
			return vec4f(clamp(rgb + vec3f(n), vec3f(0.0), vec3f(1.0)), 1.0);
		}
		""";
	// Composites the acrylic backdrop: blurred content (sampled at screen UV) → luminosity blend with
	// u.lum → procedural grain (u.params.z) → rounded-rect SDF mask (alpha), so corners outside the
	// rounded region keep the original content (SrcOver). rectClip = clip-space quad; rectPx/radii in px.
	private const string BackdropWgsl = """
		struct U { rectClip : vec4f, params : vec4f, lum : vec4f, rectPx : vec4f, radii : vec4f }
		@group(0) @binding(0) var<uniform> u : U;
		@group(0) @binding(1) var samp : sampler;
		@group(0) @binding(2) var tex : texture_2d<f32>;
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var xs = array<f32,6>(u.rectClip.x, u.rectClip.z, u.rectClip.x, u.rectClip.x, u.rectClip.z, u.rectClip.z);
			var ys = array<f32,6>(u.rectClip.y, u.rectClip.y, u.rectClip.w, u.rectClip.w, u.rectClip.y, u.rectClip.w);
			return vec4f(xs[vi], ys[vi], 0, 1);
		}
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let blurred = textureSampleLevel(tex, samp, pos.xy * u.params.xy, 0.0).rgb;
			var rgb = mix(blurred, u.lum.rgb, u.lum.a);
			let n = (fract(sin(dot(floor(pos.xy), vec2f(12.9898, 78.233))) * 43758.5453) - 0.5) * 2.0 * u.params.z;
			rgb = clamp(rgb + vec3f(n), vec3f(0.0), vec3f(1.0));
			let half = (u.rectPx.zw - u.rectPx.xy) * 0.5;
			let lp = pos.xy - (u.rectPx.xy + u.rectPx.zw) * 0.5;
			let rTop = select(u.radii.x, u.radii.y, lp.x > 0.0);
			let rBot = select(u.radii.w, u.radii.z, lp.x > 0.0);
			var rad = select(rTop, rBot, lp.y > 0.0);
			rad = min(rad, min(half.x, half.y));
			let q = abs(lp) - half + vec2f(rad);
			let d = min(max(q.x, q.y), 0.0) + length(max(q, vec2f(0.0))) - rad;
			let aa = max(fwidth(d), 1e-4);
			return vec4f(rgb, 1.0 - smoothstep(-aa, aa, d));
		}
		""";

	/// <summary>
	/// Renders the frame. When <paramref name="readback"/> is true the RGBA pixels are read back and returned
	/// (offscreen→CPU path, for the X11/GDI blit). When false the readback is SKIPPED and an empty array is
	/// returned — the frame stays on the GPU in <paramref name="targets"/>.Color, for a swapchain present to
	/// blit directly (no CPU round-trip). The swapchain caller MUST pass a persistent <paramref name="targets"/>.
	/// </summary>
		// AA sample count. Analytic shapes (rounded-rects/gradients via SDF) don't need it, but arbitrary path fills
		// — notably TEXT (stencil-then-cover glyph outlines) — have no analytic AA and rely on MSAA. The MSAA buffer
		// is now discarded after resolve (see Rendering.cs StoreOp.Discard) so MSAA no longer writes all N samples to
		// memory every frame; even so, the resolve still costs samples × physical-pixels, which is enough to blow the
		// 60fps budget at 4× on a high-DPI integrated GPU. So the DEFAULT is DPI-AWARE: fewer samples as scale rises,
		// where dense pixels already hide aliasing. UNO_WEBGPU_MSAA=1|2|4|8 forces a fixed count (8× is opt-in).
		private uint MsaaSampleCount()
			=> Environment.GetEnvironmentVariable("UNO_WEBGPU_MSAA") switch
			{
				"1" => 1u, "2" => 2u, "4" => 4u, "8" => 8u,
				_ => _scale >= 2f ? 1u : _scale > 1f ? 2u : 4u,
			};

	public byte[] Render(WebGpuContext ctx, Color clear, WebGpuTargets? targets = null, bool readback = true)
	{
		bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
		long ts = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
		var device = ctx.Gpu;
		var queue = ctx.GpuQueue;

		// Persistent caches live on `targets`; for a one-off caller without one, use a transient that's disposed
		// at the end (so pipelines/textures are still created+freed within the frame, just not reused).
		var cache = targets ?? new WebGpuTargets();
		bool transientCache = targets is null;
		var disposables = cache.ScratchA; disposables.Clear(); // reused per-frame scratch (option [2])
		cache.BeginCacheFrame();
		cache.EnsureTimer(ctx); cache.Timer?.BeginFrame();
		_dbgDraws = 0; _dbgPasses = 0; _dbgPassMs = 0; Common.Rendering.ResCreateMs = 0; _dbgEmptySkip = 0; _dbgHashMs = 0; _dbgClipMs = 0; _dbgClipN = 0;

		// NOTE: the whole-frame content-hash skip was removed — re-rendering an identical frame is harmless, and the
		// heavy per-frame vertex hash cost more than it saved (it ran every frame of any transform/opacity animation,
		// where the command structure is stable but the geometry moves). Simpler and faster to just always render.

		// Pipelines + sampler are created ONCE and cached (keyed by sample count) — they don't change per frame,
		// and recreating them every frame was the dominant GPU-frame cost once the readback was removed. Scene
		// pipelines use always-on depth so they're config-invariant: depth is the clip mask (cleared to 0,
		// content z=0, GreaterEqual passes everywhere when nothing writes depth), so it's a no-op without clips.
		uint msaa = MsaaSampleCount();
		if (!cache.PipelinesValid(msaa))
		{
			cache.DisposePipelines();
			var sceneDs = StencilState(StencilOperation.Keep, CompareFunction.Always);
			using (var m = ctx.CreateShaderModuleWgsl("solid", SolidWgsl))
				cache.AddPipe("solid", ctx.CreateRenderPipeline("solid", m,
					vertexLayouts: [new VLayout(24, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x4, 8, 1)])], bindGroupLayouts: [], blend: SrcOver, depthStencil: sceneDs, sampleCount: msaa));
			using (var m = ctx.CreateShaderModuleWgsl("grad", GradientWgsl))
				cache.AddPipe("grad", ctx.CreateRenderPipeline("grad", m,
					vertexLayouts: [new VLayout(16, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x2, 8, 1)])],
					bindGroupLayouts: [[
						new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform } },
						new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } },
						new BindGroupLayoutEntry { Binding = 2, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
					]], blend: SrcOver, depthStencil: sceneDs, sampleCount: msaa));
			using (var m = ctx.CreateShaderModuleWgsl("rrect", RoundedWgsl))
				cache.AddPipe("rrect", ctx.CreateRenderPipeline("rrect", m,
					vertexLayouts: [new VLayout(56, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x2, 8, 1), new(VertexFormat.Float32x2, 16, 2), new(VertexFormat.Float32x4, 24, 3), new(VertexFormat.Float32x4, 40, 4)])],
					bindGroupLayouts: [], blend: SrcOver, depthStencil: sceneDs, sampleCount: msaa));
			using (var m = ctx.CreateShaderModuleWgsl("image", ImageWgsl))
				cache.AddPipe("image", ctx.CreateRenderPipeline("image", m,
					vertexLayouts: [new VLayout(16, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x2, 8, 1)])],
					bindGroupLayouts: [[
						new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform } },
						new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } },
						new BindGroupLayoutEntry { Binding = 2, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
					]], blend: SrcOver, depthStencil: sceneDs, sampleCount: msaa));

			// Clip-mask pipelines (depth-only writes). Created unconditionally now (cached, used only when clips present).
			var clipDs = new DepthStencilState
			{
				Format = TextureFormat.Depth24PlusStencil8, DepthWriteEnabled = true, DepthCompare = CompareFunction.Always,
				StencilReadMask = 0, StencilWriteMask = 0,
				StencilFront = new StencilFaceState { Compare = CompareFunction.Always, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Keep },
				StencilBack = new StencilFaceState { Compare = CompareFunction.Always, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Keep },
			};
			using (var m = ctx.CreateShaderModuleWgsl("clipw", ClipWriteWgsl))
				cache.AddPipe("clipw", ctx.CreateRenderPipeline("clipw", m, vertexLayouts: [],
					bindGroupLayouts: [[new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform } }]],
					depthStencil: clipDs, writeMask: 0, sampleCount: msaa));
			using (var m = ctx.CreateShaderModuleWgsl("clipc", ClipClearWgsl))
				cache.AddPipe("clipc", ctx.CreateRenderPipeline("clipc", m, vertexLayouts: [], bindGroupLayouts: [],
					depthStencil: clipDs, writeMask: 0, sampleCount: msaa));
			var clipPathDs = new DepthStencilState
			{
				Format = TextureFormat.Depth24PlusStencil8, DepthWriteEnabled = true, DepthCompare = CompareFunction.Always,
				StencilReadMask = 0xFF, StencilWriteMask = 0xFF,
				StencilFront = new StencilFaceState { Compare = CompareFunction.Equal, FailOp = StencilOperation.Zero, DepthFailOp = StencilOperation.Zero, PassOp = StencilOperation.Keep },
				StencilBack = new StencilFaceState { Compare = CompareFunction.Equal, FailOp = StencilOperation.Zero, DepthFailOp = StencilOperation.Zero, PassOp = StencilOperation.Keep },
			};
			using (var m = ctx.CreateShaderModuleWgsl("clippc", ClipPathCoverWgsl))
				cache.AddPipe("clippc", ctx.CreateRenderPipeline("clippc", m, vertexLayouts: [], bindGroupLayouts: [],
					depthStencil: clipPathDs, writeMask: 0, sampleCount: msaa));

			// Path fill (stencil-then-cover) pipelines. ARENA variants take a per-vertex transform index + a
			// read-only storage buffer of per-visual affines (verts are local); plain variants take NDC verts.
			var xformBgl = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex, Buffer = new() { Type = BufferBindingType.ReadOnlyStorage } };
			using (var m = ctx.CreateShaderModuleWgsl("pstencil", _arenaEnabled ? StencilArenaWgsl : StencilWgsl))
				cache.AddPipe("pstencil", ctx.CreateRenderPipeline("pstencil", m,
					vertexLayouts: _arenaEnabled
						? [new VLayout(12, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32, 8, 1)])]
						: [new VLayout(8, [new(VertexFormat.Float32x2, 0, 0)])],
					bindGroupLayouts: _arenaEnabled ? [[xformBgl]] : [],
					depthStencil: StencilState(StencilOperation.Invert, CompareFunction.Always), writeMask: 0, sampleCount: msaa));
			using (var m = ctx.CreateShaderModuleWgsl("pcover", _arenaEnabled ? CoverArenaWgsl : CoverWgsl))
				cache.AddPipe("pcover", ctx.CreateRenderPipeline("pcover", m,
					vertexLayouts: _arenaEnabled
						? [new VLayout(28, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x4, 8, 1), new(VertexFormat.Float32, 24, 2)])]
						: [new VLayout(24, [new(VertexFormat.Float32x2, 0, 0), new(VertexFormat.Float32x4, 8, 1)])],
					bindGroupLayouts: _arenaEnabled ? [[xformBgl]] : [],
					blend: SrcOver, depthStencil: StencilState(StencilOperation.Zero, CompareFunction.NotEqual), sampleCount: msaa));

			cache.Sampler = device.CreateSampler(new()
			{
				MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
				AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
				LodMinClamp = 0, LodMaxClamp = 1, MaxAnisotropy = 1,
			});
			cache.MarkPipelines(msaa);
		}

		var solidPipeline = cache.Pipe("solid");
		var gradPipeline = cache.Pipe("grad");
		var rrPipeline = cache.Pipe("rrect");
		var imgPipeline = cache.Pipe("image");
		var clipWritePipe = cache.Pipe("clipw");
		var clipClearPipe = cache.Pipe("clipc");
		var clipPathCoverPipe = cache.Pipe("clippc");
		var stencilPipeline = cache.Pipe("pstencil");
		var coverPipeline = cache.Pipe("pcover");
		var sampler = cache.Sampler;

		// Pooled vertex buffers — persistent CopyDst buffers rewritten via QueueWriteBuffer each frame (no per-frame alloc).
		Buffer* solidVb = cache.VertexBuffer(ctx, "solid", CollectionsMarshal.AsSpan(_solidVerts));
		Buffer* gradVb = cache.VertexBuffer(ctx, "grad", CollectionsMarshal.AsSpan(_gradVerts));
		Buffer* rrVb = cache.VertexBuffer(ctx, "rr", CollectionsMarshal.AsSpan(_rrVerts));
		// When the slab is active, the visual-fill cmds were patched to address the slab buffers; the clip/acrylic
		// geometry (Clip/Backdrop-referenced, not relocated) lives in the separate dyn buffers.
		// With dirty-range tracking the slab marked exactly which slices changed, so upload only those (no scan).
		Buffer* pathVb = _dirtyEnabled
			? cache.VertexBufferDirty(ctx, "path", CollectionsMarshal.AsSpan(_pathSlab), _dirtyPath)
			: cache.VertexBuffer(ctx, "path", CollectionsMarshal.AsSpan(_slabActive ? _pathSlab : _pathVerts));
		Buffer* coverVb = _dirtyEnabled
			? cache.VertexBufferDirty(ctx, "cover", CollectionsMarshal.AsSpan(_coverSlab), _dirtyCover)
			: cache.VertexBuffer(ctx, "cover", CollectionsMarshal.AsSpan(_slabActive ? _coverSlab : _coverVerts));
		int pathCount = _slabActive ? _pathSlab.Count : _pathVerts.Count;
		int coverCount = _slabActive ? _coverSlab.Count : _coverVerts.Count;
		Buffer* pathDynVb = _slabActive ? cache.VertexBuffer(ctx, "pathdyn", CollectionsMarshal.AsSpan(_pathDyn)) : pathVb;
		Buffer* coverDynVb = _slabActive ? cache.VertexBuffer(ctx, "coverdyn", CollectionsMarshal.AsSpan(_coverDyn)) : coverVb;
		int pathDynCount = _slabActive ? _pathDyn.Count : pathCount;
		int coverDynCount = _slabActive ? _coverDyn.Count : coverCount;

		// Arena: the per-visual transform table (storage buffer) + a bind group the stencil/cover pipelines read.
		GpuBindGroup xformBg = default;
		if (_arenaEnabled && _xforms.Count > 0)
		{
			// Cached across frames (recreated only on buffer realloc) — not a per-frame CreateBindGroup + native
			// GetBindGroupLayout. Owned by the WebGpuTargets cache, so NOT added to this frame's disposables.
			xformBg = cache.XformBindGroup(ctx, stencilPipeline, CollectionsMarshal.AsSpan(_xforms));
		}

		var gradBgs = new GpuBindGroup[_grads.Count];
		for (int i = 0; i < _grads.Count; i++)
		{
			var grad = _grads[i];
			Span<float> gu = [grad.Start.X, grad.Start.Y, grad.End.X, grad.End.Y, grad.Opacity, grad.Radial ? 1f : 0f, grad.Half.X, grad.Half.Y,
				grad.Radii.X, grad.Radii.Y, grad.Radii.Z, grad.Radii.W, grad.Origin.X, grad.Origin.Y, grad.Tile, 0];
			var usig = MemoryMarshal.AsBytes(gu);
			var hit = cache.FindBindGroup(usig, grad.Lut)
				?? CreateGrad(ctx, cache, gradPipeline, sampler, usig, grad.Lut, gu);
			gradBgs[i] = hit.Bg;
		}

		Buffer* imageVb = cache.VertexBuffer(ctx, "image", CollectionsMarshal.AsSpan(_imageVerts));
		var imgBgs = new GpuBindGroup[_images.Count];
		for (int i = 0; i < _images.Count; i++)
		{
			var im = _images[i];
			// Reuse the uploaded texture across frames when the source image is cacheable (Key != 0). Only on a
			// miss do we allocate + upload — so N static images cost N textures total, not N per frame (the cause
			// of the out-of-memory cascade under many images). Key == 0 (e.g. the per-frame FPS overlay) stays
			// per-frame, freed via `disposables`.
			GpuTextureView view;
			if (!cache.TryGetImageTex(im.Key, im.W, im.H, out view))
			{
				var tex = device.CreateTexture(new() { Size = new Extent3D((uint)im.W, (uint)im.H, 1), Dimension = TextureDimension.Dimension2D, Format = TextureFormat.Rgba8Unorm, MipLevelCount = 1, SampleCount = 1, Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst });
				fixed (byte* p = im.Pixels) { queue.WriteTexture(new ImageCopyTexture { Texture = tex, Aspect = TextureAspect.All }, p, (nuint)(im.W * im.H * 4), new TextureDataLayout { BytesPerRow = (uint)(im.W * 4), RowsPerImage = (uint)im.H }, new Extent3D((uint)im.W, (uint)im.H, 1)); }
				view = tex.CreateView();
				if (im.Key != 0) { cache.AddImageTex(im.Key, im.W, im.H, tex, view); }
				else { disposables.Add(tex); disposables.Add(view); }
			}
			// U: row0..row3 (rgba coeffs), off (per-channel offset), flags (opacity, hasMatrix). 96 bytes.
			var m = im.Mat;
			float[] iu = m is null
				? [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, im.Opacity, 0, 0, 0]
				: [m[0], m[1], m[2], m[3], m[5], m[6], m[7], m[8], m[10], m[11], m[12], m[13], m[15], m[16], m[17], m[18],
				   m[4], m[9], m[14], m[19], im.Opacity, 1, 0, 0];
			var ub = ctx.CreateBuffer<float>("iu", BufferUsage.Uniform, iu); disposables.Add(ub);
			imgBgs[i] = ctx.CreateBindGroup(imgPipeline.GetBindGroupLayout(0),
				new BindGroupEntry { Binding = 0, Buffer = ub, Size = 96 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = view });
			disposables.Add(imgBgs[i]);
		}

		var clipBgs = new GpuBindGroup[_clips.Count];
		for (int i = 0; i < _clips.Count; i++)
		{
			var cl = _clips[i];
			if (cl.IsPath) { continue; } // path clips use stencil-then-cover, not a rounded-rect bind group
			float clipL = cl.RectPx.X / (float)_w * 2f - 1f, clipR = cl.RectPx.Z / (float)_w * 2f - 1f;
			float clipT = 1f - cl.RectPx.Y / (float)_h * 2f, clipB = 1f - cl.RectPx.W / (float)_h * 2f;
			Span<float> clu = [clipL, clipT, clipR, clipB, cl.RectPx.X, cl.RectPx.Y, cl.RectPx.Z, cl.RectPx.W,
				cl.RadiiPx.X, cl.RadiiPx.Y, cl.RadiiPx.Z, cl.RadiiPx.W, cl.Exclude ? 1f : 0f, 0f, 0f, 0f];
			var usig = MemoryMarshal.AsBytes(clu);
			var hit = cache.FindBindGroup(usig, default)
				?? CreateClip(ctx, cache, clipWritePipe, usig, clu);
			clipBgs[i] = hit.Bg;
		}

		var clearColor = new Silk.NET.WebGPU.Color(clear.R / 255.0, clear.G / 255.0, clear.B / 255.0, clear.A / 255.0);

		// Clip stack is shared across DrawScene calls so clips carry across segmented-path passes and into
		// shadow/backdrop coverage sub-renders (each pass clears depth, so we re-establish at its start).
		var clipStack = new List<int>();

		// Render-bundle recording plumbing. In direct mode the E* helpers forward straight to the current pass (the
		// only added cost is a well-predicted branch). When bundleRec is set, draws record into a RenderBundle and
		// scissor/stencil-ref (illegal in bundles) flush the current bundle + append a pass-level op to _prog.
		bool bundleRec = false;
		GpuRenderPassEncoder curPass = default;
		GpuRenderBundleEncoder recBundle = default; bool recHas = false;
		void EnsureRec() { if (!recHas) { recBundle = device.CreateRenderBundleEncoder(TextureFormat.Rgba8Unorm, TextureFormat.Depth24PlusStencil8, msaa); recHas = true; } }
		void FlushRec() { if (recHas) { _prog.Add(new POp(2, (uint)_bundles.Count, 0, 0, 0)); _bundles.Add(recBundle.Finish()); recHas = false; } }
		void EPipeline(GpuRenderPipeline p) { if (bundleRec) { EnsureRec(); recBundle.SetPipeline(p); } else { curPass.SetPipeline(p); } }
		void EBind(GpuBindGroup bg) { if (bundleRec) { EnsureRec(); recBundle.SetBindGroup(0, bg, 0, null); } else { curPass.SetBindGroup(0, bg, 0, null); } }
		void EVB(Buffer* b, ulong sz) { if (bundleRec) { EnsureRec(); recBundle.SetVertexBuffer(0, b, 0, sz); } else { curPass.SetVertexBuffer(0, b, 0, sz); } }
		void EDraw(uint vc, uint fv) { if (bundleRec) { EnsureRec(); recBundle.Draw(vc, 1, fv, 0); } else { curPass.Draw(vc, 1, fv, 0); } }
		void EScissor(uint x, uint y, uint w, uint h) { if (bundleRec) { FlushRec(); _prog.Add(new POp(0, x, y, w, h)); } else { curPass.SetScissorRect(x, y, w, h); } }
		void EStencil(uint r) { if (bundleRec) { FlushRec(); _prog.Add(new POp(1, r, 0, 0, 0)); } else { curPass.SetStencilReference(r); } }

		// Draws scene commands [from, to) (skipping Backdrop/Shadow markers, handled by the segmented path).
		void DrawScene(GpuRenderPassEncoder pass, int from, int to)
		{
			curPass = pass;
			Kind? current = null;
			// Last scissor pushed to the GPU — dedup redundant SetScissorRect across the many same-clip commands.
			uint lsx = 0, lsy = 0, lsw = 0, lsh = 0; bool scissorSet = false;
			// Arena: whether group 0 currently holds the transform table. It persists across path stencil/cover draws
			// (same layout), so we only re-bind after a non-path pipeline (gradient/image/clip) disturbs group 0.
			bool xfBound = false;
			// A clip needs a DEPTH MASK only when it's non-rectangular: rounded corners, an exclude (hole), or an
			// arbitrary path. Plain axis-aligned rect clips are fully enforced by the per-command scissor rect
			// (content is DepthWrite=false and tests GreaterEqual against a 0 mask), so they need NO mask draw — the
			// old code drew a full-screen clipWrite for every rect clip that wrote depth nowhere: 390MB/frame wasted.
			static bool NeedsMask(in Clip cl) => cl.IsPath || cl.Exclude || cl.RadiiPx.X != 0 || cl.RadiiPx.Y != 0 || cl.RadiiPx.Z != 0 || cl.RadiiPx.W != 0;
			// Scissor the mask draw to the clip's device bbox (same math as PushCmd). Content under this clip is
			// scissored to the same rect, and clips nest (inner ⊆ outer), so masking within the bbox is sufficient —
			// turning full-screen depth writes into small ones. Returns false for a degenerate/empty rect.
			bool ClipScissor(in Clip cl)
			{
				float l = Math.Clamp(cl.RectPx.X, 0, _w), t = Math.Clamp(cl.RectPx.Y, 0, _h);
				float rt = Math.Clamp(cl.RectPx.Z, 0, _w), bt = Math.Clamp(cl.RectPx.W, 0, _h);
				uint sw = rt > l ? (uint)MathF.Ceiling(rt - l) : 0, sh = bt > t ? (uint)MathF.Ceiling(bt - t) : 0;
				if (sw == 0 || sh == 0) { return false; }
				uint x = (uint)MathF.Floor(l), y = (uint)MathF.Floor(t);
				EScissor(x, y, sw, sh); lsx = x; lsy = y; lsw = sw; lsh = sh; scissorSet = true;
				return true;
			}
			// Draw ONE clip's depth-mask contribution, scissored to its bbox. Additive into the shared mask (no clear).
			void DrawClipMask(int idx)
			{
				var cl = _clips[idx];
				if (!ClipScissor(cl)) { return; }
				if (!cl.IsPath)
				{
					EPipeline(clipWritePipe); EBind(clipBgs[idx]); EDraw(6, 0);
				}
				else
				{
					// even-odd stencil the path, then write depth=1 where stencil==0 (outside the path)
					EStencil(0);
					EPipeline(stencilPipeline); if (_arenaEnabled) { EBind(xformBg); } EVB(pathDynVb, (ulong)(pathDynCount * 4)); EDraw((uint)cl.FanCount, (uint)cl.FanStart);
					EPipeline(clipPathCoverPipe); EDraw(3, 0);
				}
			}
			// Rebuild the whole mask: optionally clear depth to 0, then redraw every MASKING clip in the stack.
			// clear=false at pass start (the pass LoadOp already cleared depth); clear=true after POPPING a masking
			// clip (its depth=1 can't be selectively un-written, so the mask is rebuilt from the survivors).
			void RebuildMask(bool clear)
			{
				long _tc = System.Diagnostics.Stopwatch.GetTimestamp(); _dbgClipN++;
				if (clear) { EScissor(0, 0, _w, _h); EPipeline(clipClearPipe); EDraw(3, 0); }
				foreach (var idx in clipStack) { if (NeedsMask(_clips[idx])) { DrawClipMask(idx); } }
				current = null; xfBound = false; scissorSet = false;
				_dbgClipMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _tc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			}
			// Additively add one just-pushed masking clip to the mask (no clear — existing survivors stay valid).
			void AddMask(int idx) { _dbgClipN++; DrawClipMask(idx); current = null; xfBound = false; scissorSet = false; }
			// Legacy path (UNO_WEBGPU_CLIPOPT off): clear depth + redraw EVERY clip on each change, full-screen.
			// Correct but O(stack) per push/pop — the behavior before the incremental optimization.
			void RebuildMaskFull()
			{
				long _tc = System.Diagnostics.Stopwatch.GetTimestamp(); _dbgClipN++;
				EScissor(0, 0, _w, _h); lsx = 0; lsy = 0; lsw = _w; lsh = _h; scissorSet = true;
				EPipeline(clipClearPipe); EDraw(3, 0);
				foreach (var idx in clipStack)
				{
					var cl = _clips[idx];
					if (!cl.IsPath) { EPipeline(clipWritePipe); EBind(clipBgs[idx]); EDraw(6, 0); }
					else { EStencil(0); EPipeline(stencilPipeline); if (_arenaEnabled) { EBind(xformBg); } EVB(pathDynVb, (ulong)(pathDynCount * 4)); EDraw((uint)cl.FanCount, (uint)cl.FanStart); EPipeline(clipPathCoverPipe); EDraw(3, 0); }
				}
				current = null; xfBound = false;
				_dbgClipMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _tc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			}
			if (_hasClips) { if (_clipIncremental) { RebuildMask(clear: false); } else { RebuildMaskFull(); } } // establish the inherited clip
			for (int ci = from; ci < to; ci++)
			{
				var cmd = _cmds[ci];
				// Rect clip push/pop touches NO depth mask (scissor handles it) → free. A masking clip push adds its
				// mask; a masking clip pop rebuilds (can't un-write one clip's depth). This makes clip cost scale with
				// the number of NON-rect clips, not with every push/pop of the whole stack (was O(stack) each time).
				if (cmd.Kind == Kind.ClipPush) { clipStack.Add(cmd.Aux); if (_clipIncremental) { if (NeedsMask(_clips[cmd.Aux])) { AddMask(cmd.Aux); } } else { RebuildMaskFull(); } continue; }
				if (cmd.Kind == Kind.ClipPop) { int popped = clipStack.Count > 0 ? clipStack[^1] : -1; if (popped >= 0) { clipStack.RemoveAt(clipStack.Count - 1); } if (_clipIncremental) { if (popped >= 0 && NeedsMask(_clips[popped])) { RebuildMask(clear: true); } } else { RebuildMaskFull(); } continue; }
				// Effect markers (Backdrop/Shadow/Layer) are split out by RenderInto and never appear in a scene
				// range, so they're a no-op here.
				if (cmd.Kind is Kind.Backdrop or Kind.Shadow or Kind.Layer or Kind.Mask || cmd.Sw == 0 || cmd.Sh == 0) { continue; }
				// Push the scissor only when it actually changes — most adjacent commands share a clip rect.
				if (!scissorSet || cmd.Sx != lsx || cmd.Sy != lsy || cmd.Sw != lsw || cmd.Sh != lsh)
				{
					EScissor(cmd.Sx, cmd.Sy, cmd.Sw, cmd.Sh);
					lsx = cmd.Sx; lsy = cmd.Sy; lsw = cmd.Sw; lsh = cmd.Sh; scissorSet = true;
				}
				if (cmd.Kind == Kind.PathFill)
				{
					// Coalesce consecutive PathFills (contiguous fan + cover ranges, same scissor) into ONE stencil
					// draw over all fans + ONE cover draw over all cover quads — one draw pair per text run instead
					// of per glyph. Only correct when the glyphs' fills don't overlap (opt-in via UNO_WEBGPU_COALESCE).
					uint fanStart = (uint)cmd.VertexStart, fanCount = (uint)cmd.Count;
					uint cvStart = (uint)cmd.Aux, cvCount = 6;
					if (_coalescePaths)
					{
						while (ci + 1 < to)
						{
							var nxt = _cmds[ci + 1];
							if (nxt.Kind != Kind.PathFill) { break; }
							if (nxt.Sx != cmd.Sx || nxt.Sy != cmd.Sy || nxt.Sw != cmd.Sw || nxt.Sh != cmd.Sh) { break; }
							if ((uint)nxt.VertexStart != fanStart + fanCount || (uint)nxt.Aux != cvStart + cvCount) { break; }
							fanCount += (uint)nxt.Count; cvCount += 6; ci++;
						}
					}
					// Defensive: never issue a draw past the bound buffer (the ≤1-in-flight backpressure should already
					// prevent a torn cmds-vs-buffer frame, but a stray out-of-bounds draw is a hard validation error).
					if (fanStart + fanCount > (uint)pathCount || cvStart + cvCount > (uint)coverCount) { current = null; continue; }
					EStencil(0);
					EPipeline(stencilPipeline);
					if (_arenaEnabled && !xfBound) { EBind(xformBg); xfBound = true; }
					EVB(pathVb, (ulong)(pathCount * 4));
					EDraw(fanCount, fanStart);
					EPipeline(coverPipeline); EVB(coverVb, (ulong)(coverCount * 4)); // xform bind persists from the stencil draw (same layout)
					EDraw(cvCount, cvStart);
					current = null;
					continue;
				}
				if (current != cmd.Kind)
				{
					current = cmd.Kind;
					if (_arenaEnabled) { xfBound = false; } // a non-path pipeline is being set → group 0 no longer holds the transform table
					switch (cmd.Kind)
					{
						case Kind.Solid: EPipeline(solidPipeline); EVB(solidVb, (ulong)(_solidVerts.Count * 4)); break;
						case Kind.Gradient: EPipeline(gradPipeline); EVB(gradVb, (ulong)(_gradVerts.Count * 4)); break;
						case Kind.RoundedRect: EPipeline(rrPipeline); EVB(rrVb, (ulong)(_rrVerts.Count * 4)); break;
						case Kind.Image: EPipeline(imgPipeline); EVB(imageVb, (ulong)(_imageVerts.Count * 4)); break;
					}
				}
				if (cmd.Kind == Kind.Gradient) { EBind(gradBgs[cmd.Aux]); }
				else if (cmd.Kind == Kind.Image) { EBind(imgBgs[cmd.Aux]); }
				// Coalesce a run of adjacent commands that share kind + scissor + bind group and whose vertices are
				// contiguous, into ONE draw call. Per-primitive geometry is already baked into the vertex buffer, so
				// merging the ranges is identical to issuing them separately — it just removes hundreds of FFI calls.
				uint vstart = (uint)cmd.VertexStart, count = (uint)cmd.Count;
				while (ci + 1 < to)
				{
					var nxt = _cmds[ci + 1];
					if (nxt.Kind != cmd.Kind) { break; }
					if (nxt.Sx != cmd.Sx || nxt.Sy != cmd.Sy || nxt.Sw != cmd.Sw || nxt.Sh != cmd.Sh) { break; }
					if ((cmd.Kind == Kind.Gradient || cmd.Kind == Kind.Image) && nxt.Aux != cmd.Aux) { break; }
					if ((uint)nxt.VertexStart != vstart + count) { break; }
					count += (uint)nxt.Count; ci++;
				}
				EDraw(count, vstart); _dbgDraws++;
			}
		}

		// Fast-path draw: record bundles on a structure change, then replay them; otherwise encode directly.
		ulong HashScene()
		{
			ulong h = 1469598103934665603UL;
			void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }
			Mix((ulong)_cmds.Count);
			foreach (var c in _cmds) { Mix((ulong)(byte)c.Kind | ((ulong)(uint)c.VertexStart << 8)); Mix((ulong)(uint)c.Count | ((ulong)(uint)c.Aux << 32)); Mix(c.Sx | ((ulong)c.Sy << 16) | ((ulong)c.Sw << 32) | ((ulong)c.Sh << 48)); }
			Mix((ulong)pathVb); Mix((ulong)coverVb); Mix((ulong)pathDynVb); Mix((ulong)coverDynVb);
			Mix((ulong)solidVb); Mix((ulong)gradVb); Mix((ulong)rrVb); Mix((ulong)imageVb); Mix((ulong)xformBg.Ptr);
			foreach (var bg in gradBgs) { Mix((ulong)bg.Ptr); }
			foreach (var bg in imgBgs) { Mix((ulong)bg.Ptr); }
			foreach (var bg in clipBgs) { Mix((ulong)bg.Ptr); }
			Mix((ulong)_clips.Count);
			foreach (var cl in _clips) { Mix((cl.IsPath ? 1UL : 0UL) | ((ulong)(uint)cl.FanStart << 1) | ((ulong)(uint)cl.FanCount << 33)); }
			return h;
		}
		void ReplayProgram(GpuRenderPassEncoder pass)
		{
			foreach (var op in _prog)
			{
				switch (op.Kind)
				{
					case 0: pass.SetScissorRect(op.A, op.B, op.C, op.D); break;
					case 1: pass.SetStencilReference(op.A); break;
					default: { var b = _bundles[(int)op.A].Ptr; pass.ExecuteBundles(&b, 1); break; }
				}
			}
		}
		void FastDraw(GpuRenderPassEncoder pass)
		{
			if (!_bundleEnabled) { DrawScene(pass, 0, _cmds.Count); return; }
			ulong hh = HashScene();
			if (!_progValid || hh != _progHash)
			{
				foreach (var b in _bundles) { b.Dispose(); }
				_bundles.Clear(); _prog.Clear();
				bundleRec = true; DrawScene(default, 0, _cmds.Count); FlushRec(); bundleRec = false;
				_progHash = hh; _progValid = true;
			}
			ReplayProgram(pass);
		}

		long tSetup = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

		byte[] result = _backdrops.Count == 0 && _shadows.Count == 0 && _layers.Count == 0 && _masks.Count == 0
			? ctx.RenderToRgba(_w, _h, clearColor, FastDraw, depthFormat: TextureFormat.Depth24PlusStencil8, sampleCount: msaa, cache: cache, readback: readback)
			: RenderSegmented(ctx, clearColor, sampler, DrawScene, clipStack, pathVb, pathCount, coverVb, coverCount, stencilPipeline, pathDynVb, pathDynCount, coverDynVb, coverDynCount, xformBg, cache, readback);

		if (perf)
		{
			long tGpu = System.Diagnostics.Stopwatch.GetTimestamp();
			double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			System.Console.WriteLine($"[PERF] wgpu.render setup(pipelines+buffers)={Ms(ts, tSetup):F2} gpu+readback={Ms(tSetup, tGpu):F2} cmds={_cmds.Count} verts[solid={_solidVerts.Count / 6} rr={_rrVerts.Count / 14} grad={_gradVerts.Count / 4} path={_pathVerts.Count / 2} cover={_coverVerts.Count / 6} img={_imageVerts.Count / 4}] ms");
		}

		cache.HasContent = false;
		foreach (var d in disposables) { d.Dispose(); }
		cache.EvictStaleBindGroups();
		cache.EvictStaleImageTex();
		if (transientCache) { cache.Dispose(); }
		return result;
	}

	// Cache-miss factories for the content-keyed bind-group cache: create the GPU resources for one gradient/clip
	// and hand them to the cache (which owns their disposal). On a hit these never run — the chrome's gradients and
	// clips are byte-identical every frame, so they're built once and reused.
	private static unsafe WebGpuTargets.BgEntry CreateGrad(WebGpuContext ctx, WebGpuTargets cache,
		GpuRenderPipeline gradPipeline, GpuSampler sampler, ReadOnlySpan<byte> usig, byte[] lut, ReadOnlySpan<float> gu)
	{
		var device = ctx.Gpu;
		var queue = ctx.GpuQueue;
		var lutTex = device.CreateTexture(new() { Size = new Extent3D(256, 1, 1), Dimension = TextureDimension.Dimension2D, Format = TextureFormat.Rgba8Unorm, MipLevelCount = 1, SampleCount = 1, Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst });
		fixed (byte* p = lut) { queue.WriteTexture(new ImageCopyTexture { Texture = lutTex, Aspect = TextureAspect.All }, p, 256 * 4, new TextureDataLayout { BytesPerRow = 256 * 4, RowsPerImage = 1 }, new Extent3D(256, 1, 1)); }
		var lutView = lutTex.CreateView();
		var ub = ctx.CreateBuffer<float>("gu", BufferUsage.Uniform, gu);
		var bg = ctx.CreateBindGroup(gradPipeline.GetBindGroupLayout(0),
			new BindGroupEntry { Binding = 0, Buffer = ub, Size = 64 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = lutView });
		return cache.AddBindGroup(usig, lut, bg, lutTex, lutView, ub, bg);
	}

	private static unsafe WebGpuTargets.BgEntry CreateClip(WebGpuContext ctx, WebGpuTargets cache,
		GpuRenderPipeline clipWritePipe, ReadOnlySpan<byte> usig, ReadOnlySpan<float> clu)
	{
		var ub = ctx.CreateBuffer<float>("clu", BufferUsage.Uniform, clu);
		var bg = ctx.CreateBindGroup(clipWritePipe.GetBindGroupLayout(0), new BindGroupEntry { Binding = 0, Buffer = ub, Size = 64 });
		return cache.AddBindGroup(usig, null, bg, ub, bg);
	}

	// Multi-pass render for frames containing acrylic backdrops. Renders scene commands in segments
	// split at each Backdrop command; at the split it blurs the content rendered so far (separable
	// gaussian into ping-pong textures) and blits the blurred result into the acrylic region, then
	// resumes the scene (the tint rounded-rect that follows composites over the blurred backdrop).
	private byte[] RenderSegmented(WebGpuContext ctx, Silk.NET.WebGPU.Color clearColor, GpuSampler sampler,
		Action<GpuRenderPassEncoder, int, int> drawScene, List<int> clipStack,
		Buffer* pathVb, int pathVertsCount, Buffer* coverVb, int coverVertsCount, GpuRenderPipeline stencilPipeline,
		Buffer* pathDynVb, int pathDynCount, Buffer* coverDynVb, int coverDynCount, GpuBindGroup xformBg,
		WebGpuTargets? cache = null, bool readback = true)
	{
		bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
		long ts0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
		var wgpu = ctx.Wgpu;
		var disp = cache?.ScratchB ?? new List<IDisposable>(); disp.Clear(); // reused per-frame scratch (option [2])

		BindGroupLayoutEntry[][] bgl = [[
			new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform } },
			new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } },
			new BindGroupLayoutEntry { Binding = 2, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
		]];
		// Two-texture variant for the mask-brush composite (source + mask offscreens).
		BindGroupLayoutEntry[][] bgl2 = [[
			new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Vertex | ShaderStage.Fragment, Buffer = new() { Type = BufferBindingType.Uniform } },
			new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } },
			new BindGroupLayoutEntry { Binding = 2, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
			new BindGroupLayoutEntry { Binding = 3, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
		]];
		// MSAA sample count for the segmented path — MUST match the scene/clip pipelines in Render() (same
		// helper). Every multisampled target resolves to a single-sample twin that effects sample; at S==1 we
		// render straight into the single-sample target (no resolve).
		// Segmented composite pipelines — cached on `cache` (cleared with the main pipelines on a sample-count
		// change), created once. `cache` is always non-null here (Render passes it).
		uint S = MsaaSampleCount();
		var pcache = cache!;
		if (!pcache.HasPipe("blur"))
		{
			using (var m = ctx.CreateShaderModuleWgsl("blur", BlurWgsl))
				pcache.AddPipe("blur", ctx.CreateRenderPipeline("blur", m, vertexLayouts: [], bindGroupLayouts: bgl));
			using (var m = ctx.CreateShaderModuleWgsl("backdrop", BackdropWgsl))
				pcache.AddPipe("backdrop", ctx.CreateRenderPipeline("backdrop", m, vertexLayouts: [], bindGroupLayouts: bgl, blend: SrcOver, sampleCount: S));
			using (var m = ctx.CreateShaderModuleWgsl("coverbackdrop", CoverBackdropWgsl))
				pcache.AddPipe("coverbackdrop", ctx.CreateRenderPipeline("coverbackdrop", m,
					// Cover verts carry a trailing tf index in arena mode (28B) vs 24B plain; the shader reads only
					// pos (offset 0), but the stride MUST match the emitted layout or every vertex after the first misreads.
					vertexLayouts: [new VLayout(_arenaEnabled ? 28ul : 24ul, [new(VertexFormat.Float32x2, 0, 0)])], bindGroupLayouts: bgl,
					blend: SrcOver, depthStencil: StencilState(StencilOperation.Zero, CompareFunction.NotEqual), sampleCount: S));
			using (var m = ctx.CreateShaderModuleWgsl("shadow", ShadowWgsl))
				pcache.AddPipe("shadow", ctx.CreateRenderPipeline("shadow", m, vertexLayouts: [], bindGroupLayouts: bgl, blend: SrcOver, sampleCount: S));
			using (var m = ctx.CreateShaderModuleWgsl("layer", LayerWgsl))
				pcache.AddPipe("layer", ctx.CreateRenderPipeline("layer", m, vertexLayouts: [], bindGroupLayouts: bgl, blend: PremulOver, sampleCount: S));
			using (var m = ctx.CreateShaderModuleWgsl("maskcomp", MaskWgsl))
				pcache.AddPipe("maskcomp", ctx.CreateRenderPipeline("maskcomp", m, vertexLayouts: [], bindGroupLayouts: bgl2, blend: PremulOver, sampleCount: S));
		}
		var blurPipe = pcache.Pipe("blur");
		var backPipe = pcache.Pipe("backdrop");
		var coverBackPipe = pcache.Pipe("coverbackdrop");
		var shadowPipe = pcache.Pipe("shadow");
		var layerPipe = pcache.Pipe("layer");
		var maskPipe = pcache.Pipe("maskcomp");

		// `target` is the single-sample resolve/readback texture (effects sample it, the frame is read from it);
		// scene/composite passes render into the MSAA twin which resolves into `target`. These textures are
		// PERSISTENT via the cache — created per-frame they dominated GPU frame time.
		var targets = cache ?? new WebGpuTargets();
		targets.Ensure(ctx, _w, _h, S, TextureFormat.Depth24PlusStencil8); // always depth: scene pipelines are always-depth
		targets.BeginFrame(); // free the transient texture pool for reuse this frame
		var target = targets.Color;
		var targetView = targets.ColorView;
		Owned<TextureView> depthView = targets.DepthView;
		// At S==1 there is no MSAA twin: render straight into the single-sample target, no resolve.
		TextureView* sceneView = S > 1 ? (TextureView*)targets.MsaaView : (TextureView*)targetView;
		TextureView* sceneResolve = S > 1 ? (TextureView*)targetView : null;

		long ts1 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
		int _g0 = perf ? GC.CollectionCount(0) : 0, _g1 = perf ? GC.CollectionCount(1) : 0, _g2 = perf ? GC.CollectionCount(2) : 0;

		using var encoder = ctx.CreateEncoder("bd-encoder");

		// MSAA color pass: render into `msaaView`, resolve into `resolveView` (null at 1×). Optional depth.
		RenderPassEncoder* BeginColor(string label, TextureView* msaaView, TextureView* resolveView, LoadOp load, bool withDepth, Silk.NET.WebGPU.Color? clear = null)
		{
			var ca = new RenderPassColorAttachment { View = msaaView, ResolveTarget = resolveView, LoadOp = load, StoreOp = StoreOp.Store, ClearValue = clear ?? clearColor };
			var depthAtt = new RenderPassDepthStencilAttachment();
			RenderPassDepthStencilAttachment* dptr = null;
			if (withDepth) // scene pipelines are always-depth, so scene passes always attach the depth/stencil
			{
				depthAtt = new RenderPassDepthStencilAttachment
				{
					View = depthView, DepthLoadOp = LoadOp.Clear, DepthClearValue = 0f, DepthStoreOp = StoreOp.Store,
					StencilLoadOp = LoadOp.Clear, StencilClearValue = 0, StencilStoreOp = StoreOp.Store,
				};
				dptr = &depthAtt;
			}
			var desc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &ca, DepthStencilAttachment = dptr };
			RenderPassTimestampWrites tw = default;
			if (targets.Timer is { } tmr && tmr.TryPass(label, out var tb, out var te)) { tw = tmr.Writes(tb, te); desc.TimestampWrites = &tw; }
			_dbgPasses++; long _tb = System.Diagnostics.Stopwatch.GetTimestamp(); var _rp = wgpu.CommandEncoderBeginRenderPass(encoder, ref desc); _dbgPassMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _tb) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; return _rp;
		}
		// Single-sample color pass (no MSAA, no depth) — used for the blur/downsample levels.
		RenderPassEncoder* BeginColorSS(string label, TextureView* view, LoadOp load, Silk.NET.WebGPU.Color? clear = null)
		{
			var ca = new RenderPassColorAttachment { View = view, LoadOp = load, StoreOp = StoreOp.Store, ClearValue = clear ?? clearColor };
			var desc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &ca, DepthStencilAttachment = null };
			RenderPassTimestampWrites tw = default;
			if (targets.Timer is { } tmr && tmr.TryPass(label, out var tb, out var te)) { tw = tmr.Writes(tb, te); desc.TimestampWrites = &tw; }
			_dbgPasses++; long _tb = System.Diagnostics.Stopwatch.GetTimestamp(); var _rp = wgpu.CommandEncoderBeginRenderPass(encoder, ref desc); _dbgPassMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _tb) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; return _rp;
		}
		void EndPass(RenderPassEncoder* p) { long _te = System.Diagnostics.Stopwatch.GetTimestamp(); wgpu.RenderPassEncoderEnd(p); wgpu.RenderPassEncoderRelease(p); _dbgPassMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _te) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

		// One blur/downsample pass: render a fullscreen tri into dstView (dstW×dstH), sampling `input`.
		// step=0 with a half-size output → exact 2x2 box downsample; non-zero step → 9-tap gaussian.
		Owned<TextureView> BlurPass(Owned<TextureView> input, uint dstW, uint dstH, float stepX, float stepY)
		{
			var view = targets.Rent(ctx, dstW, dstH, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, 1, TextureFormat.Rgba8Unorm);
			var u = ctx.CreateBuffer<float>("bu", BufferUsage.Uniform, [1f / dstW, 1f / dstH, stepX, stepY]); disp.Add(u);
			var bg = ctx.CreateBindGroup(blurPipe.GetBindGroupLayout(0),
				new BindGroupEntry { Binding = 0, Buffer = u, Size = 16 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = input });
			disp.Add(bg);
			var p = BeginColorSS("blur", view, LoadOp.Clear);
			var w = new GpuRenderPassEncoder(wgpu, p); w.SetPipeline(blurPipe); w.SetBindGroup(0, bg, 0, null); w.Draw(3, 1, 0, 0);
			EndPass(p);
			return view;
		}

		// Recursively render commands [from,to) onto a target (tgtMsaa, resolving to tgtResolve; tgtSingle is the
		// single-sample view that effects sample). The range is split at effect markers; each Backdrop/Shadow/Layer
		// renders its OWN subtree to a sub-texture via a RECURSIVE call, so nested effects (an acrylic inside a
		// menu's shadow, or inside an opacity layer, etc.) compose correctly instead of being dropped.
		void RenderInto(TextureView* tgtMsaa, TextureView* tgtResolve, Owned<TextureView> tgtSingle, int from, int to, bool clearFirst, Silk.NET.WebGPU.Color clearVal)
		{
			int clipBase = clipStack.Count;
			bool first = clearFirst;
			int i = from;
			while (true)
			{
				int next = i;
				while (next < to && _cmds[next].Kind != Kind.Backdrop && _cmds[next].Kind != Kind.Shadow && _cmds[next].Kind != Kind.Layer && _cmds[next].Kind != Kind.Mask) { next++; }

				// Scene segment [i, next): no effect markers. Skip it if it draws nothing (still replay its clip
				// push/pop so the shared stack stays correct); the first segment always renders (it clears).
				bool hasDraws = false;
				for (int ci = i; ci < next; ci++)
				{
					var k = _cmds[ci].Kind;
					if (k is Kind.Solid or Kind.Gradient or Kind.RoundedRect or Kind.Image or Kind.PathFill) { hasDraws = true; break; }
				}
				if (hasDraws || first)
				{
					var sp = BeginColor("scene", tgtMsaa, tgtResolve, first ? LoadOp.Clear : LoadOp.Load, true, clearVal);
					drawScene(new GpuRenderPassEncoder(wgpu, sp), i, next);
					EndPass(sp);
					first = false;
				}
				else
				{
					for (int ci = i; ci < next; ci++)
					{
						if (_cmds[ci].Kind == Kind.ClipPush) { clipStack.Add(_cmds[ci].Aux); }
						else if (_cmds[ci].Kind == Kind.ClipPop && clipStack.Count > clipBase) { clipStack.RemoveAt(clipStack.Count - 1); }
					}
					_dbgEmptySkip++;
				}
				if (next >= to) { break; }

				var bcmd = _cmds[next];
				if (bcmd.Kind == Kind.Shadow)
				{
					var sh = _shadows[bcmd.Aux];
					int gEnd = sh.GroupEnd < 0 ? to : sh.GroupEnd;
					if (bcmd.Sw == 0 || bcmd.Sh == 0 || gEnd <= sh.GroupStart) { i = next + 1; continue; }
					// 1. Render the caster subtree (incl. nested effects) into a transparent coverage texture.
					var covSingle = targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, 1, TextureFormat.Rgba8Unorm);
					Owned<TextureView> covMsaa = S > 1 ? targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment, S, TextureFormat.Rgba8Unorm) : covSingle;
					RenderInto((TextureView*)covMsaa, S > 1 ? (TextureView*)covSingle : null, covSingle, sh.GroupStart, gEnd, true, new Silk.NET.WebGPU.Color(0, 0, 0, 0));
					// 2. Blur the coverage (downsample pyramid + small gaussian).
					int slevels = Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(sh.Sigma, 1f) / 3f)), 1, 5);
					var scur = covSingle; uint scw = _w, sch = _h;
					int srem = slevels;
					while (srem >= 2) { uint nw = Math.Max(1, scw / 4), nh = Math.Max(1, sch / 4); scur = BlurPass(scur, nw, nh, -4f, 0f); scw = nw; sch = nh; srem -= 2; }
					if (srem == 1) { uint nw = Math.Max(1, scw / 2), nh = Math.Max(1, sch / 2); scur = BlurPass(scur, nw, nh, -2f, 0f); scw = nw; sch = nh; }
					scur = BlurPass(scur, scw, sch, 1.5f / scw, 0f);
					scur = BlurPass(scur, scw, sch, 0f, 1.5f / sch);
					// 3. Composite shadowColor x blurred coverage (sampled at pixel - offset) over the region.
					float sclipL = bcmd.Sx / (float)_w * 2f - 1f, sclipR = (bcmd.Sx + bcmd.Sw) / (float)_w * 2f - 1f;
					float sclipT = 1f - bcmd.Sy / (float)_h * 2f, sclipB = 1f - (bcmd.Sy + bcmd.Sh) / (float)_h * 2f;
					var sc = sh.Color;
					float[] su = [sclipL, sclipT, sclipR, sclipB, 1f / _w, 1f / _h, sh.Dx, sh.Dy, sc.R / 255f, sc.G / 255f, sc.B / 255f, sc.A / 255f];
					var sub = ctx.CreateBuffer<float>("shu", BufferUsage.Uniform, su); disp.Add(sub);
					var sbg = ctx.CreateBindGroup(shadowPipe.GetBindGroupLayout(0),
						new BindGroupEntry { Binding = 0, Buffer = sub, Size = 48 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = scur });
					disp.Add(sbg);
					var spp = BeginColor("shadow.comp", tgtMsaa, tgtResolve, LoadOp.Load, false);
					var sww = new GpuRenderPassEncoder(wgpu, spp);
					sww.SetScissorRect(bcmd.Sx, bcmd.Sy, bcmd.Sw, bcmd.Sh);
					sww.SetPipeline(shadowPipe); sww.SetBindGroup(0, sbg, 0, null); sww.Draw(6, 1, 0, 0);
					EndPass(spp);
					i = sh.GroupStart; // then render the caster content onto THIS target (continue the walk)
					continue;
				}

				if (bcmd.Kind == Kind.Layer)
				{
					var ly = _layers[bcmd.Aux];
					int gEnd = ly.GroupEnd < 0 ? to : ly.GroupEnd;
					if (gEnd <= ly.GroupStart || bcmd.Sw == 0 || bcmd.Sh == 0) { i = gEnd; continue; }
					// 1. Render the group (incl. nested effects) into a transparent offscreen — blends ONCE here.
					var lySingle = targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, 1, TextureFormat.Rgba8Unorm);
					Owned<TextureView> lyMsaa = S > 1 ? targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment, S, TextureFormat.Rgba8Unorm) : lySingle;
					RenderInto((TextureView*)lyMsaa, S > 1 ? (TextureView*)lySingle : null, lySingle, ly.GroupStart, gEnd, true, new Silk.NET.WebGPU.Color(0, 0, 0, 0));
					// 2. Composite the premultiplied group over the target at the group opacity.
					float lL = bcmd.Sx / (float)_w * 2f - 1f, lR = (bcmd.Sx + bcmd.Sw) / (float)_w * 2f - 1f;
					float lT = 1f - bcmd.Sy / (float)_h * 2f, lB = 1f - (bcmd.Sy + bcmd.Sh) / (float)_h * 2f;
					float[] lu = [lL, lT, lR, lB, 1f / _w, 1f / _h, ly.Opacity, 0f];
					var lub = ctx.CreateBuffer<float>("lyu", BufferUsage.Uniform, lu); disp.Add(lub);
					var lbg = ctx.CreateBindGroup(layerPipe.GetBindGroupLayout(0),
						new BindGroupEntry { Binding = 0, Buffer = lub, Size = 32 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = lySingle });
					disp.Add(lbg);
					var lcp = BeginColor("layer.comp", tgtMsaa, tgtResolve, LoadOp.Load, false);
					var lww = new GpuRenderPassEncoder(wgpu, lcp);
					lww.SetScissorRect(bcmd.Sx, bcmd.Sy, bcmd.Sw, bcmd.Sh);
					lww.SetPipeline(layerPipe); lww.SetBindGroup(0, lbg, 0, null); lww.Draw(6, 1, 0, 0);
					EndPass(lcp);
					i = gEnd;
					continue;
				}

				if (bcmd.Kind == Kind.Mask)
				{
					var mk = _masks[bcmd.Aux];
					int mEnd = mk.MaskEnd < 0 ? to : mk.MaskEnd;
					int mMid = mk.MaskStart < 0 ? mEnd : mk.MaskStart;
					if (mEnd <= mk.SourceStart || bcmd.Sw == 0 || bcmd.Sh == 0) { i = mEnd; continue; }
					// 1. Render the SOURCE group into a transparent offscreen (blends once, premultiplied).
					var srcSingle = targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, 1, TextureFormat.Rgba8Unorm);
					Owned<TextureView> srcMsaa = S > 1 ? targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment, S, TextureFormat.Rgba8Unorm) : srcSingle;
					RenderInto((TextureView*)srcMsaa, S > 1 ? (TextureView*)srcSingle : null, srcSingle, mk.SourceStart, mMid, true, new Silk.NET.WebGPU.Color(0, 0, 0, 0));
					// 2. Render the MASK group into its own transparent offscreen.
					var mskSingle = targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, 1, TextureFormat.Rgba8Unorm);
					Owned<TextureView> mskMsaa = S > 1 ? targets.Rent(ctx, _w, _h, TextureUsage.RenderAttachment, S, TextureFormat.Rgba8Unorm) : mskSingle;
					RenderInto((TextureView*)mskMsaa, S > 1 ? (TextureView*)mskSingle : null, mskSingle, mMid, mEnd, true, new Silk.NET.WebGPU.Color(0, 0, 0, 0));
					// 3. Composite source × mask.alpha over the target (DstIn, then PremulOver).
					float mL = bcmd.Sx / (float)_w * 2f - 1f, mR = (bcmd.Sx + bcmd.Sw) / (float)_w * 2f - 1f;
					float mT = 1f - bcmd.Sy / (float)_h * 2f, mB = 1f - (bcmd.Sy + bcmd.Sh) / (float)_h * 2f;
					float[] mu = [mL, mT, mR, mB, 1f / _w, 1f / _h, 0f, 0f];
					var mub = ctx.CreateBuffer<float>("mku", BufferUsage.Uniform, mu); disp.Add(mub);
					var mbg = ctx.CreateBindGroup(maskPipe.GetBindGroupLayout(0),
						new BindGroupEntry { Binding = 0, Buffer = mub, Size = 32 }, new BindGroupEntry { Binding = 1, Sampler = sampler },
						new BindGroupEntry { Binding = 2, TextureView = srcSingle }, new BindGroupEntry { Binding = 3, TextureView = mskSingle });
					disp.Add(mbg);
					var mcp = BeginColor("mask.comp", tgtMsaa, tgtResolve, LoadOp.Load, false);
					var mww = new GpuRenderPassEncoder(wgpu, mcp);
					mww.SetScissorRect(bcmd.Sx, bcmd.Sy, bcmd.Sw, bcmd.Sh);
					mww.SetPipeline(maskPipe); mww.SetBindGroup(0, mbg, 0, null); mww.Draw(6, 1, 0, 0);
					EndPass(mcp);
					i = mEnd;
					continue;
				}

				if (bcmd.Sw > 0 && bcmd.Sh > 0)
				{
					var bd = _backdrops[bcmd.Aux];
					var sigma = bd.Sigma;
					// Downsample pyramid (exact 2x2 box per halving) + small gaussian, then smooth upscale on composite.
					int levels = Math.Clamp((int)MathF.Round(MathF.Log2(MathF.Max(sigma, 1f) / 3f)), 1, 5);
					var cur = tgtSingle; uint cw = _w, ch = _h;
					int rem = levels;
					while (rem >= 2) { uint nw = Math.Max(1, cw / 4), nh = Math.Max(1, ch / 4); cur = BlurPass(cur, nw, nh, -4f, 0f); cw = nw; ch = nh; rem -= 2; }
					if (rem == 1) { uint nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2); cur = BlurPass(cur, nw, nh, -2f, 0f); cw = nw; ch = nh; }
					cur = BlurPass(cur, cw, ch, 1.5f / cw, 0f);
					cur = BlurPass(cur, cw, ch, 0f, 1.5f / ch);

					var lum = bd.Luminosity;
					if (!bd.IsPath)
					{
						float clipL = bcmd.Sx / (float)_w * 2f - 1f, clipR = (bcmd.Sx + bcmd.Sw) / (float)_w * 2f - 1f;
						float clipT = 1f - bcmd.Sy / (float)_h * 2f, clipB = 1f - (bcmd.Sy + bcmd.Sh) / (float)_h * 2f;
						float[] u =
						[
							clipL, clipT, clipR, clipB,
							1f / _w, 1f / _h, bd.Noise * 0.15f, 0f,
							lum.R / 255f, lum.G / 255f, lum.B / 255f, lum.A / 255f,
							bcmd.Sx, bcmd.Sy, bcmd.Sx + bcmd.Sw, bcmd.Sy + bcmd.Sh,
							bd.RadiiPx.X, bd.RadiiPx.Y, bd.RadiiPx.Z, bd.RadiiPx.W,
						];
						var ub = ctx.CreateBuffer<float>("bdu", BufferUsage.Uniform, u); disp.Add(ub);
						var bg3 = ctx.CreateBindGroup(backPipe.GetBindGroupLayout(0),
							new BindGroupEntry { Binding = 0, Buffer = ub, Size = 80 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = cur });
						disp.Add(bg3);
						var p3 = BeginColor("backdrop.comp", tgtMsaa, tgtResolve, LoadOp.Load, false);
						var w3 = new GpuRenderPassEncoder(wgpu, p3);
						w3.SetScissorRect(bcmd.Sx, bcmd.Sy, bcmd.Sw, bcmd.Sh);
						w3.SetPipeline(backPipe); w3.SetBindGroup(0, bg3, 0, null); w3.Draw(6, 1, 0, 0);
						EndPass(p3);
					}
					else
					{
						float[] cu = [1f / _w, 1f / _h, bd.Noise * 0.15f, 0f, lum.R / 255f, lum.G / 255f, lum.B / 255f, lum.A / 255f];
						var cub = ctx.CreateBuffer<float>("cbu", BufferUsage.Uniform, cu); disp.Add(cub);
						var cbg = ctx.CreateBindGroup(coverBackPipe.GetBindGroupLayout(0),
							new BindGroupEntry { Binding = 0, Buffer = cub, Size = 32 }, new BindGroupEntry { Binding = 1, Sampler = sampler }, new BindGroupEntry { Binding = 2, TextureView = cur });
						disp.Add(cbg);
						var pp = BeginColor("backdrop.pathcomp", tgtMsaa, tgtResolve, LoadOp.Load, true);
						var wp = new GpuRenderPassEncoder(wgpu, pp);
						wp.SetScissorRect(bcmd.Sx, bcmd.Sy, bcmd.Sw, bcmd.Sh);
						wp.SetStencilReference(0);
						// Arena pstencil requires the transform table at group 0 (verts are local + a per-vertex tf index) —
						// every other pstencil draw binds it; this path-masked-backdrop stencil is the one that didn't.
						wp.SetPipeline(stencilPipeline); if (_arenaEnabled) { wp.SetBindGroup(0, xformBg, 0, null); } wp.SetVertexBuffer(0, pathDynVb, 0, (ulong)(pathDynCount * 4)); wp.Draw((uint)bd.FanCount, 1, (uint)bd.FanStart, 0);
						wp.SetPipeline(coverBackPipe); wp.SetBindGroup(0, cbg, 0, null); wp.SetVertexBuffer(0, coverDynVb, 0, (ulong)(coverDynCount * 4)); wp.Draw(6, 1, (uint)bd.CoverStart, 0);
						EndPass(pp);
					}
				}

				i = next + 1;
			}
			while (clipStack.Count > clipBase) { clipStack.RemoveAt(clipStack.Count - 1); }
		}

		RenderInto(sceneView, sceneResolve, targetView, 0, _cmds.Count, true, clearColor);


		long tEnc = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; // after all passes encoded, before submit
		targets.Timer?.Resolve(encoder);
		ctx.FinishAndSubmit(encoder);
		long tSub = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
		targets.Timer?.ReadAndLog();
		var bytes = readback ? TextureReadback.ReadRgba8(ctx, target, _w, _h) : Array.Empty<byte>();
		if (perf)
		{
			long ts2 = System.Diagnostics.Stopwatch.GetTimestamp();
			double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				System.Console.WriteLine($"[PERF] seg encode={Ms(ts1, tEnc):F2} passOvh={_dbgPassMs:F2} emptySkip={_dbgEmptySkip} hash={_dbgHashMs:F2} clip={_dbgClipMs:F2} clipN={_dbgClipN} submit={Ms(tEnc, tSub):F2} readback={Ms(tSub, ts2):F2} ms draws={_dbgDraws} passes={_dbgPasses} cmds={_cmds.Count} bd={_backdrops.Count} sh={_shadows.Count} ly={_layers.Count} img={_images.Count} gc0={GC.CollectionCount(0)-_g0} gc1={GC.CollectionCount(1)-_g1} gc2={GC.CollectionCount(2)-_g2} resCreate={Common.Rendering.ResCreateMs:F2}");
		}
		foreach (var d in disp) { d.Dispose(); }
		if (cache is null) { targets.Dispose(); } // transient (uncached) caller owns nothing persistent
		return bytes;
	}
}
