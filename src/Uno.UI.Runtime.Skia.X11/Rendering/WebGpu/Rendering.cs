using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Common;

/// <summary>The draw commands a caller records inside a render pass.</summary>
public delegate void DrawCallback(GpuRenderPassEncoder pass);

/// <summary>
/// One vertex buffer's layout, as a managed value so it can be written inline:
/// new VLayout(24, [ new(Float32x3, 0, 0), new(Float32x3, 12, 1) ]).
/// </summary>
public sealed record VLayout(ulong Stride, VertexAttribute[] Attributes, VertexStepMode StepMode = VertexStepMode.Vertex);

/// <summary>
/// Render-side helpers built so that resource creation NESTS as a dependency tree:
/// sub-arrays are taken as managed arrays and pinned internally for the call, so each
/// creation is an expression you can compose inline rather than a linear sequence of
/// pin-this, create-that statements. No semantic defaults are invented (only overridable
/// optional params); the only internal work is mechanical marshaling/pinning.
///
/// Bind-group layouts are a SHARED dependency (pipeline + bind groups both need them),
/// so they're described inline in the pipeline and recovered via pipeline.GetBindGroupLayout(i).
/// </summary>
public static unsafe class Rendering
{
    public static double ResCreateMs; // per-frame time in native CreateBuffer/CreateBindGroup (perf diag)
    /// <summary>Compile a WGSL source string into a shader module (the chained-struct dance).</summary>
    public static Owned<ShaderModule> CreateShaderModuleWgsl(this WebGpuContext ctx, string label, string code)
    {
        nint codePtr = Marshal.StringToCoTaskMemUTF8(code);
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct(null, SType.ShaderModuleWgslDescriptor),
                Code = (byte*)codePtr,
            };
            var desc = new ShaderModuleDescriptor { Label = (byte*)labelPtr, NextInChain = (ChainedStruct*)&wgsl };
            ShaderModule* module = ctx.Wgpu.DeviceCreateShaderModule(ctx.Device, ref desc);
            if (module is null)
            {
                throw new InvalidOperationException($"Failed to create shader module '{label}'.");
            }
            return new Owned<ShaderModule>(module, p => ctx.Wgpu.ShaderModuleRelease((ShaderModule*)p));
        }
        finally
        {
            Marshal.FreeCoTaskMem(codePtr);
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    /// <summary>
    /// A render pipeline assembled from an inline dependency tree. `vertexLayouts` and
    /// `bindGroupLayouts` (one entry-array per @group) are created/pinned internally; the
    /// derived bind-group layouts and pipeline layout are owned by the returned pipeline
    /// (recover layouts with pipeline.GetBindGroupLayout(i) to build bind groups).
    /// Overridable defaults: single-sample, no blend, CCW front face, no depth.
    /// </summary>
    public static GpuRenderPipeline CreateRenderPipeline(
        this WebGpuContext ctx,
        string label,
        ShaderModule* shaderModule,
        VLayout[] vertexLayouts,
        BindGroupLayoutEntry[][] bindGroupLayouts,
        TextureFormat targetFormat = TextureFormat.Rgba8Unorm,
        string vertexEntry = "vs_main",
        string fragmentEntry = "fs_main",
        PrimitiveTopology topology = PrimitiveTopology.TriangleList,
        CullMode cullMode = CullMode.None,
        DepthStencilState? depthStencil = null,
        BlendState? blend = null,
        ColorWriteMask writeMask = ColorWriteMask.All,
        uint sampleCount = 1)
    {
        var pins = new List<GCHandle>();
        var createdBgls = new List<nint>();
        PipelineLayout* pipelineLayout = null;
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        nint vsPtr = Marshal.StringToCoTaskMemUTF8(vertexEntry);
        nint fsPtr = Marshal.StringToCoTaskMemUTF8(fragmentEntry);
        try
        {
            // Vertex buffer layouts: pin each attribute array, point a VertexBufferLayout at it.
            var vbls = new VertexBufferLayout[vertexLayouts.Length];
            for (int i = 0; i < vertexLayouts.Length; i++)
            {
                var h = GCHandle.Alloc(vertexLayouts[i].Attributes, GCHandleType.Pinned);
                pins.Add(h);
                vbls[i] = new VertexBufferLayout
                {
                    ArrayStride = vertexLayouts[i].Stride,
                    StepMode = vertexLayouts[i].StepMode,
                    AttributeCount = (nuint)vertexLayouts[i].Attributes.Length,
                    Attributes = (VertexAttribute*)h.AddrOfPinnedObject(),
                };
            }
            var vblsHandle = GCHandle.Alloc(vbls, GCHandleType.Pinned);
            pins.Add(vblsHandle);

            // Bind-group layouts: create each as a real GPU object from its inline entries.
            var bglPtrs = new nint[bindGroupLayouts.Length];
            for (int j = 0; j < bindGroupLayouts.Length; j++)
            {
                var eh = GCHandle.Alloc(bindGroupLayouts[j], GCHandleType.Pinned);
                pins.Add(eh);
                var bglDesc = new BindGroupLayoutDescriptor
                {
                    EntryCount = (nuint)bindGroupLayouts[j].Length,
                    Entries = (BindGroupLayoutEntry*)eh.AddrOfPinnedObject(),
                };
                BindGroupLayout* bgl = ctx.Wgpu.DeviceCreateBindGroupLayout(ctx.Device, ref bglDesc);
                if (bgl is null)
                {
                    throw new InvalidOperationException($"Failed to create bind group layout {j} for pipeline '{label}'.");
                }
                bglPtrs[j] = (nint)bgl;
                createdBgls.Add((nint)bgl);
            }

            if (bindGroupLayouts.Length > 0)
            {
                var plHandle = GCHandle.Alloc(bglPtrs, GCHandleType.Pinned);
                pins.Add(plHandle);
                var plDesc = new PipelineLayoutDescriptor
                {
                    BindGroupLayoutCount = (nuint)bglPtrs.Length,
                    BindGroupLayouts = (BindGroupLayout**)plHandle.AddrOfPinnedObject(),
                };
                pipelineLayout = ctx.Wgpu.DeviceCreatePipelineLayout(ctx.Device, ref plDesc);
            }

            BlendState bs = blend ?? default;
            var colorTarget = new ColorTargetState { Format = targetFormat, Blend = blend.HasValue ? &bs : null, WriteMask = writeMask };
            var fragment = new FragmentState { Module = shaderModule, EntryPoint = (byte*)fsPtr, TargetCount = 1, Targets = &colorTarget };
            DepthStencilState ds = depthStencil ?? default;   // addressable local; used only when HasValue
            var desc = new RenderPipelineDescriptor
            {
                Label = (byte*)labelPtr,
                Layout = pipelineLayout,
                Vertex = new VertexState
                {
                    Module = shaderModule,
                    EntryPoint = (byte*)vsPtr,
                    BufferCount = (nuint)vbls.Length,
                    Buffers = (VertexBufferLayout*)vblsHandle.AddrOfPinnedObject(),
                },
                Primitive = new PrimitiveState { Topology = topology, FrontFace = FrontFace.Ccw, CullMode = cullMode, StripIndexFormat = IndexFormat.Undefined },
                Multisample = new MultisampleState { Count = sampleCount, Mask = ~0u, AlphaToCoverageEnabled = false },
                DepthStencil = depthStencil.HasValue ? &ds : null,
                Fragment = &fragment,
            };
            RenderPipeline* pipeline = ctx.Wgpu.DeviceCreateRenderPipeline(ctx.Device, ref desc);
            if (pipeline is null)
            {
                throw new InvalidOperationException($"Failed to create render pipeline '{label}'.");
            }
            return new GpuRenderPipeline(ctx.Wgpu, pipeline);
        }
        finally
        {
            // The pipeline ref-holds its layout(s); drop our references (recover via GetBindGroupLayout).
            foreach (var bgl in createdBgls) ctx.Wgpu.BindGroupLayoutRelease((BindGroupLayout*)bgl);
            if (pipelineLayout is not null) ctx.Wgpu.PipelineLayoutRelease(pipelineLayout);
            foreach (var h in pins) h.Free();
            Marshal.FreeCoTaskMem(labelPtr);
            Marshal.FreeCoTaskMem(vsPtr);
            Marshal.FreeCoTaskMem(fsPtr);
        }
    }

    /// <summary>Bind group from a layout (typically pipeline.GetBindGroupLayout(i)) + inline entries.</summary>
    public static GpuBindGroup CreateBindGroup(this WebGpuContext ctx, BindGroupLayout* layout, params BindGroupEntry[] entries)
    {
        var h = GCHandle.Alloc(entries, GCHandleType.Pinned);
        try
        {
            var desc = new BindGroupDescriptor
            {
                Layout = layout,
                EntryCount = (nuint)entries.Length,
                Entries = (BindGroupEntry*)h.AddrOfPinnedObject(),
            };
            long _t = System.Diagnostics.Stopwatch.GetTimestamp();
            BindGroup* bg = ctx.Wgpu.DeviceCreateBindGroup(ctx.Device, ref desc);
            ResCreateMs += (System.Diagnostics.Stopwatch.GetTimestamp() - _t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (bg is null)
            {
                throw new InvalidOperationException("Failed to create bind group.");
            }
            return new GpuBindGroup(ctx.Wgpu, bg);
        }
        finally
        {
            h.Free();
        }
    }

    /// <summary>
    /// Make a render-attachment texture (optionally with a depth buffer), clear it, let the
    /// caller record draws, then read it back as tightly packed RGBA bytes (→ PngWriter).
    /// Pass depthFormat to attach a cleared depth buffer (DepthClearValue 1.0).
    /// </summary>
    public static byte[] RenderToRgba(this WebGpuContext ctx, uint width, uint height, Color clearColor, DrawCallback draw,
        TextureFormat? depthFormat = null, uint sampleCount = 1, WebGpuTargets? cache = null, bool readback = true)
    {
        bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
        long t0 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var wgpu = ctx.Wgpu;

        // The single-sample texture we read back. With MSAA it's the resolve target; the pass renders into a
        // separate multisampled color texture that resolves into it (StoreOp.Store + ResolveTarget). The
        // textures are EXPENSIVE to allocate (≈26 MB total on a 1 MP frame) so a persistent `cache`, keyed by
        // size/sampleCount/depth, reuses them across frames — created per-frame they dominated GPU frame time.
        WebGpuTargets targets = cache ?? new WebGpuTargets();
        targets.Ensure(ctx, width, height, sampleCount, depthFormat);
        var texture = targets.Color;
        var view = targets.ColorView;
        var msaaView = targets.MsaaView;

        long t1 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        using var encoder = ctx.CreateEncoder("render-encoder");

        var colorAttachment = new RenderPassColorAttachment
        {
            View = sampleCount > 1 ? msaaView : view,
            ResolveTarget = sampleCount > 1 ? view : null,
            LoadOp = LoadOp.Clear,
            // With MSAA we only need the RESOLVED single-sample output (`view`), never the multisampled buffer
            // itself. StoreOp.Discard lets tiled/integrated GPUs resolve on-tile and skip writing all N samples
            // out to memory — the difference between MSAA being ~free and costing N× the framebuffer bandwidth
            // every frame (the latter tanked an Intel UHD 620 from <3ms to 50ms+). At 1× we render straight into
            // `view`, so it must be stored.
            StoreOp = sampleCount > 1 ? StoreOp.Discard : StoreOp.Store,
            ClearValue = clearColor,
        };

        var depthView = targets.DepthView;
        var depthAttachment = new RenderPassDepthStencilAttachment();
        RenderPassDepthStencilAttachment* depthPtr = null;
        if (depthFormat.HasValue)
        {
            bool hasStencil = depthFormat.Value is TextureFormat.Depth24PlusStencil8 or TextureFormat.Stencil8;
            depthAttachment = new RenderPassDepthStencilAttachment
            {
                View = depthView,
                DepthLoadOp = depthFormat.Value is TextureFormat.Stencil8 ? LoadOp.Undefined : LoadOp.Clear,
                // Depth is the rounded-clip mask: 0 = allowed. Cleared to 0 so un-clipped content passes
                // the GreaterEqual depth test (content z = 0); clip-write marks excluded corners as 1.
                DepthClearValue = 0.0f,
                DepthStoreOp = depthFormat.Value is TextureFormat.Stencil8 ? StoreOp.Undefined : StoreOp.Store,
                StencilLoadOp = hasStencil ? LoadOp.Clear : LoadOp.Undefined,
                StencilClearValue = 0,
                StencilStoreOp = hasStencil ? StoreOp.Store : StoreOp.Undefined,
            };
            depthPtr = &depthAttachment;
        }

        // GPU-side per-pass timing (UNO_WEBGPU_GPUTIME=1): stamp begin/end of the fast-path scene pass. This is the
        // whole offscreen render (all draws + clear + MSAA resolve at pass-end) as a single trustworthy GPU number —
        // the segmented path stamps its sub-passes separately. A/B recipes: MSAA=1 vs 4 isolates the resolve cost;
        // UNO_WEBGPU_NOTEXT on/off isolates the text stencil-then-cover cost (both compare this "scene" number).
        uint sceneTb = 0, sceneTe = 0;
        bool sceneTimed = targets.Timer is { } gtm && gtm.TryPass("scene", out sceneTb, out sceneTe);
        RenderPassTimestampWrites sceneTw = sceneTimed ? targets.Timer!.Writes(sceneTb, sceneTe) : default;
        var passDesc = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &colorAttachment,
            DepthStencilAttachment = depthPtr,
            TimestampWrites = sceneTimed ? &sceneTw : null,
        };
        RenderPassEncoder* pass = wgpu.CommandEncoderBeginRenderPass(encoder, ref passDesc);
        draw(new GpuRenderPassEncoder(wgpu, pass));
        wgpu.RenderPassEncoderEnd(pass);
        wgpu.RenderPassEncoderRelease(pass);
        long t2 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        targets.Timer?.Resolve(encoder); // encode query-set → staging copy before finishing the encoder
        ctx.FinishAndSubmit(encoder);
        targets.Timer?.ReadAndLog(); // maps + logs the per-pass GPU deltas (blocks on GPU; diagnostic-only)
        long t3 = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // Swapchain present skips the readback: the frame stays in `texture` (cache.Color) for a GPU blit.
        var result = readback ? TextureReadback.ReadRgba8(ctx, texture, width, height) : Array.Empty<byte>();

        if (perf)
        {
            double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"[PERF] rtr texcreate={Ms(t0, t1):F2} encode={Ms(t1, t2):F2} submit={Ms(t2, t3):F2} ms");
        }

        if (cache is null) { targets.Dispose(); } // transient (uncached) caller owns nothing persistent
        return result;
    }
}

