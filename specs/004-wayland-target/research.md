# Research: Native Wayland Target for Uno Platform

**Branch**: `004-wayland-target` | **Date**: 2026-04-05

## R1: Platform Host Architecture

**Decision**: Follow the exact same host builder pattern as X11, creating `WaylandHostBuilder`, `WaylandApplicationHost`, and `WaylandXamlRootHost`.

**Rationale**: The existing `IPlatformHostBuilder` → `ISkiaApplicationHost` → `IXamlRootHost` hierarchy is well-established across all Skia targets (X11, Win32, MacOS, FrameBuffer). Consistency reduces maintenance burden and ensures the Wayland target integrates seamlessly with the `UnoPlatformHostBuilder.UseWayland()` chain.

**Alternatives considered**:
- Extending X11 host with Wayland fallback: Rejected because X11 and Wayland have fundamentally different windowing models (server-managed vs. client-managed), making a shared host overly complex.
- Using a third-party managed Wayland library (e.g., WaylandSharp): Rejected because no mature, maintained .NET Wayland binding exists. P/Invoke to libwayland-client follows the same pattern as X11's Xlib P/Invoke and keeps the dependency minimal.

## R2: Platform Detection and Priority

**Decision**: Detect Wayland via `WAYLAND_DISPLAY` environment variable. When both `WAYLAND_DISPLAY` and `DISPLAY` are set, prefer Wayland unless overridden by `UNO_PLATFORM_BACKEND=x11` environment variable.

**Rationale**: `WAYLAND_DISPLAY` is the standard indicator set by all Wayland compositors. Most Wayland sessions also set `DISPLAY` for XWayland compatibility, so Wayland must take priority. The user's instruction specifies `.UseWayland()` in the builder chain; registration order in `Program.cs` determines which builder is tried first.

**Alternatives considered**:
- Socket file detection at `/run/user/{uid}/wayland-*`: More robust but unnecessarily complex; `WAYLAND_DISPLAY` is universally set.
- D-Bus session query: Over-engineered for detection; D-Bus may not be available in minimal environments.

## R3: Wayland Protocol Bindings

**Decision**: Create managed P/Invoke bindings to `libwayland-client.so` in a `Wayland_Bindings/` directory, following the same pattern as `X11_Bindings/`.

**Rationale**: The X11 target uses direct P/Invoke to Xlib functions with managed struct definitions. This approach is proven in the codebase, avoids third-party dependencies, and gives full control over which protocol interfaces are used.

**Key protocols to bind**:
- **Core**: `wl_display`, `wl_registry`, `wl_compositor`, `wl_surface`, `wl_shm`, `wl_seat`, `wl_output`
- **Shell**: `xdg_wm_base`, `xdg_surface`, `xdg_toplevel` (via `xdg-shell`)
- **Input**: `wl_pointer`, `wl_keyboard`, `wl_touch`
- **Clipboard/DnD**: `wl_data_device_manager`, `wl_data_device`, `wl_data_source`, `wl_data_offer`
- **Decorations**: `zxdg_decoration_manager_v1` (unstable but widely supported)
- **Scaling**: `wp_fractional_scale_manager_v1`, `wp_viewporter`
- **Cursor**: `wp_cursor_shape_manager_v1` (or fallback to `wl_cursor` library)

**Alternatives considered**:
- wayland-scanner codegen: Standard in C projects but would require a custom build step. Manual P/Invoke is simpler for the subset of protocols needed.

## R4: Rendering Strategy

**Decision**: Use EGL as the primary GPU rendering path, with wl_shm software rendering as fallback. No GLX (GLX is X11-specific).

**Rationale**: EGL is the native OpenGL surface management API for Wayland (via `wl_egl_window`). The X11 target already has an EGL renderer (`X11EGLRenderer`) whose pattern can be adapted. Software rendering via `wl_shm` shared memory buffers is the universal fallback that works on all compositors.

**Rendering pipeline**:
1. Create `wl_surface` → attach `wl_egl_window` for EGL, or `wl_shm_pool` + `wl_buffer` for software
2. Create EGL display/surface from `wl_display`/`wl_egl_window`
3. Skia `GRContext` (GL backend) renders to EGL surface
4. `eglSwapBuffers()` presents to compositor
5. For software: Skia renders to `SKSurface` backed by shared memory, then `wl_surface.attach()` + `wl_surface.commit()`

**Alternatives considered**:
- Vulkan renderer: Better long-term but significantly more complex. Can be added later as an additional renderer alongside EGL.
- DMA-BUF buffer sharing: Advanced optimization for zero-copy; not needed for initial implementation.

## R5: Event Loop and Threading

**Decision**: Single event dispatch thread using `wl_display_dispatch()` with `poll()` on the Wayland file descriptor, similar to X11's event thread model.

