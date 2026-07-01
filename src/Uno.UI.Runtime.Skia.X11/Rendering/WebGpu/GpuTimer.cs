#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Common;

/// <summary>
/// EXPERIMENTAL GPU-side timing via WebGPU timestamp queries (opt-in: UNO_WEBGPU_GPUTIME=1, and only when the
/// adapter advertises the <c>timestamp-query</c> feature — software adapters like lavapipe don't, so this is a
/// no-op there). Each render pass writes a begin/end timestamp (the portable per-PASS granularity — wgpu-native
/// 0.19 / Silk 2.23 exposes no per-draw-inside-pass write), the set is resolved into a buffer, read back, and the
/// per-pass deltas logged. Because every expensive pass in the segmented path is a single draw (blur level,
/// composite, layer offscreen), per-pass timing is effectively per-draw for everything that matters.
///
/// The readback is DEFERRED and NON-BLOCKING: each frame resolves into its own slot of a small ring of staging
/// buffers and maps it async; the result is collected a few frames later once the map has completed on its own.
/// A blocking readback (DevicePoll wait:true) every frame added a ~37ms per-frame stall on the Intel UHD 620
/// Vulkan driver (its map/poll round-trip floor) — which dwarfed the actual ~0.4ms of GPU work and made the tool
/// unusable as a live monitor. The timestamps themselves are unaffected; only when we READ them is deferred.
/// </summary>
public sealed unsafe class GpuTimer : IDisposable
{
	private const int Depth = 3; // ring depth: a slot is read ~Depth frames after it's resolved (GPU is done by then)

	private readonly WebGpuContext _ctx;
	private readonly WebGPU _wgpu;
	private readonly QuerySet* _querySet;
	private readonly Buffer* _resolve;   // QueryResolve | CopySrc (single: GPU-ordered, consumed each frame)
	private readonly uint _capacity;     // total timestamps (2 per pass)
	private int _next;                   // next free timestamp index this frame
	private readonly List<(string label, int begin, int end)> _passes = new();

	// Per-slot ring state. Staging buffers held as nint (C# can't hold a managed array of pointers).
	private readonly nint[] _staging = new nint[Depth];
	private readonly int[] _slotCount = new int[Depth];
	private readonly List<(string label, int begin, int end)>[] _slotPasses = new List<(string, int, int)>[Depth];
	private readonly bool[] _pendingMap = new bool[Depth];
	private readonly bool[] _inFlight = new bool[Depth];
	private readonly BufferMapAsyncStatus[] _status = new BufferMapAsyncStatus[Depth];
	private readonly PfnBufferMapCallback[] _cb = new PfnBufferMapCallback[Depth];
	private int _cur; // slot the current frame resolves into

	public GpuTimer(WebGpuContext ctx, uint capacity = 512)
	{
		_ctx = ctx;
		_wgpu = ctx.Wgpu;
		_capacity = capacity;
		var qd = new QuerySetDescriptor { Type = QueryType.Timestamp, Count = capacity };
		_querySet = _wgpu.DeviceCreateQuerySet(ctx.Device, ref qd);
		var rd = new BufferDescriptor { Size = capacity * sizeof(ulong), Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc };
		_resolve = _wgpu.DeviceCreateBuffer(ctx.Device, ref rd);
		for (int i = 0; i < Depth; i++)
		{
			var sd = new BufferDescriptor { Size = capacity * sizeof(ulong), Usage = BufferUsage.CopyDst | BufferUsage.MapRead };
			_staging[i] = (nint)_wgpu.DeviceCreateBuffer(ctx.Device, ref sd);
			_slotPasses[i] = new List<(string, int, int)>();
			int slot = i; // capture by value for the callback
			_cb[i] = new PfnBufferMapCallback((s, _) => _status[slot] = s);
		}
	}

	public void BeginFrame()
	{
		_next = 0;
		_passes.Clear();
	}

	/// <summary>Reserve a begin/end timestamp pair for a pass; false if the per-frame capacity is exhausted.</summary>
	public bool TryPass(string label, out uint beginIdx, out uint endIdx)
	{
		if (_next + 2 > _capacity)
		{
			beginIdx = endIdx = 0;
			return false;
		}
		beginIdx = (uint)_next;
		endIdx = (uint)(_next + 1);
		_next += 2;
		_passes.Add((label, (int)beginIdx, (int)endIdx));
		return true;
	}

	/// <summary>The descriptor sub-struct a render pass points at to record begin/end into the reserved pair.</summary>
	public RenderPassTimestampWrites Writes(uint beginIdx, uint endIdx)
		=> new RenderPassTimestampWrites { QuerySet = _querySet, BeginningOfPassWriteIndex = beginIdx, EndOfPassWriteIndex = endIdx };