/// <summary>
/// Persistent render-target set for <see cref="Rendering.RenderToRgba"/>: the single-sample readback color
/// texture, an optional multisample color texture (resolves into it), and an optional depth/stencil texture.
/// Reused across frames; <see cref="Ensure"/> reallocates only when size/sampleCount/depthFormat change.
/// </summary>
public sealed unsafe class WebGpuTargets : IDisposable
{
    private uint _w, _h, _samples;
    private TextureFormat? _depthFormat;

    public Owned<Texture> Color { get; private set; }
    public Owned<TextureView> ColorView { get; private set; }
    public Owned<Texture> Msaa { get; private set; }
    public Owned<TextureView> MsaaView { get; private set; }
    public Owned<Texture> Depth { get; private set; }
    public Owned<TextureView> DepthView { get; private set; }

    // --- Persistent pipeline cache. Pipelines/sampler don't change frame-to-frame, but were recreated every
    // frame (~7 ms on a real GPU once the readback was removed — the dominant remaining cost). Created once by
    // WebGpuDrawList.Render, keyed by sample count; modules are disposed right after creation (the pipeline
    // keeps the compiled shader), so only the pipelines + sampler are retained. ---
    private readonly Dictionary<string, GpuRenderPipeline> _pipes = new();
    private readonly List<IDisposable> _pipeDisposables = new();
    public GpuSampler Sampler { get; set; }
    private uint _pipeMsaa;
    private bool _hasPipelines;

