# Wayland Host Contracts

**Branch**: `004-wayland-target` | **Date**: 2026-04-05

This document defines the interface contracts that the Wayland target must implement. Each contract corresponds to an existing Uno Platform abstraction that already has implementations for other platforms (X11, Win32, MacOS, etc.).

## 1. Platform Host Builder

**Interface**: `IPlatformHostBuilder` (internal)

```
WaylandHostBuilder : IPlatformHostBuilder
├── IsSupported → true when WAYLAND_DISPLAY env var is set and connection succeeds
├── Create(appBuilder, appType) → WaylandApplicationHost
├── RenderFrameRate(int fps) → self (fluent)
└── UseSystemHarfBuzz(bool) → self (fluent)
```

**Extension method**: `UseWayland()` on `IUnoPlatformHostBuilder`

```
HostBuilder.UseWayland(builder) → registers WaylandHostBuilder
HostBuilder.UseWayland(builder, Action<WaylandHostBuilder>) → registers with config callback
```

## 2. Application Host

**Base class**: `SkiaHost`
**Interface**: `ISkiaApplicationHost`

```
WaylandApplicationHost : SkiaHost, ISkiaApplicationHost, IDisposable
├── constructor(appBuilder, renderFrameRate, useSystemHarfBuzz)
├── Initialize() → connect to Wayland display, bind globals, register ApiExtensibility extensions
├── RunLoop() → run dispatcher event loop, wait for AllWindowsDone
└── Dispose() → disconnect from Wayland display
```

**Static constructor registers all ApiExtensibility extensions** (see section 8).

## 3. XamlRoot Host

**Interface**: `IXamlRootHost`

```
WaylandXamlRootHost : IXamlRootHost
├── RootElement → Window.RootElement
├── InvalidateRender() → signal render thread via AutoResetEvent
├── ResignNativeFocus() → no-op (Wayland manages focus server-side)
├── WaylandWindow → the native Wayland window struct
├── Closed → Task that completes when window is destroyed
├── static GetHostFromWindow(Window) → lookup host by Window instance
├── static CloseAllWindows() → close all tracked windows
└── static AllWindowsDone() → true when all windows are closed
```

## 4. Native Window Wrapper

**Base class**: `NativeWindowWrapperBase`
**Interface**: `INativeWindowWrapper`

```
WaylandWindowWrapper : NativeWindowWrapperBase
├── Title { get; set; } → xdg_toplevel.set_title
├── NativeWindow → WaylandNativeWindow record
├── Activate() → no-op (Wayland does not allow clients to raise windows)
├── CloseCore() → destroy xdg_toplevel, xdg_surface, wl_surface
├── ShowCore() → initial wl_surface.commit (map the window)
├── Move(position) → no-op (Wayland does not allow client-initiated moves)
├── Resize(size) → resize wl_egl_window or reallocate shm buffers
├── ExtendContentIntoTitleBar(bool) → toggle CSD title bar area
├── ApplyOverlappedPresenter(presenter) → set xdg_toplevel state
└── ApplyFullScreenPresenter() → xdg_toplevel.set_fullscreen
```

**Note**: Wayland intentionally prevents clients from positioning their own windows or raising themselves above other windows. `Move()` and `Activate()` are no-ops. The compositor controls window placement.

## 5. Native Window Factory

**Interface**: `INativeWindowFactoryExtension`

```
WaylandNativeWindowFactoryExtension : INativeWindowFactoryExtension
├── SupportsClosingCancellation → true
├── SupportsMultipleWindows → true
└── CreateWindow(Window, XamlRoot) → WaylandWindowWrapper
```

## 6. Renderers

**Abstract base**:

```
WaylandRenderer : IDisposable
├── Render() → clear background, call CompositionTarget.OnNativePlatformFrameRequested, flush
├── UpdateSize(width, height) → abstract, returns new SKSurface
├── MakeCurrent() → virtual, make EGL context current
├── Flush() → abstract, present buffer to compositor
└── Dispose()
```

**EGL renderer**:

