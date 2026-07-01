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
/// </summary>
public sealed unsafe class GpuTimer : IDisposable
{
	private readonly WebGpuContext _ctx;
	private readonly WebGPU _wgpu;
	private readonly QuerySet* _querySet;
	private readonly Buffer* _resolve;   // QueryResolve | CopySrc
	private readonly Buffer* _staging;   // CopyDst | MapRead
	private readonly uint _capacity;     // total timestamps (2 per pass)
	private int _next;                   // next free timestamp index this frame
	private readonly List<(string label, int begin, int end)> _passes = new();

	public GpuTimer(WebGpuContext ctx, uint capacity = 512)
	{
		_ctx = ctx;
		_wgpu = ctx.Wgpu;
		_capacity = capacity;
		var qd = new QuerySetDescriptor { Type = QueryType.Timestamp, Count = capacity };
		_querySet = _wgpu.DeviceCreateQuerySet(ctx.Device, ref qd);
		var rd = new BufferDescriptor { Size = capacity * sizeof(ulong), Usage = BufferUsage.QueryResolve | BufferUsage.CopySrc };
		_resolve = _wgpu.DeviceCreateBuffer(ctx.Device, ref rd);
		var sd = new BufferDescriptor { Size = capacity * sizeof(ulong), Usage = BufferUsage.CopyDst | BufferUsage.MapRead };
		_staging = _wgpu.DeviceCreateBuffer(ctx.Device, ref sd);
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

	/// <summary>Encode the resolve (query set → resolve buffer → mappable staging) onto the frame encoder before finish.</summary>
	public void Resolve(CommandEncoder* encoder)
	{
		if (_next == 0)
		{
			return;
		}
		_wgpu.CommandEncoderResolveQuerySet(encoder, _querySet, 0, (uint)_next, _resolve, 0);
		_wgpu.CommandEncoderCopyBufferToBuffer(encoder, _resolve, 0, _staging, 0, (ulong)_next * sizeof(ulong));
	}

	/// <summary>After submit: map the staging buffer, read the ns timestamps, log per-pass deltas. Best-effort.</summary>
	public void ReadAndLog()
	{
		if (_next == 0)
		{
			return;
		}
		try
		{
			ulong size = (ulong)_next * sizeof(ulong);
			var status = (BufferMapAsyncStatus)(-1);
			_wgpu.BufferMapAsync(_staging, MapMode.Read, 0, (nuint)size, new PfnBufferMapCallback((s, _) => status = s), null);
			while (status == (BufferMapAsyncStatus)(-1))
			{
				_ctx.Native.DevicePoll(_ctx.Device, true, null);
			}
			if (status != BufferMapAsyncStatus.Success)
			{
				return;
			}
			ulong* t = (ulong*)_wgpu.BufferGetConstMappedRange(_staging, 0, (nuint)size);
			if (t is null)
			{
				_wgpu.BufferUnmap(_staging);
				return;
			}
			// Aggregate by label (many passes share a label, e.g. "blur") so the line stays readable.
			var agg = new Dictionary<string, (double ms, int n)>();
			double totalMs = 0;
			foreach (var (label, b, e) in _passes)
			{
				double ms = t[e] >= t[b] ? (t[e] - t[b]) / 1_000_000.0 : 0; // timestamps are ns; period folded in by wgpu
				totalMs += ms;
				agg.TryGetValue(label, out var v); // v defaults to (0,0) when absent
				agg[label] = (v.ms + ms, v.n + 1);
			}
			_wgpu.BufferUnmap(_staging);
			var sb = new StringBuilder("[PERF][GPU] ");
			foreach (var kv in agg)
			{
				sb.Append($"{kv.Key}={kv.Value.ms:F3}ms×{kv.Value.n} ");
			}
			sb.Append($"| totalGPU={totalMs:F3}ms passes={_passes.Count}");
			Console.WriteLine(sb.ToString());
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[PERF][GPU] timestamp read failed: {ex.Message}");
		}
	}

	public void Dispose()
	{
		if (_staging is not null) { _wgpu.BufferRelease(_staging); }
		if (_resolve is not null) { _wgpu.BufferRelease(_resolve); }
		if (_querySet is not null) { _wgpu.QuerySetRelease(_querySet); }
	}
}