    // Whole-frame skip: when a frame's content hash matches the last one rendered, the persistent target already
    // holds the correct pixels, so the entire encode (all blur/scene passes) is skipped and the target is just
    // re-presented. Cleared whenever the targets are torn down (resize / sample-count change) so we never present
    // a stale or wrong-sized texture.
    public ulong LastContentHash { get; set; } // full (cheap^heavy) signature of the last rendered frame
    public ulong LastCheapHash { get; set; }   // command-only signature (cheap early-out before the heavy hash)
    public bool HasContent { get; set; }
    public byte[]? LastBytes { get; set; } // last readback result, returned on a skip so readback callers still get pixels

    // Opt-in GPU-side per-pass timing (UNO_WEBGPU_GPUTIME=1). Persistent (query set + buffers created once); only
    // when the device advertises timestamp-query support.
    public GpuTimer? Timer { get; private set; }
    public void EnsureTimer(WebGpuContext ctx)
    {
        if (Timer is null && ctx.TimestampEnabled) { Timer = new GpuTimer(ctx); }
    }

    // Reusable per-frame scratch (option [2]): the draw loop fills these IDisposable lists with effect uniforms /
    // image bind groups, then disposes+clears at frame end. Reusing the List objects (and their backing arrays)
    // avoids re-allocating them every frame. (The bigger GC source — the whole draw list — is a separate change.)
    public readonly List<IDisposable> ScratchA = new();
    public readonly List<IDisposable> ScratchB = new();

