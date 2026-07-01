using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Common;

// Thin, transparent OOP handles over the raw wgpu objects. Each method is a
// 1:1 forward to the corresponding wgpuXxxYyy call — NO defaults, NO descriptor
// building, NO null-checks, NO canned values. The ONLY transformations are:
//   (a) the receiver pointer moves from first argument to `this`, and
//   (b) returned object pointers are re-wrapped in their handle type.
// So `device.CreateBuffer(d)`  ≡  `wgpu.DeviceCreateBuffer(device, ref d)`.
// Descriptors are taken BY VALUE so you can construct them inline:
//   device.CreateBuffer(new() { Size = 400, Usage = ... });
// (a shallow copy; any pointers inside stay valid for the synchronous call.)
// Every wrapper implicitly converts to its raw T*, so they interoperate freely
// with raw wgpu.* calls and the Common helpers; inline any of these by inspection.
//
// Dispose() == the matching XxxRelease(). These are structs: don't dispose a copy
// twice. Use `using var` (no copy) for RAII, or call Release()/Dispose() once.

public readonly unsafe struct GpuDevice(WebGPU wgpu, Device* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly Device* Ptr = ptr;
    public static implicit operator Device*(GpuDevice x) => x.Ptr;

    public GpuBuffer CreateBuffer(BufferDescriptor d) => new(Wgpu, Wgpu.DeviceCreateBuffer(Ptr, ref d));
    public GpuTexture CreateTexture(TextureDescriptor d) => new(Wgpu, Wgpu.DeviceCreateTexture(Ptr, ref d));
    public GpuSampler CreateSampler(SamplerDescriptor d) => new(Wgpu, Wgpu.DeviceCreateSampler(Ptr, ref d));
    public GpuCommandEncoder CreateCommandEncoder(CommandEncoderDescriptor d) => new(Wgpu, Wgpu.DeviceCreateCommandEncoder(Ptr, ref d));
    // colorFormat/depthFormat/sampleCount MUST match the render pass the bundle will execute in.
    public GpuRenderBundleEncoder CreateRenderBundleEncoder(TextureFormat colorFormat, TextureFormat depthFormat, uint sampleCount)
    {
        var cf = colorFormat;
        var d = new RenderBundleEncoderDescriptor
        {
            ColorFormatCount = 1,
            ColorFormats = &cf,
            DepthStencilFormat = depthFormat,
            SampleCount = sampleCount,
        };
        return new GpuRenderBundleEncoder(Wgpu, Wgpu.DeviceCreateRenderBundleEncoder(Ptr, ref d));
    }
    public GpuShaderModule CreateShaderModule(ShaderModuleDescriptor d) => new(Wgpu, Wgpu.DeviceCreateShaderModule(Ptr, ref d));
    public GpuBindGroupLayout CreateBindGroupLayout(BindGroupLayoutDescriptor d) => new(Wgpu, Wgpu.DeviceCreateBindGroupLayout(Ptr, ref d));
    public GpuBindGroup CreateBindGroup(BindGroupDescriptor d) => new(Wgpu, Wgpu.DeviceCreateBindGroup(Ptr, ref d));
    public GpuPipelineLayout CreatePipelineLayout(PipelineLayoutDescriptor d) => new(Wgpu, Wgpu.DeviceCreatePipelineLayout(Ptr, ref d));
    public GpuRenderPipeline CreateRenderPipeline(RenderPipelineDescriptor d) => new(Wgpu, Wgpu.DeviceCreateRenderPipeline(Ptr, ref d));
    public GpuQueue GetQueue() => new(Wgpu, Wgpu.DeviceGetQueue(Ptr));
    public Bool32 GetLimits(ref SupportedLimits l) => Wgpu.DeviceGetLimits(Ptr, ref l);
    public void Dispose() => Wgpu.DeviceRelease(Ptr);
}

public readonly unsafe struct GpuQueue(WebGPU wgpu, Queue* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly Queue* Ptr = ptr;
    public static implicit operator Queue*(GpuQueue x) => x.Ptr;

    public void WriteBuffer(Buffer* buffer, ulong offset, void* data, nuint size) => Wgpu.QueueWriteBuffer(Ptr, buffer, offset, data, size);
    public void WriteTexture(ImageCopyTexture dst, void* data, nuint size, TextureDataLayout layout, Extent3D writeSize) => Wgpu.QueueWriteTexture(Ptr, ref dst, data, size, ref layout, ref writeSize);
    public void Submit(nuint count, CommandBuffer** commands) => Wgpu.QueueSubmit(Ptr, count, commands);
    public void Dispose() => Wgpu.QueueRelease(Ptr);
}