	/// <summary>Encode the resolve (query set → resolve buffer → this frame's ring staging slot) before finish.</summary>
	public void Resolve(CommandEncoder* encoder)
	{
		if (_next == 0)
		{
			return;
		}
		// The slot we're about to write must not still be mapped from a prior use. With Depth=3 and ~0.4ms of GPU
		// work this never actually blocks; the drain is only a safety net if the GPU ever falls >Depth frames behind.
		if (_inFlight[_cur] || _pendingMap[_cur])
		{
			DrainSlot(_cur);
		}
		var staging = (Buffer*)_staging[_cur];
		_wgpu.CommandEncoderResolveQuerySet(encoder, _querySet, 0, (uint)_next, _resolve, 0);
		_wgpu.CommandEncoderCopyBufferToBuffer(encoder, _resolve, 0, staging, 0, (ulong)_next * sizeof(ulong));
		_slotCount[_cur] = _next;
		_slotPasses[_cur].Clear();
		_slotPasses[_cur].AddRange(_passes);
		_pendingMap[_cur] = true;
	}

	/// <summary>
	/// After submit: kick the async map for this frame's slot, pump completed callbacks NON-blocking, and log any
	/// slot whose map has finished (a few frames old). Never blocks on the GPU, so it won't stall the frame.
	/// </summary>
	public void ReadAndLog()
	{
		try
		{
			// Kick the async map for the slot we just resolved into.
			if (_pendingMap[_cur])
			{
				var staging = (Buffer*)_staging[_cur];
				_status[_cur] = (BufferMapAsyncStatus)(-1);
				_wgpu.BufferMapAsync(staging, MapMode.Read, 0, (nuint)((ulong)_slotCount[_cur] * sizeof(ulong)), _cb[_cur], null);
				_inFlight[_cur] = true;
				_pendingMap[_cur] = false;
			}

			_cur = (_cur + 1) % Depth;

			// Non-blocking pump: fires map callbacks for work the GPU has already finished.
			_ctx.Native.DevicePoll(_ctx.Device, false, null);

			// Log every slot whose map completed this pump (normally the oldest one, ~Depth frames back).
			for (int s = 0; s < Depth; s++)
			{
				if (_inFlight[s] && _status[s] == BufferMapAsyncStatus.Success)
				{
					LogSlot(s);
					_wgpu.BufferUnmap((Buffer*)_staging[s]);
					_inFlight[s] = false;
				}
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[PERF][GPU] timestamp read failed: {ex.Message}");
		}
	}

	private void LogSlot(int slot)
	{
		ulong size = (ulong)_slotCount[slot] * sizeof(ulong);
		ulong* t = (ulong*)_wgpu.BufferGetConstMappedRange((Buffer*)_staging[slot], 0, (nuint)size);
		if (t is null)
		{
			return;
		}
		// Aggregate by label (many passes share a label, e.g. "blur") so the line stays readable.
		var agg = new Dictionary<string, (double ms, int n)>();
		double totalMs = 0;
		ulong firstBegin = ulong.MaxValue, lastEnd = 0;
		foreach (var (label, b, e) in _slotPasses[slot])
		{
			double ms = t[e] >= t[b] ? (t[e] - t[b]) / 1_000_000.0 : 0; // timestamps are ns; period folded in by wgpu
			totalMs += ms;
			if (t[b] < firstBegin) { firstBegin = t[b]; }
			if (t[e] > lastEnd) { lastEnd = t[e]; }
			agg.TryGetValue(label, out var v); // v defaults to (0,0) when absent
			agg[label] = (v.ms + ms, v.n + 1);
		}
		// span = first-pass-begin → last-pass-end: the TRUE GPU wall-clock for the frame, INCLUDING the gaps
		// between passes (barriers, tile flushes, texture load/store bandwidth) that the per-pass sum misses.
		// span >> totalGPU ⇒ the cost is inter-pass overhead/bandwidth, not the draws themselves.
		double spanMs = lastEnd >= firstBegin ? (lastEnd - firstBegin) / 1_000_000.0 : 0;
		var sb = new StringBuilder("[PERF][GPU] ");
		foreach (var kv in agg)
		{
			sb.Append($"{kv.Key}={kv.Value.ms:F3}ms×{kv.Value.n} ");
		}
		sb.Append($"| totalGPU={totalMs:F3}ms span={spanMs:F3}ms passes={_slotPasses[slot].Count}");
		Console.WriteLine(sb.ToString());
	}

	/// <summary>Blocking drain for one slot — only used as a safety net before reusing a slot still in flight.</summary>
	private void DrainSlot(int slot)
	{
		if (_pendingMap[slot])
		{
			// resolved but map not yet kicked (shouldn't happen in normal flow) — nothing mapped to wait on.
			_pendingMap[slot] = false;
			return;
		}
		while (_status[slot] == (BufferMapAsyncStatus)(-1))
		{
			_ctx.Native.DevicePoll(_ctx.Device, true, null);
		}
		if (_status[slot] == BufferMapAsyncStatus.Success)
		{
			LogSlot(slot);
			_wgpu.BufferUnmap((Buffer*)_staging[slot]);
		}
		_inFlight[slot] = false;
	}

	public void Dispose()
	{
		for (int i = 0; i < Depth; i++)
		{
			if (_staging[i] != 0) { _wgpu.BufferRelease((Buffer*)_staging[i]); _staging[i] = 0; }
		}
		if (_resolve is not null) { _wgpu.BufferRelease(_resolve); }
		if (_querySet is not null) { _wgpu.QuerySetRelease(_querySet); }
	}
}