    public bool PipelinesValid(uint msaa) => _hasPipelines && _pipeMsaa == msaa;
    public bool HasPipe(string key) => _pipes.ContainsKey(key);
    public GpuRenderPipeline Pipe(string key) => _pipes[key];
    public void AddPipe(string key, GpuRenderPipeline p) { _pipes[key] = p; _pipeDisposables.Add(p); }
    public void MarkPipelines(uint msaa) { _pipeMsaa = msaa; _hasPipelines = true; }
    public void DisposePipelines()
    {
        foreach (var d in _pipeDisposables) { d.Dispose(); }
        _pipeDisposables.Clear(); _pipes.Clear();
        if (Sampler.Ptr is not null) { Sampler.Dispose(); Sampler = default; }
        _hasPipelines = false;
        // Cached bind groups reference the sampler/layouts that were just torn down — drop them too.
        ClearBindGroupCache();
    }

    // --- Content-keyed bind-group cache. The static chrome rebuilds byte-identical gradients & clips every frame;
    // creating their LUT texture + uniform buffer + bind group cost ~1.5 ms of CPU "setup" per frame. Here each is
    // created once and reused, keyed by its content (uniform bytes + optional LUT). Entries unused for a while are
    // evicted so transient content (hover states, scroll) doesn't leak. ---
    public sealed class BgEntry
    {
        public byte[] USig = System.Array.Empty<byte>();
        public byte[]? Lut;
        public GpuBindGroup Bg;
        public readonly List<IDisposable> Own = new();
        public int LastUsed;
    }
    private readonly Dictionary<int, List<BgEntry>> _bgCache = new();
    private int _frameNo;

    public void BeginCacheFrame() => _frameNo++;

