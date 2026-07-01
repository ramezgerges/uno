using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Common;

/// <summary>
/// Sugar over the ceremony mastered in lessons 1–4: create + label + null-check +
/// release pairing, queue writes, and the map → poll → check → copy → unmap dance.
/// Deliberately does NOT wrap anything not yet covered in a lesson.
/// </summary>
public static unsafe class WebGpuContextExtensions
{
    // ---- creation -----------------------------------------------------------

    /// <summary>
    /// Create a buffer pre-filled with <paramref name="data"/> in ONE expression (sized to
    /// the data, written via mapped-at-creation). No CopyDst is needed or added — the initial
    /// contents go in through the creation mapping, so `usage` is exactly what you pass.
    /// </summary>
    public static GpuBuffer CreateBuffer<T>(this WebGpuContext ctx, string label,
        BufferUsage usage, ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * sizeof(T));
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            var desc = new BufferDescriptor { Label = (byte*)labelPtr, Size = size, Usage = usage, MappedAtCreation = true };
            Buffer* buffer = ctx.Wgpu.DeviceCreateBuffer(ctx.Device, ref desc);
            if (buffer is null)
            {
                throw new InvalidOperationException($"Failed to create buffer '{label}'.");
            }
            data.CopyTo(new Span<T>(ctx.Wgpu.BufferGetMappedRange(buffer, 0, (nuint)size), data.Length));
            ctx.Wgpu.BufferUnmap(buffer);
            return new GpuBuffer(ctx.Wgpu, buffer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    public static Owned<Buffer> CreateBuffer(this WebGpuContext ctx, string label,
        ulong size, BufferUsage usage, bool mappedAtCreation = false)
    {
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            var desc = new BufferDescriptor
            {
                Label = (byte*)labelPtr,
                Size = size,
                Usage = usage,
                MappedAtCreation = mappedAtCreation,
            };
            Buffer* buffer = ctx.Wgpu.DeviceCreateBuffer(ctx.Device, ref desc);
            if (buffer is null)
            {
                throw new InvalidOperationException($"Failed to create buffer '{label}'.");
            }
            return new Owned<Buffer>(buffer, p => ctx.Wgpu.BufferRelease((Buffer*)p));
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    public static Owned<Texture> CreateTexture2D(this WebGpuContext ctx, string label,
        uint width, uint height, TextureUsage usage,
        TextureFormat format = TextureFormat.Rgba8Unorm,
        uint mipLevelCount = 1, uint sampleCount = 1)
    {
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            var desc = new TextureDescriptor
            {
                Label = (byte*)labelPtr,
                Size = new Extent3D(width, height, 1),
                Dimension = TextureDimension.Dimension2D,
                Format = format,
                Usage = usage,
                MipLevelCount = mipLevelCount,
                SampleCount = sampleCount,
            };
            Texture* texture = ctx.Wgpu.DeviceCreateTexture(ctx.Device, ref desc);
            if (texture is null)
            {
                throw new InvalidOperationException($"Failed to create texture '{label}'.");
            }
            return new Owned<Texture>(texture, p => ctx.Wgpu.TextureRelease((Texture*)p));
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    /// <summary>Full default view: every mip, every layer, texture's own format.</summary>
    public static Owned<TextureView> CreateView(this WebGpuContext ctx, Texture* texture)
    {
        TextureView* view = ctx.Wgpu.TextureCreateView(texture, null);
        if (view is null)
        {
            throw new InvalidOperationException("Failed to create texture view.");
        }
        return new Owned<TextureView>(view, p => ctx.Wgpu.TextureViewRelease((TextureView*)p));
    }

    public static Owned<CommandEncoder> CreateEncoder(this WebGpuContext ctx, string label)
    {
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(label);
        try
        {
            var desc = new CommandEncoderDescriptor { Label = (byte*)labelPtr };
            CommandEncoder* encoder = ctx.Wgpu.DeviceCreateCommandEncoder(ctx.Device, ref desc);
            if (encoder is null)
            {
                throw new InvalidOperationException($"Failed to create command encoder '{label}'.");
            }
            return new Owned<CommandEncoder>(encoder, p => ctx.Wgpu.CommandEncoderRelease((CommandEncoder*)p));
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }
    }

    // ---- submission ---------------------------------------------------------

    /// <summary>
    /// Finish → submit → release the command buffer. Returns the submission index
    /// (usable with WrappedSubmissionIndex / DevicePoll). The encoder itself is
    /// still owned by the caller — it is spent, but its release stays paired with
    /// its creation.
    /// </summary>
    public static ulong FinishAndSubmit(this WebGpuContext ctx, CommandEncoder* encoder)
    {
        var desc = new CommandBufferDescriptor();
        CommandBuffer* commandBuffer = ctx.Wgpu.CommandEncoderFinish(encoder, ref desc);
        if (commandBuffer is null)
        {
            throw new InvalidOperationException("CommandEncoderFinish returned null.");
        }
        ulong index = ctx.Native.QueueSubmitForIndex(ctx.Queue, 1, &commandBuffer);
        ctx.Wgpu.CommandBufferRelease(commandBuffer);
        return index;
    }

    /// <summary>Block until all submitted GPU work completes (and pump callbacks).</summary>
    public static void WaitIdle(this WebGpuContext ctx)
        => ctx.Native.DevicePoll(ctx.Device, true, null);

    // ---- data transfer ------------------------------------------------------

    public static void WriteBuffer<T>(this WebGpuContext ctx, Buffer* buffer,
        ReadOnlySpan<T> data, ulong bufferOffset = 0) where T : unmanaged
    {
        fixed (T* p = data)
        {
            ctx.Wgpu.QueueWriteBuffer(ctx.Queue, buffer, bufferOffset, p,
                (nuint)(data.Length * sizeof(T)));
        }
    }

    /// <summary>
    /// The full lesson-2 readback dance: MapAsync → poll → status check → copy out
    /// → unmap. Buffer needs MapRead usage. Offset must be 8-byte aligned and the
    /// byte size (count * sizeof(T)) 4-byte aligned, per the mapping rules.
    /// </summary>
    public static T[] ReadBuffer<T>(this WebGpuContext ctx, Buffer* buffer,
        int count, ulong offset = 0) where T : unmanaged
    {
        ulong byteSize = (ulong)(count * sizeof(T));
        var status = (BufferMapAsyncStatus)(-1);
        ctx.Wgpu.BufferMapAsync(buffer, MapMode.Read, (nuint)offset, (nuint)byteSize,
            new PfnBufferMapCallback((s, _) => status = s), null);
        while (status == (BufferMapAsyncStatus)(-1))
        {
            ctx.Native.DevicePoll(ctx.Device, true, null);
        }
        if (status != BufferMapAsyncStatus.Success)
        {
            throw new InvalidOperationException($"MapAsync failed: {status}");
        }
        void* mapped = ctx.Wgpu.BufferGetConstMappedRange(buffer, (nuint)offset, (nuint)byteSize);
        if (mapped is null)
        {
            throw new InvalidOperationException("BufferGetConstMappedRange returned null.");
        }
        var result = new T[count];
        new ReadOnlySpan<T>(mapped, count).CopyTo(result);
        ctx.Wgpu.BufferUnmap(buffer);
        return result;
    }
}