public readonly unsafe struct GpuBuffer(WebGPU wgpu, Buffer* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly Buffer* Ptr = ptr;
    public static implicit operator Buffer*(GpuBuffer x) => x.Ptr;

    public void MapAsync(MapMode mode, nuint offset, nuint size, PfnBufferMapCallback callback, void* userdata) => Wgpu.BufferMapAsync(Ptr, mode, offset, size, callback, userdata);
    public void* GetConstMappedRange(nuint offset, nuint size) => Wgpu.BufferGetConstMappedRange(Ptr, offset, size);
    public void* GetMappedRange(nuint offset, nuint size) => Wgpu.BufferGetMappedRange(Ptr, offset, size);
    public void Unmap() => Wgpu.BufferUnmap(Ptr);
    public ulong GetSize() => Wgpu.BufferGetSize(Ptr);
    public void Destroy() => Wgpu.BufferDestroy(Ptr);
    public void Dispose() => Wgpu.BufferRelease(Ptr);
}

public readonly unsafe struct GpuTexture(WebGPU wgpu, Texture* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly Texture* Ptr = ptr;
    public static implicit operator Texture*(GpuTexture x) => x.Ptr;

    public GpuTextureView CreateView(TextureViewDescriptor d) => new(Wgpu, Wgpu.TextureCreateView(Ptr, ref d));
    public GpuTextureView CreateView() => new(Wgpu, Wgpu.TextureCreateView(Ptr, null));
    public void Destroy() => Wgpu.TextureDestroy(Ptr);
    public void Dispose() => Wgpu.TextureRelease(Ptr);
}

public readonly unsafe struct GpuTextureView(WebGPU wgpu, TextureView* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly TextureView* Ptr = ptr;
    public static implicit operator TextureView*(GpuTextureView x) => x.Ptr;
    public void Dispose() => Wgpu.TextureViewRelease(Ptr);
}

public readonly unsafe struct GpuSampler(WebGPU wgpu, Sampler* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly Sampler* Ptr = ptr;
    public static implicit operator Sampler*(GpuSampler x) => x.Ptr;
    public void Dispose() => Wgpu.SamplerRelease(Ptr);
}

public readonly unsafe struct GpuCommandEncoder(WebGPU wgpu, CommandEncoder* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly CommandEncoder* Ptr = ptr;
    public static implicit operator CommandEncoder*(GpuCommandEncoder x) => x.Ptr;

    public GpuRenderPassEncoder BeginRenderPass(RenderPassDescriptor d) => new(Wgpu, Wgpu.CommandEncoderBeginRenderPass(Ptr, ref d));
    public void CopyBufferToBuffer(Buffer* src, ulong srcOffset, Buffer* dst, ulong dstOffset, ulong size) => Wgpu.CommandEncoderCopyBufferToBuffer(Ptr, src, srcOffset, dst, dstOffset, size);
    public void CopyTextureToBuffer(ImageCopyTexture src, ImageCopyBuffer dst, Extent3D size) => Wgpu.CommandEncoderCopyTextureToBuffer(Ptr, ref src, ref dst, ref size);
    public void CopyBufferToTexture(ImageCopyBuffer src, ImageCopyTexture dst, Extent3D size) => Wgpu.CommandEncoderCopyBufferToTexture(Ptr, ref src, ref dst, ref size);
    public void ClearBuffer(Buffer* buffer, ulong offset, ulong size) => Wgpu.CommandEncoderClearBuffer(Ptr, buffer, offset, size);
    public GpuCommandBuffer Finish(CommandBufferDescriptor d) => new(Wgpu, Wgpu.CommandEncoderFinish(Ptr, ref d));
    public void PushDebugGroup(byte* label) => Wgpu.CommandEncoderPushDebugGroup(Ptr, label);
    public void PopDebugGroup() => Wgpu.CommandEncoderPopDebugGroup(Ptr);
    public void Dispose() => Wgpu.CommandEncoderRelease(Ptr);
}

public readonly unsafe struct GpuCommandBuffer(WebGPU wgpu, CommandBuffer* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly CommandBuffer* Ptr = ptr;
    public static implicit operator CommandBuffer*(GpuCommandBuffer x) => x.Ptr;
    public void Dispose() => Wgpu.CommandBufferRelease(Ptr);
}