    private static int SigHash(ReadOnlySpan<byte> sig)
    {
        uint h = 2166136261u; // FNV-1a/32
        foreach (var b in sig) { h ^= b; h *= 16777619u; }
        return (int)h;
    }

    public BgEntry? FindBindGroup(ReadOnlySpan<byte> usig, ReadOnlySpan<byte> lut)
    {
        if (_bgCache.TryGetValue(SigHash(usig), out var bucket))
        {
            foreach (var e in bucket)
            {
                bool lutOk = lut.IsEmpty ? e.Lut is null : (e.Lut is not null && e.Lut.AsSpan().SequenceEqual(lut));
                if (lutOk && e.USig.AsSpan().SequenceEqual(usig)) { e.LastUsed = _frameNo; return e; }
            }
        }
        return null;
    }

    public BgEntry AddBindGroup(ReadOnlySpan<byte> usig, byte[]? lut, GpuBindGroup bg, params IDisposable[] own)
    {
        var e = new BgEntry { USig = usig.ToArray(), Lut = lut, Bg = bg, LastUsed = _frameNo };
        e.Own.AddRange(own);
        int h = SigHash(usig);
        if (!_bgCache.TryGetValue(h, out var bucket)) { bucket = new List<BgEntry>(); _bgCache[h] = bucket; }
        bucket.Add(e);
        return e;
    }

    public void EvictStaleBindGroups(int maxAgeFrames = 240)
    {
        foreach (var bucket in _bgCache.Values)
        {
            for (int i = bucket.Count - 1; i >= 0; i--)
            {
                if (_frameNo - bucket[i].LastUsed > maxAgeFrames)
                {
                    foreach (var d in bucket[i].Own) { d.Dispose(); }
                    bucket.RemoveAt(i);
                }
            }
        }
    }

    private void ClearBindGroupCache()
    {
        foreach (var bucket in _bgCache.Values)
        {
            foreach (var e in bucket) { foreach (var d in e.Own) { d.Dispose(); } }
        }
        _bgCache.Clear();
        // The xform bind group holds a ref to pstencil's group-0 layout; when pipelines are torn down (the caller,
        // DisposePipelines) that layout dies, so this cached bind group must be dropped too or it dangles.
        if (_xformBg.Ptr is not null) { _xformBg.Dispose(); _xformBg = default; _xformBgBuf = default; }
    }

    // --- Image-texture cache. An image brush's GPU texture depends only on its source pixels, so upload it ONCE
    // and reuse it across frames (the bind group — which also carries the per-frame opacity/color-matrix — is
    // still rebuilt cheaply each frame). Without this, a frame with N images created AND destroyed N textures
    // EVERY frame; under many images that exhausts VRAM (CreateTexture "out of memory", then cascading
    // "texture invalid or destroyed"). Keyed by source-image identity; a size change replaces the entry, and
    // entries unused for a while are evicted so transient images don't leak. ---
    public sealed class ImageTex { public GpuTexture Tex; public GpuTextureView View; public int W, H; public int LastUsed; }
    private readonly Dictionary<long, ImageTex> _imageTex = new();

    public bool TryGetImageTex(long key, int w, int h, out GpuTextureView view)
    {
        if (key != 0 && _imageTex.TryGetValue(key, out var e) && e.W == w && e.H == h)
        {
            e.LastUsed = _frameNo; view = e.View; return true;
        }
        view = default; return false;
    }

    public void AddImageTex(long key, int w, int h, GpuTexture tex, GpuTextureView view)
    {
        if (_imageTex.TryGetValue(key, out var old)) // stale entry (e.g. the image's size changed) — replace it
        {
            if (old.View.Ptr is not null) { old.View.Dispose(); }
            if (old.Tex.Ptr is not null) { old.Tex.Dispose(); }
        }
        _imageTex[key] = new ImageTex { Tex = tex, View = view, W = w, H = h, LastUsed = _frameNo };
    }

    public void EvictStaleImageTex(int maxAgeFrames = 240)
    {
        if (_imageTex.Count == 0) { return; }
        List<long>? dead = null;
        foreach (var kv in _imageTex) { if (_frameNo - kv.Value.LastUsed > maxAgeFrames) { (dead ??= new()).Add(kv.Key); } }
        if (dead is null) { return; }
        foreach (var k in dead)
        {
            var e = _imageTex[k]; _imageTex.Remove(k);
            if (e.View.Ptr is not null) { e.View.Dispose(); } if (e.Tex.Ptr is not null) { e.Tex.Dispose(); }
        }
    }

    private void ClearImageTexCache()
    {
        foreach (var e in _imageTex.Values) { if (e.View.Ptr is not null) { e.View.Dispose(); } if (e.Tex.Ptr is not null) { e.Tex.Dispose(); } }
        _imageTex.Clear();
    }

