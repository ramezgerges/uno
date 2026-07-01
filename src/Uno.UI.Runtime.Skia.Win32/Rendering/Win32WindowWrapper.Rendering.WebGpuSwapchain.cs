#nullable enable

using System;
using System.Runtime.InteropServices;
using Common;
using Silk.NET.WebGPU;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia.Win32;

// EXPERIMENTAL: present the WebGPU frame via a real swapchain instead of the offscreen→readback→GDI-blit path
// (opt-in via UNO_WEBGPU_SWAPCHAIN=1). The frame is still rendered offscreen into the cached target texture,
// but instead of a CPU readback + BitBlt we GPU-blit that texture into the swapchain backbuffer and present —
// no CPU↔GPU round-trip, and the CPU doesn't stall on the readback so frames can pipeline. Falls back to the
// readback path on any failure, so the default behaviour is unaffected.
internal unsafe partial class Win32WindowWrapper
{
	private Surface* _wgpuSurface;
	private GpuRenderPipeline _blitPipe;
	private GpuSampler _blitSampler;
	private Owned<ShaderModule> _blitModule;
	private uint _wgpuSurfaceW, _wgpuSurfaceH;
	private bool _swapchainReady;
	private long _swRenderTicks, _swNoNewFrameTicks; // perf: how often a render tick finds no new built frame

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetModuleHandle(string? lpModuleName);

	private const string BlitWgsl = """
		@vertex fn vs_main(@builtin(vertex_index) vi : u32) -> @builtin(position) vec4f {
			var p = array<vec2f,3>(vec2f(-1,-1), vec2f(3,-1), vec2f(-1,3));
			return vec4f(p[vi], 0, 1);
		}
		@group(0) @binding(0) var samp : sampler;
		@group(0) @binding(1) var tex : texture_2d<f32>;
		@fragment fn fs_main(@builtin(position) pos : vec4f) -> @location(0) vec4f {
			let uv = pos.xy / vec2f(textureDimensions(tex));
			return textureSampleLevel(tex, samp, uv, 0.0);
		}
		""";

	// Renders the frame (no readback) and presents it via the swapchain. Returns false to fall back to readback.
	private bool TryRenderWebGpuSwapchain(global::Microsoft.UI.Xaml.Media.CompositionTarget compositionTarget)
	{
		var ctx = _webGpuContext!;
		var wgpu = ctx.Wgpu;
		try
		{
			_swRenderTicks++;
			if (compositionTarget.OnNativePlatformFrameRequestedWebGpu() is not WebGpuExperiment.WebGpuDrawList drawList)
			{
				_swNoNewFrameTicks++; // render tick fired but the UI thread hasn't produced a new frame yet
				if (Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1" && _swRenderTicks % 120 == 0)
				{
					Console.WriteLine($"[PERF] swapchain render-ticks={_swRenderTicks} no-new-frame={_swNoNewFrameTicks} ({100.0 * _swNoNewFrameTicks / _swRenderTicks:F0}% of ticks had no new built frame → UI-build-starved if high)");
				}
				return true; // no new frame this tick — keep the previously presented backbuffer
			}

			_webGpuFpsHelper.OnFrameRecorded();
			using var fpsScope = _webGpuFpsHelper.BeginFrame();

			bool swPerf = Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1";
			// Diagnostic only (UNO_WEBGPU_GPUPROBE=1): our compute/present numbers are CPU-submit times and never
			// measured the real GPU/present cost. Inserting a WaitIdle (DevicePoll wait) after the scene render and
			// after present forces each phase to fully drain, so the timers below reflect ACTUAL GPU scene-render time
			// vs ACTUAL present/DWM time — telling us where the hidden per-frame cost (seen as the acquire stall) lives.
			// Kills pipelining, so it's strictly for measurement — never leave it on.
			bool gpuProbe = Environment.GetEnvironmentVariable("UNO_WEBGPU_GPUPROBE") == "1";
			long swT0 = swPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

			int width = (int)drawList.Width, height = (int)drawList.Height;
			EnsureSwapchain((uint)width, (uint)height);

			// The frame-rate counter rides the draw list as a top-most image, so it composites into the offscreen
			// render and presents with no swapchain-specific code.
			_webGpuFpsOverlay.Compose(_webGpuFpsHelper, drawList, width, height);

			// Render the frame into the cached target texture WITHOUT reading it back to the CPU.
			drawList.Render(ctx, Windows.UI.Color.FromArgb(255, 255, 255, 255), _webGpuTargets, readback: false);
			if (gpuProbe) { ctx.WaitIdle(); } // drain → swTRender captures true GPU scene-render time
			long swTRender = swPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

			SurfaceTexture st = default;
			wgpu.SurfaceGetCurrentTexture(_wgpuSurface, &st);
			long swTAcquire = swPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
			if (st.Status != SurfaceGetCurrentTextureStatus.Success || st.Texture is null)
			{
				_swapchainReady = false; // reconfigure next frame (resize / lost)
				return true;
			}

			var view = wgpu.TextureCreateView(st.Texture, null);
			var bg = ctx.CreateBindGroup(_blitPipe.GetBindGroupLayout(0),
				new BindGroupEntry { Binding = 0, Sampler = _blitSampler },
				new BindGroupEntry { Binding = 1, TextureView = _webGpuTargets.ColorView });

			using var encoder = ctx.CreateEncoder("blit-enc");
			var ca = new RenderPassColorAttachment { View = view, LoadOp = LoadOp.Clear, StoreOp = StoreOp.Store, ClearValue = new Silk.NET.WebGPU.Color(1, 1, 1, 1) };
			var rpd = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &ca };
			var pass = wgpu.CommandEncoderBeginRenderPass(encoder, ref rpd);
			var enc = new GpuRenderPassEncoder(wgpu, pass);
			enc.SetPipeline(_blitPipe);
			enc.SetBindGroup(0, bg, 0, null);
			enc.Draw(3, 1, 0, 0);
			wgpu.RenderPassEncoderEnd(pass);
			wgpu.RenderPassEncoderRelease(pass);
			ctx.FinishAndSubmit(encoder);

			long swTBlit = swPerf ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
			wgpu.SurfacePresent(_wgpuSurface);
			if (gpuProbe) { ctx.WaitIdle(); } // drain → measures true present/DWM cost (vs blit encode above)
			_webGpuFpsHelper.OnFramePresentRequested();
			if (swPerf)
			{
				double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				var swTPresent = System.Diagnostics.Stopwatch.GetTimestamp();
				Console.WriteLine($"[PERF] swapchain render={Ms(swT0, swTRender):F2} acquire={Ms(swTRender, swTAcquire):F2} blit-encode={Ms(swTAcquire, swTBlit):F2} present={Ms(swTBlit, swTPresent):F2} ms{(gpuProbe ? " [GPUPROBE: render/present are true GPU-drained times]" : "")}");
			}

			bg.Dispose();
			wgpu.TextureViewRelease(view);
			wgpu.TextureRelease(st.Texture);
			return true;
		}
		catch (Exception e)
		{
			this.LogError()?.Error($"WebGPU swapchain present failed: {e}. Falling back to readback.", e);
			return false;
		}
	}