**Rationale**: Wayland's event model is simpler than X11's — a single fd for all events, with `wl_display_dispatch()` handling deserialization and listener callbacks. The X11 target uses per-window event threads with `poll()` on the X11 fd; Wayland can use a single thread since all events come through one connection.

**Threading model**:
- **Main thread**: Uno dispatcher (UI thread)
- **Event thread**: `poll()` on `wl_display_get_fd()`, calls `wl_display_dispatch()`, queues input events to dispatcher
- **Render thread**: High-priority background thread (same as X11), triggered by `InvalidateRender()`

**Alternatives considered**:
- Integrating Wayland dispatch into the main thread event loop: Possible but would require careful integration with `CoreDispatcher`. Separate thread is proven in the X11 target.

## R6: Keyboard Input (xkbcommon)

**Decision**: Use `libxkbcommon` for keymap handling, consistent with the FrameBuffer target which already uses it.

**Rationale**: Wayland delivers keymaps as XKB keymap files (via `wl_keyboard.keymap` event). The `xkbcommon` library is the standard tool for processing these. The FrameBuffer target already has xkbcommon P/Invoke bindings that can be reused.

**Key mappings**:
- `wl_keyboard.keymap` → `xkb_keymap_new_from_string()`
- `wl_keyboard.key` → `xkb_state_key_get_one_sym()` → Uno `VirtualKey`
- `wl_keyboard.modifiers` → `xkb_state_update_mask()`

## R7: Window Decorations

**Decision**: Request server-side decorations via `zxdg_decoration_manager_v1`. If unavailable, use client-side decorations (CSD).

**Rationale**: GNOME/Mutter uses CSD by default. KDE/KWin prefers SSD. Sway/wlroots support the decoration protocol. By requesting SSD and falling back to CSD, the app respects compositor preferences.

**CSD fallback**: For initial implementation, CSD fallback can be minimal (no title bar drawing). The app will still be functional; users can resize/move via compositor-provided mechanisms (alt+drag, etc.). Full CSD with title bar rendering can be added later.

## R8: DPI and Fractional Scaling

**Decision**: Support integer scaling via `wl_output.scale` and fractional scaling via `wp_fractional_scale_v1` + `wp_viewporter`.

**Rationale**: Integer scaling is the baseline supported by all compositors. Fractional scaling (125%, 150%, 175%) is increasingly common on modern displays and is handled by the `wp_fractional_scale_v1` protocol (stable since 2023). The viewporter protocol is needed alongside fractional scaling to let the compositor handle the final scaling step.

## R9: File Pickers

**Decision**: Reuse the existing `LinuxFilePickerExtension` and `LinuxFileSaverExtension` which use xdg-desktop-portal via D-Bus.

**Rationale**: The X11 target already uses xdg-desktop-portal for file pickers (not X11-specific at all). These extensions are Linux-generic and work on both X11 and Wayland. No new implementation needed.

## R10: System Theme Detection

**Decision**: Reuse the existing `LinuxSystemThemeHelper` which reads the system theme via D-Bus (freedesktop settings portal).

**Rationale**: Same as file pickers — this is a Linux-generic service, not X11-specific.

## R11: Testing Strategy

**Decision**: Use headless Weston compositor for CI testing. For local development, any Wayland compositor works.

**Rationale**: Weston is the reference Wayland compositor and supports headless mode (`weston --backend=headless`). This allows running the SamplesApp and runtime tests in CI without a physical display. The user explicitly mentioned this approach.

**Test setup**:
```bash
# Install weston
apt-get install weston

# Start headless weston
weston --backend=headless &

# Set env for Uno app
export WAYLAND_DISPLAY=wayland-1

# Build and run
cd src
dotnet build SamplesApp/SamplesApp.Skia.Generic -p:UnoTargetFrameworkOverride=net10.0
dotnet run --project SamplesApp/SamplesApp.Skia.Generic -p:UnoTargetFrameworkOverride=net10.0
```

## R12: Clipboard Protocol

**Decision**: Implement clipboard via `wl_data_device_manager` / `wl_data_device` / `wl_data_source` / `wl_data_offer`.

**Rationale**: This is the only clipboard mechanism in Wayland (no equivalent to X11's XSelection shortcuts). The protocol is event-driven: the app creates a `wl_data_source` with offered MIME types, sets it on the `wl_data_device`, and when another client requests data, writes to a provided file descriptor.

**Alternatives considered**:
- Using a clipboard daemon (wl-clipboard): Shelling out to external tools is fragile. Direct protocol usage is more reliable.

## R13: Drag and Drop

**Decision**: Implement drag-and-drop using the same `wl_data_device` protocol that handles clipboard, extended with drag-specific events (`enter`, `motion`, `leave`, `drop`).

**Rationale**: Wayland unifies clipboard and DnD under the data device protocol, unlike X11 which uses completely separate mechanisms (XSelection vs XDND). This actually simplifies the implementation since much of the data transfer infrastructure is shared.