    public void Ensure(WebGpuContext ctx, uint width, uint height, uint sampleCount, TextureFormat? depthFormat)
    {
        if (width == _w && height == _h && sampleCount == _samples && depthFormat == _depthFormat && Color.Ptr is not null)
        {
            return;
        }
        DisposeTextures();
        _w = width; _h = height; _samples = sampleCount; _depthFormat = depthFormat;

        // TextureBinding so the segmented path can SAMPLE the resolved target (backdrop blur reads it).
        Color = ctx.CreateTexture2D("render-target", width, height, TextureUsage.RenderAttachment | TextureUsage.CopySrc | TextureUsage.TextureBinding);
        ColorView = ctx.CreateView(Color);
        if (sampleCount > 1)
        {
            Msaa = ctx.CreateTexture2D("render-target-msaa", width, height, TextureUsage.RenderAttachment, TextureFormat.Rgba8Unorm, 1, sampleCount);
            MsaaView = ctx.CreateView(Msaa);
        }
        if (depthFormat.HasValue)
        {
            Depth = ctx.CreateTexture2D("depth", width, height, TextureUsage.RenderAttachment, depthFormat.Value, 1, sampleCount);
            DepthView = ctx.CreateView(Depth);
        }
    }

    private void DisposeTextures()
    {
        // default(Owned<T>) has a null release action — only dispose ones actually created.
        if (DepthView.Ptr is not null) { DepthView.Dispose(); } if (Depth.Ptr is not null) { Depth.Dispose(); }
        if (MsaaView.Ptr is not null) { MsaaView.Dispose(); } if (Msaa.Ptr is not null) { Msaa.Dispose(); }
        if (ColorView.Ptr is not null) { ColorView.Dispose(); } if (Color.Ptr is not null) { Color.Dispose(); }
        Depth = default; DepthView = default; Msaa = default; MsaaView = default; Color = default; ColorView = default;
        HasContent = false; LastBytes = null; // target pixels are gone — the next frame must render
    }

    // --- Transient texture pool (segmented path: blur-pyramid levels, shadow coverage, layer offscreens) ---
    // These were created fresh every frame; on a real GPU that per-frame allocation dominated the segmented
    // loop. Rent reuses a free texture matching the key; BeginFrame marks all free for the next frame. Every
    // renter clears (LoadOp.Clear) before writing, so reuse is safe.
    private sealed class PoolEntry
    {
        public Owned<Texture> Tex; public Owned<TextureView> View;
        public uint W, H, Samples; public TextureFormat Fmt; public TextureUsage Usage; public bool InUse;
    }
    private readonly List<PoolEntry> _pool = new();

    public void BeginFrame()
    {
        foreach (var e in _pool) { e.InUse = false; }
    }

    public Owned<TextureView> Rent(WebGpuContext ctx, uint w, uint h, TextureUsage usage, uint samples, TextureFormat fmt)
    {
        foreach (var e in _pool)
        {
            if (!e.InUse && e.W == w && e.H == h && e.Samples == samples && e.Fmt == fmt && e.Usage == usage)
            {
                e.InUse = true;
                return e.View;
            }
        }
        var tex = ctx.CreateTexture2D("pool", w, h, usage, fmt, 1, samples);
        var view = ctx.CreateView(tex);
        _pool.Add(new PoolEntry { Tex = tex, View = view, W = w, H = h, Samples = samples, Fmt = fmt, Usage = usage, InUse = true });
        return view;
    }

    // --- Persistent vertex-buffer pool. The scene vertex buffers (solid/grad/rr/path/cover/image) were created
    // fresh every frame via mapped-at-creation; with the readback gone that per-frame allocation+map was a chunk
    // of the CPU-side "setup" cost. Here one CopyDst buffer per key is grown on demand and rewritten each frame
    // with QueueWriteBuffer (a cheap queued copy), so no buffer is allocated on a steady-state frame. ---
    private readonly Dictionary<string, Owned<Buffer>> _vb = new();
    private readonly Dictionary<string, ulong> _vbCap = new();
    // CPU shadow of the last bytes uploaded per buffer, so we can skip the (expensive) QueueWriteBuffer when the
    // frame's vertex data is byte-identical to what's already on the GPU (idle/static frames, and the buffers that
    // didn't change during a partial scroll). _vbLen tracks the valid prefix length (the float[] may be oversized).
    private readonly Dictionary<string, float[]> _vbShadow = new();
    private readonly Dictionary<string, int> _vbLen = new();

    private static readonly bool _vbStats = System.Environment.GetEnvironmentVariable("UNO_WEBGPU_VBSTATS") == "1";
    private static readonly System.Collections.Generic.Dictionary<string, int> _vbStatN = new();
    private static void VbStat(string key, string kind, int len, int total)
    {
        // Throttle per key so the animating home screen doesn't flood; print every 120th call.
        _vbStatN.TryGetValue(key, out var n);
        if (n < 12 || n % 120 == 0) { System.Console.WriteLine($"[VB] {key} #{n}: {kind} {len}/{total} floats ({(total == 0 ? 0 : 100.0 * len / total):F1}%)"); }
        _vbStatN[key] = n + 1;
    }