	private void EnsureSwapchain(uint width, uint height)
	{
		var ctx = _webGpuContext!;
		var wgpu = ctx.Wgpu;

		if (_wgpuSurface is null)
		{
			var hwndDesc = new SurfaceDescriptorFromWindowsHWND
			{
				Chain = new ChainedStruct { Next = null, SType = SType.SurfaceDescriptorFromWindowsHwnd },
				Hinstance = (void*)GetModuleHandle(null),
				Hwnd = (void*)(nint)_hwnd,
			};
			var surfDesc = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&hwndDesc };
			_wgpuSurface = wgpu.InstanceCreateSurface(ctx.Instance, ref surfDesc);

			_blitModule = ctx.CreateShaderModuleWgsl("blit", BlitWgsl);
			_blitPipe = ctx.CreateRenderPipeline("blit", _blitModule, vertexLayouts: [],
				bindGroupLayouts: [[
					new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Sampler = new() { Type = SamplerBindingType.Filtering } },
					new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Texture = new() { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } },
				]], targetFormat: TextureFormat.Bgra8Unorm);
			_blitSampler = ctx.Gpu.CreateSampler(new()
			{
				MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest,
				AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge,
				LodMinClamp = 0, LodMaxClamp = 1, MaxAnisotropy = 1,
			});
		}

		if (!_swapchainReady || width != _wgpuSurfaceW || height != _wgpuSurfaceH)
		{
			// Fifo (vsync) is always supported, but SurfaceGetCurrentTexture blocks on it until DWM frees a backbuffer
			// — the dominant per-frame cost (profiled at ~19ms avg, spiking to 40-67ms) when presenting a physical-res
			// surface in windowed mode. UNO_WEBGPU_PRESENT picks a different mode IF the surface supports it (validated
			// against SurfaceGetCapabilities; else falls back to Fifo): mailbox|immediate are non-blocking; fiforelaxed
			// keeps vsync but presents a late frame immediately (tearing) instead of waiting a whole interval — which
			// turns the multi-vsync acquire spikes into at most one interval.
			var presentMode = PresentMode.Fifo;
			var requested = Environment.GetEnvironmentVariable("UNO_WEBGPU_PRESENT")?.ToLowerInvariant() switch
			{
				"mailbox" => (PresentMode?)PresentMode.Mailbox,
				"immediate" => PresentMode.Immediate,
				"fiforelaxed" => PresentMode.FifoRelaxed,
				"fifo" => PresentMode.Fifo,
				_ => null,
			};
			if (requested is { } req && req != PresentMode.Fifo)
			{
				SurfaceCapabilities caps = default;
				wgpu.SurfaceGetCapabilities(_wgpuSurface, ctx.Adapter, ref caps);
				for (nuint i = 0; i < caps.PresentModeCount; i++)
				{
					if (caps.PresentModes[i] == req) { presentMode = req; break; }
				}
				wgpu.SurfaceCapabilitiesFreeMembers(caps);
			}
			var config = new SurfaceConfiguration
			{
				Device = ctx.Device,
				Format = TextureFormat.Bgra8Unorm,
				Usage = TextureUsage.RenderAttachment,
				Width = width,
				Height = height,
				PresentMode = presentMode,
				AlphaMode = CompositeAlphaMode.Auto,
				ViewFormatCount = 0,
				ViewFormats = null,
			};
			wgpu.SurfaceConfigure(_wgpuSurface, ref config);
			if (Environment.GetEnvironmentVariable("UNO_RENDER_PERF") == "1")
			{
				Console.WriteLine($"[PERF] swapchain configured with PresentMode={presentMode} (requested={Environment.GetEnvironmentVariable("UNO_WEBGPU_PRESENT") ?? "fifo"})");
			}
			_wgpuSurfaceW = width; _wgpuSurfaceH = height; _swapchainReady = true;
		}
	}

	private void DisposeSwapchain()
	{
		if (_blitSampler.Ptr is not null) { _blitSampler.Dispose(); }
		if (_blitModule.Ptr is not null) { _blitModule.Dispose(); }
		if (_wgpuSurface is not null && _webGpuContext is { } ctx) { ctx.Wgpu.SurfaceRelease(_wgpuSurface); _wgpuSurface = null; }
	}
}