```
WaylandEGLRenderer : WaylandRenderer
├── constructor(host, waylandWindow) → create EGL display from wl_display, EGL surface from wl_egl_window
├── UpdateSize(w, h) → resize wl_egl_window, recreate Skia GRContext surface
├── MakeCurrent() → eglMakeCurrent
├── Flush() → eglSwapBuffers
└── Dispose() → destroy EGL surface, context, display
```

**Software renderer**:

```
WaylandSoftwareRenderer : WaylandRenderer
├── constructor(host, waylandWindow) → create wl_shm_pool, allocate shared memory buffer
├── UpdateSize(w, h) → reallocate shm buffer, create new SKSurface from pixel pointer
├── Flush() → wl_surface.attach(buffer), wl_surface.damage, wl_surface.commit
└── Dispose() → destroy buffer, pool, unmap shared memory
```

## 7. Input Sources

**Pointer**:

```
WaylandPointerInputSource : IUnoCorePointerInputSource
├── Events: PointerPressed, PointerReleased, PointerMoved, PointerEntered, PointerExited, PointerWheelChanged, PointerCaptureLost, PointerCancelled
├── PointerCursor { get; set; } → set cursor via cursor-shape-v1 or wl_cursor
├── HasCapture → bool
├── PointerPosition → last known position
├── SetPointerCapture(pointerId) → implicit in Wayland (pointer grab)
└── ReleasePointerCapture(pointerId) → release grab
```

**Keyboard**:

```
WaylandKeyboardInputSource : IUnoKeyboardInputSource
├── Events: KeyDown, KeyUp
├── ProcessKeyEvent(key, state, serial) → translate via xkbcommon → fire KeyDown/KeyUp
└── ProcessModifiers(depressed, latched, locked, group) → update xkb_state
```

**Touch**:

```
WaylandTouchInputSource : (integrated into pointer input source)
├── ProcessTouchDown(serial, surface, id, x, y) → fire PointerPressed with touch pointer type
├── ProcessTouchUp(serial, id) → fire PointerReleased
├── ProcessTouchMotion(id, x, y) → fire PointerMoved
└── ProcessTouchCancel() → fire PointerCancelled
```

## 8. ApiExtensibility Registrations

The following extensions must be registered in the `WaylandApplicationHost` static constructor:

| Extension Interface | Implementation | Reuse from X11? |
|---|---|---|
| `ICoreApplicationExtension` | `WaylandCoreApplicationExtension` | New (calls `CloseAllWindows`) |
| `IApplicationViewExtension` | `WaylandApplicationViewExtension` | New (limited — no client resize/move) |
| `IDisplayInformationExtension` | `WaylandDisplayInformationExtension` | New (uses `wl_output` scale/geometry) |
| `IUnoCorePointerInputSource` | `WaylandPointerInputSource` | New |
| `IUnoKeyboardInputSource` | `WaylandKeyboardInputSource` | New |
| `INativeWindowFactoryExtension` | `WaylandNativeWindowFactoryExtension` | New |
| `ILauncherExtension` | `LinuxLauncherExtension` | **Reuse** (xdg-open) |
| `IClipboardExtension` | `WaylandClipboardExtension` | New (wl_data_device) |
| `IFileOpenPickerExtension` | `LinuxFilePickerExtension` | **Reuse** (xdg-desktop-portal) |
| `IFolderPickerExtension` | `LinuxFilePickerExtension` | **Reuse** (xdg-desktop-portal) |
| `IFileSavePickerExtension` | `LinuxFileSaverExtension` | **Reuse** (xdg-desktop-portal) |
| `IDragDropExtension` | `WaylandDragDropExtension` | New (wl_data_device DnD) |
| `INativeOpenGLWrapper` | `WaylandNativeOpenGLWrapper` | New (EGL-based) |
| `ISystemThemeHelperExtension` | `LinuxSystemThemeHelper` | **Reuse** (freedesktop portal) |

**5 extensions can be reused** from the X11/Linux target (file pickers, launcher, theme helper).
**9 extensions require new Wayland-specific implementations**.