    public Buffer* VertexBuffer(WebGpuContext ctx, string key, ReadOnlySpan<float> data)
    {
        if (data.Length == 0) { return null; }
        ulong needed = (ulong)data.Length * sizeof(float);
        bool fresh = false;
        if (!_vb.TryGetValue(key, out var buf) || buf.Ptr is null || _vbCap[key] < needed)
        {
            if (buf.Ptr is not null) { buf.Dispose(); }
            ulong cap = (needed + (needed >> 1) + 3UL) & ~3UL; // grow 1.5×, 4-byte aligned (COPY_BUFFER_ALIGNMENT)
            buf = ctx.CreateBuffer(key, cap, BufferUsage.Vertex | BufferUsage.CopyDst);
            _vb[key] = buf; _vbCap[key] = cap; fresh = true; // new GPU buffer → must upload all
        }
        _vbShadow.TryGetValue(key, out var shadow);

        // PARTIAL upload: when the buffer layout is unchanged (same total length), only re-send the byte range that
        // actually differs from what's already on the GPU. A moved/animated/recoloured visual whose vertex COUNT is
        // stable doesn't shift anything after it, so just its own slice differs — we upload [lo..hi] at its offset
        // instead of the whole ~MB buffer. CommonPrefixLength is SIMD; the back-scan stops at the first unchanged
        // tail float. Falls back to a full upload only when the buffer is fresh/grown or its length changed (content
        // added/removed shifts every subsequent offset). The shadow is the last-uploaded contents, kept in sync.
        if (!fresh && shadow is not null && _vbLen.TryGetValue(key, out var prevLen) && prevLen == data.Length)
        {
            var prev = shadow.AsSpan(0, prevLen);
            int lo = data.CommonPrefixLength(prev);
            if (lo == data.Length) { if (_vbStats) { VbStat(key, "SKIP", 0, data.Length); } return buf.Ptr; } // identical → nothing to upload
            int hi = data.Length - 1;
            while (hi > lo && data[hi] == prev[hi]) { hi--; }
            int len = hi - lo + 1;
            ctx.WriteBuffer<float>(buf.Ptr, data.Slice(lo, len), (ulong)lo * sizeof(float));
            data.Slice(lo, len).CopyTo(shadow.AsSpan(lo));
            if (_vbStats) { VbStat(key, "PARTIAL", len, data.Length); }
            return buf.Ptr;
        }

        if (_vbStats) { VbStat(key, fresh ? "FULL(fresh/grow)" : "FULL(len-changed)", data.Length, data.Length); }
        ctx.WriteBuffer<float>(buf.Ptr, data);
        if (shadow is null || shadow.Length < data.Length)
        {
            shadow = new float[data.Length];
            _vbShadow[key] = shadow;
        }
        data.CopyTo(shadow);
        _vbLen[key] = data.Length;
        return buf.Ptr;
    }

