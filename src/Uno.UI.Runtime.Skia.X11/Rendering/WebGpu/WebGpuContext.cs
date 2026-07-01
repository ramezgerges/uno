using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;
using WgpuNativeExtension = Silk.NET.WebGPU.Extensions.WGPU.Wgpu;

namespace Common;

/// <summary>
/// Owns the WebGPU initialization chain — Instance → Adapter → Device → Queue —
/// so each lesson starts from a working device instead of repeating the ceremony.
/// Dispose releases everything in reverse order of creation.
/// </summary>
public sealed unsafe class WebGpuContext : IDisposable
{
    public WebGPU Wgpu { get; }

    /// <summary>
    /// wgpu-native-specific functions beyond portable webgpu.h (DevicePoll, log
    /// callbacks, ...). Portable wgpuInstanceProcessEvents is an unimplemented
    /// stub in this wgpu-native build (hard-panics), so DevicePoll is the only
    /// working way to pump async callbacks like BufferMapAsync's.
    /// </summary>
    public WgpuNativeExtension Native { get; }

    public Instance* Instance { get; private set; }
    public Adapter* Adapter { get; private set; }
    public Device* Device { get; private set; }
    public Queue* Queue { get; private set; }

    /// <summary>True when timestamp-query was requested (UNO_WEBGPU_GPUTIME=1) AND the adapter supports it.</summary>
    public bool TimestampEnabled { get; private set; }

    /// <summary>The device as a thin OOP handle (see Gpu.cs). Same pointer, method syntax.</summary>
    public GpuDevice Gpu => new(Wgpu, Device);
    /// <summary>The queue as a thin OOP handle.</summary>
    public GpuQueue GpuQueue => new(Wgpu, Queue);

    /// <summary>
    /// Called for every error the device reports that nothing else captured
    /// (validation, out-of-memory, ...). Swap it out per lesson if needed.
    /// </summary>
    public Action<ErrorType, string> OnUncapturedError { get; set; } =
        static (type, message) => Console.Error.WriteLine($"[WebGPU {type}] {message}");

    // The device stores this function pointer for its entire lifetime, so the GC
    // must never collect the underlying delegate: it lives here, not in a local.
    private readonly PfnErrorCallback _errorCallback;
    // The log callback is global to wgpu-native (not per-device) and may be called
    // from any wgpu entry point forever after — rooted statically.
    private static PfnLogCallback _logCallback;
    private bool _disposed;
    private readonly bool _adopted;

    /// <summary>
    /// Adopt externally-created handles (e.g. a surface-compatible device created alongside an
    /// X11 WebGPU surface). The caller owns the handles; Dispose does NOT release them.
    /// </summary>
    public WebGpuContext(WebGPU wgpu, Instance* instance, Adapter* adapter, Device* device, Queue* queue)
    {
        Wgpu = wgpu;
        Instance = instance;
        Adapter = adapter;
        Device = device;
        Queue = queue;
        _adopted = true;
        if (!Wgpu.TryGetDeviceExtension(null, out WgpuNativeExtension native))
        {
            throw new InvalidOperationException("wgpu-native extension functions not available.");
        }
        Native = native;
        _errorCallback = new PfnErrorCallback((type, message, _) =>
        {
            try { OnUncapturedError(type, Marshal.PtrToStringUTF8((nint)message) ?? ""); }
            catch (Exception e) { Console.Error.WriteLine($"OnUncapturedError handler threw: {e}"); }
        });
        Wgpu.DeviceSetUncapturedErrorCallback(Device, _errorCallback, null);
    }

    /// <summary>
    /// Routes wgpu-native's internal diagnostics log to stderr. Levels: Error,
    /// Warn (default-ish), Info, Debug, Trace. Trace logs every API call — loud
    /// but extremely instructive. wgpu-native specific, not portable WebGPU.
    /// </summary>
    public void EnableNativeLog(LogLevel level)
    {
        _logCallback = new PfnLogCallback((lvl, message, _) =>
        {
            try
            {
                Console.Error.WriteLine($"[wgpu {lvl,-5}] {Marshal.PtrToStringUTF8((nint)message)}");
            }
            catch
            {
                // never let an exception escape into native frames
            }
        });
        Native.SetLogCallback(_logCallback, null);
        Native.SetLogLevel(level);
    }