public readonly unsafe struct GpuRenderPassEncoder(WebGPU wgpu, RenderPassEncoder* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly RenderPassEncoder* Ptr = ptr;
    public static implicit operator RenderPassEncoder*(GpuRenderPassEncoder x) => x.Ptr;

    public void SetPipeline(RenderPipeline* pipeline) => Wgpu.RenderPassEncoderSetPipeline(Ptr, pipeline);
    public void SetBindGroup(uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => Wgpu.RenderPassEncoderSetBindGroup(Ptr, groupIndex, group, dynamicOffsetCount, dynamicOffsets);
    public void SetVertexBuffer(uint slot, Buffer* buffer, ulong offset, ulong size) => Wgpu.RenderPassEncoderSetVertexBuffer(Ptr, slot, buffer, offset, size);
    public void SetIndexBuffer(Buffer* buffer, IndexFormat format, ulong offset, ulong size) => Wgpu.RenderPassEncoderSetIndexBuffer(Ptr, buffer, format, offset, size);
    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) => Wgpu.RenderPassEncoderDraw(Ptr, vertexCount, instanceCount, firstVertex, firstInstance);
    public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance) => Wgpu.RenderPassEncoderDrawIndexed(Ptr, indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
    public void SetScissorRect(uint x, uint y, uint width, uint height) => Wgpu.RenderPassEncoderSetScissorRect(Ptr, x, y, width, height);
    public void SetStencilReference(uint reference) => Wgpu.RenderPassEncoderSetStencilReference(Ptr, reference);
    public void End() => Wgpu.RenderPassEncoderEnd(Ptr);
    public void ExecuteBundles(RenderBundle** bundles, nuint count) => Wgpu.RenderPassEncoderExecuteBundles(Ptr, count, bundles);
    public void Dispose() => Wgpu.RenderPassEncoderRelease(Ptr);
}

// Records a reusable sequence of draw commands (pipeline/bindgroup/vertexbuffer/draw) that can be replayed cheaply
// in a render pass via ExecuteBundles — avoids re-encoding the same draws every frame. Cannot set scissor/stencil-
// reference/viewport (those stay pass-level). The encoder's color/depth formats + sample count must match the pass.
public readonly unsafe struct GpuRenderBundleEncoder(WebGPU wgpu, RenderBundleEncoder* ptr)
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly RenderBundleEncoder* Ptr = ptr;
    public void SetPipeline(RenderPipeline* pipeline) => Wgpu.RenderBundleEncoderSetPipeline(Ptr, pipeline);
    public void SetBindGroup(uint groupIndex, BindGroup* group, nuint dynamicOffsetCount, uint* dynamicOffsets) => Wgpu.RenderBundleEncoderSetBindGroup(Ptr, groupIndex, group, dynamicOffsetCount, dynamicOffsets);
    public void SetVertexBuffer(uint slot, Buffer* buffer, ulong offset, ulong size) => Wgpu.RenderBundleEncoderSetVertexBuffer(Ptr, slot, buffer, offset, size);
    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance) => Wgpu.RenderBundleEncoderDraw(Ptr, vertexCount, instanceCount, firstVertex, firstInstance);
    public GpuRenderBundle Finish()
    {
        var desc = new RenderBundleDescriptor();
        return new GpuRenderBundle(Wgpu, Wgpu.RenderBundleEncoderFinish(Ptr, ref desc));
    }
}

public readonly unsafe struct GpuRenderBundle(WebGPU wgpu, RenderBundle* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly RenderBundle* Ptr = ptr;
    public void Dispose() => Wgpu.RenderBundleRelease(Ptr);
}

public readonly unsafe struct GpuShaderModule(WebGPU wgpu, ShaderModule* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly ShaderModule* Ptr = ptr;
    public static implicit operator ShaderModule*(GpuShaderModule x) => x.Ptr;
    public void Dispose() => Wgpu.ShaderModuleRelease(Ptr);
}

public readonly unsafe struct GpuBindGroupLayout(WebGPU wgpu, BindGroupLayout* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly BindGroupLayout* Ptr = ptr;
    public static implicit operator BindGroupLayout*(GpuBindGroupLayout x) => x.Ptr;
    public void Dispose() => Wgpu.BindGroupLayoutRelease(Ptr);
}

public readonly unsafe struct GpuBindGroup(WebGPU wgpu, BindGroup* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly BindGroup* Ptr = ptr;
    public static implicit operator BindGroup*(GpuBindGroup x) => x.Ptr;
    public void Dispose() => Wgpu.BindGroupRelease(Ptr);
}

public readonly unsafe struct GpuPipelineLayout(WebGPU wgpu, PipelineLayout* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly PipelineLayout* Ptr = ptr;
    public static implicit operator PipelineLayout*(GpuPipelineLayout x) => x.Ptr;
    public void Dispose() => Wgpu.PipelineLayoutRelease(Ptr);
}

public readonly unsafe struct GpuRenderPipeline(WebGPU wgpu, RenderPipeline* ptr) : IDisposable
{
    public readonly WebGPU Wgpu = wgpu;
    public readonly RenderPipeline* Ptr = ptr;
    public static implicit operator RenderPipeline*(GpuRenderPipeline x) => x.Ptr;
    public GpuBindGroupLayout GetBindGroupLayout(uint groupIndex) => new(Wgpu, Wgpu.RenderPipelineGetBindGroupLayout(Ptr, groupIndex));
    public void Dispose() => Wgpu.RenderPipelineRelease(Ptr);
}