    // Like VertexBuffer, but the caller has already recorded exactly which float ranges changed (the slab's dirty
    // ranges), so we upload only those — no CommonPrefixLength scan. Falls back to a full upload when the buffer was
    // (re)allocated or its length changed (a structural change, where the dirty ranges don't describe the whole diff).
    public Buffer* VertexBufferDirty(WebGpuContext ctx, string key, ReadOnlySpan<float> data, System.Collections.Generic.List<(int off, int len)> ranges)
    {
        if (data.Length == 0) { return null; }
        ulong needed = (ulong)data.Length * sizeof(float);
        bool fresh = false;
        if (!_vb.TryGetValue(key, out var buf) || buf.Ptr is null || _vbCap[key] < needed)
        {
            if (buf.Ptr is not null) { buf.Dispose(); }
            ulong cap = (needed + (needed >> 1) + 3UL) & ~3UL;
            buf = ctx.CreateBuffer(key, cap, BufferUsage.Vertex | BufferUsage.CopyDst);
            _vb[key] = buf; _vbCap[key] = cap; fresh = true;
        }
        if (!fresh && _vbLen.TryGetValue(key, out var prevLen) && prevLen == data.Length)
        {
            // Nothing dirtied this buffer this frame (e.g. after routing borders to analytic quads, the path slab
            // may be unchanged) — the persistent buffer already holds the correct data, so skip the upload.
            if (ranges.Count == 0) { return buf.Ptr; }
            // Coalesce nearby dirty ranges into fewer, larger WriteBuffers. Each QueueWriteBuffer can stall on
            // wgpu's staging belt (a full segment waits for the GPU, which is a frame behind on an iGPU), so many
            // small writes cost far more than a few bigger ones. Neighbouring slab slices are usually contiguous, so
            // merging across a small gap collapses most of them; the gap bytes we re-upload are cheap vs a stall.
            const int gap = 4096;    // floats — bridge this much clean space rather than start a new write
            const int maxWrites = 4; // each WriteBuffer can stall on the staging belt (~a vsync), so cap the count
            ranges.Sort(static (a, b) => a.off.CompareTo(b.off));
            int minOff = ranges[0].off, maxEnd = 0;
            int merged = 1, mend = ranges[0].off + ranges[0].len;
            for (int j = 1; j < ranges.Count; j++)
            {
                if (ranges[j].off + ranges[j].len > maxEnd) { maxEnd = ranges[j].off + ranges[j].len; }
                if (ranges[j].off > mend + gap) { merged++; mend = ranges[j].off + ranges[j].len; }
                else if (ranges[j].off + ranges[j].len > mend) { mend = ranges[j].off + ranges[j].len; }
            }
            if (maxEnd < ranges[0].off + ranges[0].len) { maxEnd = ranges[0].off + ranges[0].len; }
            int wrote = 0, calls = 0;
            if (merged > maxWrites)
            {
                // Too scattered — one bounding write beats many stalls (extra clean bytes are cheaper than the belt waits).
                ctx.WriteBuffer<float>(buf.Ptr, data.Slice(minOff, maxEnd - minOff), (ulong)minOff * sizeof(float));
                wrote = maxEnd - minOff; calls = 1;
            }
            else
            {
                int i = 0;
                while (i < ranges.Count)
                {
                    int off = ranges[i].off, end = off + ranges[i].len, j = i + 1;
                    while (j < ranges.Count && ranges[j].off <= end + gap) { end = System.Math.Max(end, ranges[j].off + ranges[j].len); j++; }
                    ctx.WriteBuffer<float>(buf.Ptr, data.Slice(off, end - off), (ulong)off * sizeof(float)); wrote += end - off; calls++;
                    i = j;
                }
            }
            if (_vbStats) { VbStat(key, $"DIRTY({calls}w)", wrote, data.Length); }
            return buf.Ptr;
        }
        ctx.WriteBuffer<float>(buf.Ptr, data);
        _vbLen[key] = data.Length;
        if (_vbStats) { VbStat(key, fresh ? "FULL(fresh/grow)" : "FULL(len-changed)", data.Length, data.Length); }
        return buf.Ptr;
    }

    // Read-only storage buffer (persistent, grown 1.5×, rewritten each frame). Used for the arena's per-visual
    // transform table — small, so a full WriteBuffer each frame is fine.
    public Buffer* StorageBuffer(WebGpuContext ctx, string key, ReadOnlySpan<float> data)
    {
        if (data.Length == 0) { return null; }
        ulong needed = (ulong)data.Length * sizeof(float);
        if (!_vb.TryGetValue(key, out var buf) || buf.Ptr is null || _vbCap[key] < needed)
        {
            if (buf.Ptr is not null) { buf.Dispose(); }
            ulong cap = (needed + (needed >> 1) + 3UL) & ~3UL;
            buf = ctx.CreateBuffer(key, cap, BufferUsage.Storage | BufferUsage.CopyDst);
            _vb[key] = buf; _vbCap[key] = cap;
        }
        ctx.WriteBuffer<float>(buf.Ptr, data);
        return buf.Ptr;
    }

    private GpuBindGroup _xformBg; private IntPtr _xformBgBuf;

    // Cached bind group for the arena transform table. The table CONTENTS change every frame (rewritten in
    // StorageBuffer), but the bind group only depends on the buffer identity + size, so it survives across frames
    // and is recreated only when the buffer reallocates (grows) — avoiding a DeviceCreateBindGroup + a native
    // GetBindGroupLayout call every frame. Binds the whole capacity (the shader only indexes valid entries).
    public GpuBindGroup XformBindGroup(WebGpuContext ctx, GpuRenderPipeline pipe, ReadOnlySpan<float> data)
    {
        Buffer* buf = StorageBuffer(ctx, "xform", data);
        if (_xformBgBuf == (IntPtr)buf && _xformBg.Ptr is not null) { return _xformBg; }
        if (_xformBg.Ptr is not null) { _xformBg.Dispose(); }
        _xformBg = ctx.CreateBindGroup(pipe.GetBindGroupLayout(0), new BindGroupEntry { Binding = 0, Buffer = buf, Size = _vbCap["xform"] });
        _xformBgBuf = (IntPtr)buf;
        return _xformBg;
    }

    public void Dispose()
    {
        DisposeTextures();
        DisposePipelines();
        foreach (var e in _pool) { if (e.View.Ptr is not null) { e.View.Dispose(); } if (e.Tex.Ptr is not null) { e.Tex.Dispose(); } }
        _pool.Clear();
        foreach (var b in _vb.Values) { if (b.Ptr is not null) { b.Dispose(); } }
        _vb.Clear(); _vbCap.Clear(); _vbShadow.Clear(); _vbLen.Clear();
        ClearBindGroupCache(); // also drops the cached xform bind group
        ClearImageTexCache();
        Timer?.Dispose(); Timer = null;
    }
}