    public WebGpuContext(
        string deviceLabel = "lesson-device",
        Limits? requiredLimits = null,
        PowerPreference powerPreference = PowerPreference.HighPerformance)
    {
        Wgpu = WebGPU.GetApi();
        if (!Wgpu.TryGetDeviceExtension(null, out WgpuNativeExtension native))
        {
            throw new InvalidOperationException("wgpu-native extension functions not available.");
        }
        Native = native;

        // ---- Instance -------------------------------------------------------
        var instanceDescriptor = new InstanceDescriptor();
        Instance = Wgpu.CreateInstance(ref instanceDescriptor);
        if (Instance is null)
        {
            throw new InvalidOperationException("Could not create WebGPU instance.");
        }

        // ---- Adapter --------------------------------------------------------
        // Note: callbacks in this wgpu-native version fire synchronously inside
        // the request call, which is what lets this constructor be sequential.
        var adapterOptions = new RequestAdapterOptions
        {
            PowerPreference = powerPreference,
        };
        Adapter* adapter = null;
        var adapterStatus = (RequestAdapterStatus)(-1);
        string? adapterMessage = null;
        Exception? callbackException = null;
        var adapterCallback = new PfnRequestAdapterCallback((status, result, message, _) =>
        {
            // Exceptions must never escape into native frames; stash and rethrow below.
            try
            {
                adapterStatus = status;
                adapter = result;
                adapterMessage = Marshal.PtrToStringUTF8((nint)message);
            }
            catch (Exception e)
            {
                callbackException = e;
            }
        });
        Wgpu.InstanceRequestAdapter(Instance, ref adapterOptions, adapterCallback, null);
        if (callbackException is not null)
        {
            throw callbackException;
        }
        if (adapterStatus is not RequestAdapterStatus.Success || adapter is null)
        {
            throw new InvalidOperationException(
                $"InstanceRequestAdapter failed with status {adapterStatus}: '{adapterMessage}'");
        }
        Adapter = adapter;

        // ---- Device ---------------------------------------------------------
        // The descriptor (and everything it points at) only has to stay alive for
        // the duration of the request call — wgpu copies what it needs.
        var requiredLimitsValue = new RequiredLimits { Limits = requiredLimits ?? default };
        nint labelPtr = Marshal.StringToCoTaskMemUTF8(deviceLabel);
        try
        {
            // Opt-in GPU-side timing: request the timestamp-query feature when asked AND supported (lavapipe isn't).
            bool wantTimestamp = Environment.GetEnvironmentVariable("UNO_WEBGPU_GPUTIME") == "1"
                && Wgpu.AdapterHasFeature(Adapter, FeatureName.TimestampQuery);
            // Enable wgpu-native's adapter-specific format features when the adapter advertises them — the WebGPU
            // spec only guarantees 1×/4× MSAA for Rgba8Unorm, so without this feature the device rejects 8×
            // (UNO_WEBGPU_MSAA=8). Harmless when unused, so enable it whenever available.
            var fmtFeature = (FeatureName)Silk.NET.WebGPU.Extensions.WGPU.NativeFeature.TextureAdapterSpecificFormatFeatures;
            bool wantFmt = Wgpu.AdapterHasFeature(Adapter, fmtFeature);
            FeatureName* feats = stackalloc FeatureName[2];
            uint featCount = 0;
            if (wantTimestamp) { feats[featCount++] = FeatureName.TimestampQuery; }
            if (wantFmt) { feats[featCount++] = fmtFeature; }
            var deviceDescriptor = new DeviceDescriptor
            {
                Label = (byte*)labelPtr,
                RequiredLimits = requiredLimits is null ? null : &requiredLimitsValue,
                RequiredFeatureCount = featCount,
                RequiredFeatures = featCount > 0 ? feats : null,
            };
            TimestampEnabled = wantTimestamp;
            Device* device = null;
            var deviceStatus = (RequestDeviceStatus)(-1);
            string? deviceMessage = null;
            var deviceCallback = new PfnRequestDeviceCallback((status, result, message, _) =>
            {
                try
                {
                    deviceStatus = status;
                    device = result;
                    deviceMessage = Marshal.PtrToStringUTF8((nint)message);
                }
                catch (Exception e)
                {
                    callbackException = e;
                }
            });
            Wgpu.AdapterRequestDevice(Adapter, ref deviceDescriptor, deviceCallback, null);
            if (callbackException is not null)
            {
                throw callbackException;
            }
            if (deviceStatus is not RequestDeviceStatus.Success || device is null)
            {
                throw new InvalidOperationException(
                    $"AdapterRequestDevice failed with status {deviceStatus}: '{deviceMessage}'");
            }
            Device = device;
        }
        finally
        {
            Marshal.FreeCoTaskMem(labelPtr);
        }

        _errorCallback = new PfnErrorCallback((type, message, _) =>
        {
            try
            {
                OnUncapturedError(type, Marshal.PtrToStringUTF8((nint)message) ?? "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"OnUncapturedError handler threw: {e}");
            }
        });
        Wgpu.DeviceSetUncapturedErrorCallback(Device, _errorCallback, null);

        // ---- Queue ----------------------------------------------------------
        Queue = Wgpu.DeviceGetQueue(Device);
    }

    /// <summary>"name | backend | adapter type", e.g. "llvmpipe (...) | Vulkan | Cpu".</summary>
    public string AdapterSummary
    {
        get
        {
            AdapterProperties props = default;
            Wgpu.AdapterGetProperties(Adapter, ref props);
            return $"{Marshal.PtrToStringUTF8((nint)props.Name)} | {props.BackendType} | {props.AdapterType}";
        }
    }

    public Limits AdapterLimits
    {
        get
        {
            SupportedLimits limits = default;
            Wgpu.AdapterGetLimits(Adapter, ref limits);
            return limits.Limits;
        }
    }

    public Limits DeviceLimits
    {
        get
        {
            SupportedLimits limits = default;
            Wgpu.DeviceGetLimits(Device, ref limits);
            return limits.Limits;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return; // second dispose is a silent no-op, never a throw
        }
        _disposed = true;
        if (_adopted)
        {
            return; // caller owns the handles
        }
        Wgpu.QueueRelease(Queue);
        Wgpu.DeviceRelease(Device);
        Wgpu.AdapterRelease(Adapter);
        Wgpu.InstanceRelease(Instance);
        Queue = null;
        Device = null;
        Adapter = null;
        Instance = null;
    }
}
