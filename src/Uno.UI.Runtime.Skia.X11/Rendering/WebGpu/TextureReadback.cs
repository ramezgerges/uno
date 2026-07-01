using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Buffer = Silk.NET.WebGPU.Buffer;

namespace Common;

/// <summary>
/// The texture→CPU readback dance from lesson 4, packaged: padded staging buffer,
/// encoder copy, submit, map, un-pad. Returns tightly packed rows, ready for
/// <see cref="PngWriter"/>. The texture needs <see cref="TextureUsage.CopySrc"/>.
/// </summary>
public static unsafe class TextureReadback
{
    /// <summary>Reads mip 0 of a 2D Rgba8* texture (4 bytes/texel).</summary>
    public static byte[] ReadRgba8(WebGpuContext ctx, Texture* texture, uint width, uint height)
    {
        bool perf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
        long ts = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var wgpu = ctx.Wgpu;
        uint paddedBpr = (width * 4 + 255) & ~255u;

        var bufferDesc = new BufferDescriptor
        {
            Size = paddedBpr * height,
            Usage = BufferUsage.CopyDst | BufferUsage.MapRead,
        };
        Buffer* staging = wgpu.DeviceCreateBuffer(ctx.Device, ref bufferDesc);
        try
        {
            var src = new ImageCopyTexture
            {
                Texture = texture,
                MipLevel = 0,
                Origin = new Origin3D(0, 0, 0),
                Aspect = TextureAspect.All,
            };
            var dst = new ImageCopyBuffer
            {
                Buffer = staging,
                Layout = new TextureDataLayout(null, 0, paddedBpr, height),
            };
            var extent = new Extent3D(width, height, 1);

            var encoderDesc = new CommandEncoderDescriptor();
            CommandEncoder* encoder = wgpu.DeviceCreateCommandEncoder(ctx.Device, ref encoderDesc);
            wgpu.CommandEncoderCopyTextureToBuffer(encoder, ref src, ref dst, ref extent);
            var cbDesc = new CommandBufferDescriptor();
            CommandBuffer* commandBuffer = wgpu.CommandEncoderFinish(encoder, ref cbDesc);
            wgpu.QueueSubmit(ctx.Queue, 1, &commandBuffer);
            wgpu.CommandBufferRelease(commandBuffer);
            wgpu.CommandEncoderRelease(encoder);

            var status = (BufferMapAsyncStatus)(-1);
            wgpu.BufferMapAsync(staging, MapMode.Read, 0, paddedBpr * height,
                new PfnBufferMapCallback((s, _) => status = s), null);
            while (status == (BufferMapAsyncStatus)(-1))
            {
                ctx.Native.DevicePoll(ctx.Device, true, null);
            }
            if (status != BufferMapAsyncStatus.Success)
            {
                throw new InvalidOperationException($"Readback MapAsync failed: {status}");
            }

            long tMapped = perf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            byte* mapped = (byte*)wgpu.BufferGetConstMappedRange(staging, 0, paddedBpr * height);
            if (mapped is null)
            {
                throw new InvalidOperationException("BufferGetConstMappedRange returned null.");
            }

            var tight = new byte[width * height * 4];
            for (uint y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(mapped + y * paddedBpr, (int)(width * 4))
                    .CopyTo(tight.AsSpan((int)(y * width * 4)));
            }
            wgpu.BufferUnmap(staging);
            if (perf)
            {
                long tEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                System.Console.WriteLine($"[PERF] readback gpuwait+copyToStaging={Ms(ts, tMapped):F2} unpad={Ms(tMapped, tEnd):F2} ms");
            }
            return tight;
        }
        finally
        {
            wgpu.BufferRelease(staging);
        }
    }
}
