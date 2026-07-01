#nullable enable

using System.Collections.Generic;

namespace WebGpuExperiment;

// Per-visual STABLE slice allocator for the arena path/cover vertex buffers (UNO_WEBGPU_SLAB).
//
// Goal: when ONE visual's content changes (even drastically — different vertex count), re-upload only THAT
// visual's bytes, not the whole buffer. The monolithic "append in tree order" layout can't do this: a vertex-count
// change shifts every subsequent visual, so the partial-upload diff covers everything after the change. A slab
// gives each visual a fixed offset+capacity, so a content change rewrites its slice IN PLACE (stable offset) and a
// growth past capacity reallocs only that slice (everyone else stays put) — leaving the rest of the buffer
// byte-identical, so the partial-upload skips it.
//
// Threading: this holds only CPU allocation METADATA (offsets/free-list), mutated solely on the UI thread during
// the build, and the builds of the two double-buffered draw lists run sequentially (never concurrently). The two
// draw lists share ONE allocator so a visual lands at the SAME offset in both their buffers — that keeps the
// render-thread shadow diff valid across the alternating draw lists. The actual vertex DATA lives in each draw
// list's own buffer (written at these shared offsets), so there is no shared mutable data buffer to race on.
internal sealed class WebGpuVertexSlab
{
	internal struct Slice { public int Off; public int Cap; public int Len; } // vertices (caller's stride); offsets stay stride-aligned

	private readonly Dictionary<long, Slice> _path = new();
	private readonly Dictionary<long, Slice> _cover = new();
	// Free regions per buffer, kept sorted by capacity for a cheap best-fit. Coalescing is skipped (UI churn is
	// low and slices are similar-sized), so fragmentation is bounded by slack reuse.
	private readonly List<(int off, int cap)> _pathFree = new();
	private readonly List<(int off, int cap)> _coverFree = new();
	private int _pathCap, _coverCap; // total high-water (= the per-draw-list buffer length we size to)

	internal int PathCapacity => _pathCap;
	internal int CoverCapacity => _coverCap;

	internal void Reset()
	{
		_path.Clear(); _cover.Clear(); _pathFree.Clear(); _coverFree.Clear(); _pathCap = 0; _coverCap = 0;
	}

	// Reserve a slice of <paramref name="verts"/> for visual <paramref name="id"/>, reusing its existing slot when it
	// still fits (so the offset is stable across frames) and reallocating only when it grows past capacity. Returns
	// the VERTEX offset to write at (caller multiplies by its stride). Working in vertices keeps offsets aligned.
	internal int EnsurePath(long id, int verts) => Ensure(_path, _pathFree, ref _pathCap, id, verts);
	internal int EnsureCover(long id, int verts) => Ensure(_cover, _coverFree, ref _coverCap, id, verts);

	internal int PathLen(long id) => _path.TryGetValue(id, out var s) ? s.Len : 0;
	internal int CoverLen(long id) => _cover.TryGetValue(id, out var s) ? s.Len : 0;
	internal int PathOff(long id) => _path.TryGetValue(id, out var s) ? s.Off : 0;
	internal int CoverOff(long id) => _cover.TryGetValue(id, out var s) ? s.Off : 0;

	internal void Free(long id)
	{
		if (_path.TryGetValue(id, out var p)) { _pathFree.Add((p.Off, p.Cap)); _path.Remove(id); }
		if (_cover.TryGetValue(id, out var c)) { _coverFree.Add((c.Off, c.Cap)); _cover.Remove(id); }
	}

	private readonly List<long> _toFree = new();

	// Free the slices of visuals not present in <paramref name="live"/> (this frame's ids) so removed/virtualized-out
	// content returns its capacity to the free list instead of leaking. Called once per build on the UI thread.
	internal void RetainOnly(HashSet<long> live)
	{
		_toFree.Clear();
		foreach (var id in _path.Keys) { if (!live.Contains(id)) { _toFree.Add(id); } }
		foreach (var id in _cover.Keys) { if (!live.Contains(id) && !_path.ContainsKey(id)) { _toFree.Add(id); } }
		foreach (var id in _toFree) { Free(id); }
	}

	private static int Ensure(Dictionary<long, Slice> map, List<(int off, int cap)> free, ref int cap, long id, int floats)
	{
		if (map.TryGetValue(id, out var s))
		{
			if (s.Cap >= floats) { s.Len = floats; map[id] = s; return s.Off; }
			free.Add((s.Off, s.Cap)); // outgrew → return old region, reallocate below
		}
		int want = floats + (floats >> 1); // 1.5× slack so small growth doesn't realloc next frame
		int bestI = -1, bestCap = int.MaxValue;
		for (int i = 0; i < free.Count; i++)
		{
			if (free[i].cap >= floats && free[i].cap < bestCap) { bestI = i; bestCap = free[i].cap; }
		}
		int off, capAlloc;
		if (bestI >= 0) { off = free[bestI].off; capAlloc = free[bestI].cap; free.RemoveAt(bestI); }
		else { off = cap; capAlloc = want; cap += want; }
		map[id] = new Slice { Off = off, Cap = capAlloc, Len = floats };
		return off;
	}
}
