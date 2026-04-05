# Tasks: Native Wayland Target for Uno Platform

**Input**: Design documents from `/specs/004-wayland-target/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Tests are not explicitly requested in the spec. Test tasks are omitted. Validation is done via SamplesApp build/run after each phase.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **New project**: `src/Uno.UI.Runtime.Skia.Wayland/`
- **Bindings**: `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/`
- **SamplesApp**: `src/SamplesApp/SamplesApp.Skia.Generic/`
- **Solution filter**: `src/Uno.UI-Skia-only.slnf`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the Wayland runtime project, P/Invoke bindings, and integrate into the build system

- [x] T001 Create project file `src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj` with TargetFrameworks, PackageId, project references to Uno.UI.Runtime.Skia, Uno.UI.Skia, Uno.Foundation.Skia, Uno.Skia, and package references to SkiaSharp, SkiaSharp.NativeAssets.Linux, HarfBuzzSharp.NativeAssets.Linux
- [x] T002 [P] Create core Wayland P/Invoke bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WaylandBindings.cs` — P/Invoke declarations for libwayland-client: wl_display_connect, wl_display_disconnect, wl_display_dispatch, wl_display_roundtrip, wl_display_flush, wl_display_get_fd, wl_registry_bind, wl_proxy_marshal_flags, wl_proxy_add_listener, wl_proxy_destroy, wl_proxy_get_version
- [x] T003 [P] Create Wayland struct and enum definitions in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WaylandStructs.cs` — wl_message, wl_interface definitions, listener structs for wl_registry, wl_surface, wl_seat, wl_output, wl_pointer, wl_keyboard, wl_touch, wl_callback
- [x] T004 [P] Create Wayland enum definitions in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WaylandEnums.cs` — wl_shm_format, wl_seat_capability, wl_pointer_button_state, wl_keyboard_key_state, wl_output_transform, wl_output_subpixel
- [x] T005 [P] Create xdg-shell P/Invoke bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/XdgShellBindings.cs` — xdg_wm_base, xdg_surface, xdg_toplevel interface definitions, listener structs, and request wrappers (get_xdg_surface, get_toplevel, set_title, set_app_id, ack_configure, set_fullscreen, unset_fullscreen, set_minimized, set_maximized, unset_maximized)
- [x] T006 [P] Create EGL P/Invoke bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/EglBindings.cs` — eglGetDisplay, eglInitialize, eglChooseConfig, eglCreateContext, eglCreateWindowSurface, eglMakeCurrent, eglSwapBuffers, eglDestroySurface, eglDestroyContext, eglTerminate, wl_egl_window_create, wl_egl_window_destroy, wl_egl_window_resize
- [x] T007 [P] Create xkbcommon P/Invoke bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/XkbCommonBindings.cs` — xkb_context_new, xkb_keymap_new_from_string, xkb_state_new, xkb_state_key_get_one_sym, xkb_state_key_get_utf8, xkb_state_update_mask, xkb_state_mod_name_is_active, xkb_keymap_unref, xkb_state_unref, xkb_context_unref
- [x] T008 Add `Uno.UI.Runtime.Skia.Wayland.csproj` to `src/Uno.UI-Skia-only.slnf` solution filter
- [ ] T009 Add `Uno.UI.Runtime.Skia.Wayland.csproj` to `src/Uno.UI.sln` solution file
- [x] T010 Add project reference to `Uno.UI.Runtime.Skia.Wayland` in `src/SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj`
- [x] T011 Verify build succeeds: `dotnet build src/Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj -p:UnoTargetFrameworkOverride=net10.0`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core host infrastructure that ALL user stories depend on — builder pattern, application host, display connection, global registry binding

**CRITICAL**: No user story work can begin until this phase is complete

- [x] T012 Create `UseWayland()` extension methods in `src/Uno.UI.Runtime.Skia.Wayland/Builder/HostBuilder.cs` — two overloads: parameterless and with `Action<WaylandHostBuilder>` config callback, following the exact pattern from `src/Uno.UI.Runtime.Skia.X11/Builder/HostBuilder.cs`
- [x] T013 Create `WaylandHostBuilder` in `src/Uno.UI.Runtime.Skia.Wayland/Builder/WaylandHostBuilder.cs` — implement `IPlatformHostBuilder` with `IsSupported` checking `WAYLAND_DISPLAY` env var via `OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null`, `Create()` returning `WaylandApplicationHost`, fluent `RenderFrameRate()` and `UseSystemHarfBuzz()` methods
- [x] T014 Create `WaylandApplicationHost` in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandApplicationHost.cs` — inherit `SkiaHost`, implement `ISkiaApplicationHost`, `IDisposable`. Static constructor: call `wl_display_connect(null)` to validate, register all ApiExtensibility extensions. `Initialize()`: connect to display, bind globals via registry. `RunLoop()`: run dispatcher loop, wait for `AllWindowsDone`. `Dispose()`: disconnect display
- [x] T015 Create `WaylandWindow` struct in `src/Uno.UI.Runtime.Skia.Wayland/UI/Xaml/Window/WaylandWindow.cs` — struct holding `IntPtr Display`, `IntPtr Surface`, `IntPtr XdgSurface`, `IntPtr XdgToplevel`, `int Width`, `int Height`, `float Scale`, `bool Configured`
- [x] T016 Create `WaylandXamlRootHost` in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandXamlRootHost.cs` — implement `IXamlRootHost` with `RootElement`, `InvalidateRender()` via `AutoResetEvent`, static `ConcurrentDictionary<Window, WaylandXamlRootHost>` for window tracking, `GetHostFromWindow()`, `CloseAllWindows()`, `AllWindowsDone()`, event loop thread with `poll()` on `wl_display_get_fd()` calling `wl_display_dispatch()`
- [x] T017 Create `WaylandCoreApplicationExtension` in `src/Uno.UI.Runtime.Skia.Wayland/ApplicationModel/Core/WaylandCoreApplicationExtension.cs` — implement `ICoreApplicationExtension` with `CanExit => true` and `Exit() => WaylandXamlRootHost.CloseAllWindows()`
- [x] T018 Add `.UseWayland()` call to `src/SamplesApp/SamplesApp.Skia.Generic/Program.cs` — insert before `.UseX11()` in the builder chain
- [x] T019 Verify build and basic launch: build SamplesApp with `-p:UnoTargetFrameworkOverride=net10.0`, confirm no compilation errors and that `WaylandHostBuilder.IsSupported` returns correct values based on environment

**Checkpoint**: Foundation ready — the project builds, the host builder is registered, and the application host can connect to a Wayland display

---

## Phase 3: User Story 1 — Run Uno App on Native Wayland (Priority: P1) MVP

**Goal**: Launch a SamplesApp window on a Wayland compositor with software rendering and no input — the window appears and renders the UI

**Independent Test**: Start headless Weston, set `WAYLAND_DISPLAY`, run SamplesApp. A window surface should appear with the rendered Uno UI. Verify no crashes or exceptions in stdout/stderr.

### Implementation for User Story 1

- [x] T020 [US1] Create `WaylandNativeWindowFactoryExtension` in `src/Uno.UI.Runtime.Skia.Wayland/UI/Xaml/Window/WaylandNativeWindowFactoryExtension.cs` — implement `INativeWindowFactoryExtension` with `SupportsClosingCancellation => true`, `SupportsMultipleWindows => true`, `CreateWindow()` returning new `WaylandWindowWrapper`
- [x] T021 [US1] Create `WaylandWindowWrapper` in `src/Uno.UI.Runtime.Skia.Wayland/UI/Xaml/Window/WaylandWindowWrapper.cs` — inherit `NativeWindowWrapperBase`, implement window creation via `wl_compositor_create_surface()` + `xdg_wm_base_get_xdg_surface()` + `xdg_surface_get_toplevel()`. Implement `Title` via `xdg_toplevel_set_title()`, `ShowCore()` via `wl_surface_commit()`, `CloseCore()` to destroy all protocol objects, `Activate()` and `Move()` as no-ops, `Resize()` to resize `wl_egl_window` or reallocate shm buffers. Handle `xdg_surface.configure` event to acknowledge and update size. Implement `ApplyFullScreenPresenter()` via `xdg_toplevel_set_fullscreen()`
- [x] T022 [US1] Create abstract `WaylandRenderer` in `src/Uno.UI.Runtime.Skia.Wayland/Rendering/WaylandRenderer.cs` — `Render()` method that clears background, calls `CompositionTarget.OnNativePlatformFrameRequested()` to drive Skia rendering, then calls abstract `Flush()`. Abstract methods: `UpdateSize(int, int) → SKSurface`, `Flush()`, `Dispose()`. Virtual method: `MakeCurrent()` (no-op by default)
- [x] T023 [US1] Create `WaylandSoftwareRenderer` in `src/Uno.UI.Runtime.Skia.Wayland/Rendering/WaylandSoftwareRenderer.cs` — create shared memory via `mmap`/`shm_open`, create `wl_shm_pool` and `wl_buffer`. `UpdateSize()`: reallocate shared memory buffer, create `SKSurface` from pixel pointer with `SKImageInfo`. `Flush()`: `wl_surface_attach(buffer)`, `wl_surface_damage_buffer(0, 0, width, height)`, `wl_surface_commit()`. Double-buffering with frame callbacks to avoid tearing
- [x] T024 [US1] Wire up rendering in `WaylandXamlRootHost` — create render thread (high-priority background thread), instantiate `WaylandSoftwareRenderer`, connect `InvalidateRender()` to trigger render via `AutoResetEvent`, request frame callbacks via `wl_surface_frame()` for vsync-aligned rendering
- [x] T025 [US1] Create `WaylandApplicationViewExtension` in `src/Uno.UI.Runtime.Skia.Wayland/UI/ViewManagement/WaylandApplicationViewExtension.cs` — implement `IApplicationViewExtension`, `TryResizeView()` as no-op returning false (Wayland does not allow client-initiated window positioning/resizing through the application view)
- [x] T026 [US1] Create `WaylandDisplayInformationExtension` in `src/Uno.UI.Runtime.Skia.Wayland/Graphics/Display/WaylandDisplayInformationExtension.cs` — implement `IDisplayInformationExtension`, listen for `wl_output.scale` and `wl_output.geometry` events, provide `RawPixelsPerViewPixel` from the output scale factor
- [x] T027 [US1] Register all US1 extensions in the `WaylandApplicationHost` static constructor — register `INativeWindowFactoryExtension`, `IApplicationViewExtension`, `IDisplayInformationExtension`, `ICoreApplicationExtension`, and reused Linux extensions (`ILauncherExtension` → `LinuxLauncherExtension`, `IFileOpenPickerExtension`/`IFolderPickerExtension` → `LinuxFilePickerExtension`, `IFileSavePickerExtension` → `LinuxFileSaverExtension`, `ISystemThemeHelperExtension` → `LinuxSystemThemeHelper`)
- [x] T028 [US1] Validate: build SamplesApp with `dotnet build src/SamplesApp/SamplesApp.Skia.Generic -p:UnoTargetFrameworkOverride=net10.0 --no-restore`, run under headless Weston, verify a window surface is created and UI renders without crashes

**Checkpoint**: User Story 1 complete — the app launches on Wayland and renders the UI via software rendering. No input handling yet.

---

## Phase 4: User Story 2 — Automatic Platform Detection (Priority: P1)

**Goal**: The app automatically selects Wayland when `WAYLAND_DISPLAY` is set, X11 when only `DISPLAY` is set, with an override mechanism

**Independent Test**: Run the same SamplesApp binary with different env vars: (1) only `WAYLAND_DISPLAY` → Wayland backend, (2) only `DISPLAY` → X11 backend, (3) both set → Wayland preferred, (4) `UNO_PLATFORM_BACKEND=x11` with both set → X11 used

### Implementation for User Story 2

- [x] T029 [US2] Update `WaylandHostBuilder.IsSupported` in `src/Uno.UI.Runtime.Skia.Wayland/Builder/WaylandHostBuilder.cs` — add check for `UNO_PLATFORM_BACKEND` env var: if set to `x11` return false, if set to `wayland` return true, otherwise fall back to `WAYLAND_DISPLAY` check. Also validate the connection actually succeeds by attempting `wl_display_connect(null)` and disconnecting
- [x] T030 [US2] Ensure `UseWayland()` is placed before `UseX11()` in `src/SamplesApp/SamplesApp.Skia.Generic/Program.cs` builder chain so Wayland is tried first when both are available
- [ ] T031 [US2] Validate: test with `WAYLAND_DISPLAY` set → Wayland selected; unset `WAYLAND_DISPLAY`, set `DISPLAY` → X11 selected; set `UNO_PLATFORM_BACKEND=x11` with both env vars → X11 selected

**Checkpoint**: Automatic platform detection works correctly with proper priority and override

---

## Phase 5: User Story 1 (continued) — Input Handling (Priority: P1)

**Goal**: Add pointer and keyboard input so users can interact with the app on Wayland

**Independent Test**: Run SamplesApp on Wayland, click buttons, type in TextBox, scroll with mouse wheel — all interactions respond correctly

### Implementation for User Story 1 (Input)

- [x] T032 [P] [US1] Create `WaylandPointerInputSource` in `src/Uno.UI.Runtime.Skia.Wayland/Devices/Input/WaylandPointerInputSource.cs` — implement `IUnoCorePointerInputSource`. Set up `wl_pointer` listener with handlers for `enter`, `leave`, `motion`, `button`, `axis` events. Translate `wl_pointer.button` (BTN_LEFT=272, BTN_RIGHT=273, BTN_MIDDLE=274) to Uno pointer events. Translate `wl_pointer.axis` (vertical=0, horizontal=1) to `PointerWheelChanged`. Track pointer position and focused surface. Dispatch events to main dispatcher thread via `CoreDispatcher`
- [x] T033 [P] [US1] Create `WaylandKeyboardInputSource` in `src/Uno.UI.Runtime.Skia.Wayland/Devices/Input/WaylandKeyboardInputSource.cs` — implement `IUnoKeyboardInputSource`. Set up `wl_keyboard` listener with handlers for `keymap`, `enter`, `leave`, `key`, `modifiers` events. On `keymap`: create xkb_keymap from the fd using `xkb_keymap_new_from_string()` and `xkb_state_new()`. On `key`: translate via `xkb_state_key_get_one_sym()` → Uno `VirtualKey` (reuse key mapping table from X11 or FrameBuffer). On `modifiers`: update `xkb_state_update_mask()`. Handle key repeat via `repeat_info` event
- [x] T034 [US1] Create VirtualKey mapping in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WaylandKeyTransform.cs` — map xkb keysyms to Uno `VirtualKey` enum values, following the pattern in `src/Uno.UI.Runtime.Skia.X11/X11_Bindings/x11bindings_X11KeyTransform.cs`
- [x] T035 [US1] Register input source extensions in `WaylandApplicationHost` static constructor in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandApplicationHost.cs` — add `ApiExtensibility.Register<IXamlRootHost>(typeof(IUnoCorePointerInputSource), o => new WaylandPointerInputSource(o))` and same for `IUnoKeyboardInputSource`
- [x] T036 [US1] Wire `wl_seat` capability changes to create/destroy pointer and keyboard objects in `WaylandXamlRootHost` — listen for `wl_seat.capabilities` event, create `wl_pointer` when `WL_SEAT_CAPABILITY_POINTER` is present, create `wl_keyboard` when `WL_SEAT_CAPABILITY_KEYBOARD` is present
- [ ] T037 [US1] Validate: run SamplesApp on Wayland, click buttons (pointer events fire), type in a TextBox (keyboard events fire), scroll with mouse wheel (scroll events fire)

**Checkpoint**: Full P1 — the app renders AND accepts input on Wayland

---

## Phase 6: User Story 1 (continued) — EGL GPU Rendering (Priority: P1)

**Goal**: Add GPU-accelerated rendering for smooth 60fps performance

**Independent Test**: Run SamplesApp on Wayland with EGL available, verify smooth rendering and no visual artifacts during resize

### Implementation for User Story 1 (EGL)

- [x] T038 [US1] Create `WaylandEGLRenderer` in `src/Uno.UI.Runtime.Skia.Wayland/Rendering/WaylandEGLRenderer.cs` — create EGL display from `wl_display` via `eglGetDisplay()`, choose EGL config with `EGL_SURFACE_TYPE=EGL_WINDOW_BIT`, create EGL context, create `wl_egl_window` via `wl_egl_window_create()`, create EGL surface from `wl_egl_window`. `UpdateSize()`: `wl_egl_window_resize()`, recreate Skia `GRContext` backed `SKSurface`. `MakeCurrent()`: `eglMakeCurrent()`. `Flush()`: `eglSwapBuffers()`. `Dispose()`: destroy EGL surface, context, terminate display, destroy `wl_egl_window`
- [x] T039 [US1] Create `WaylandNativeOpenGLWrapper` in `src/Uno.UI.Runtime.Skia.Wayland/Graphics/WaylandNativeOpenGLWrapper.cs` — implement `INativeOpenGLWrapper` using EGL, following pattern from `src/Uno.UI.Runtime.Skia.X11/Graphics/X11NativeOpenGLWrapper.cs`. Register via `ApiExtensibility.Register<XamlRoot>(typeof(INativeOpenGLWrapper), ...)`
- [x] T040 [US1] Update renderer selection in `WaylandXamlRootHost` in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandXamlRootHost.cs` — try EGL first, fall back to software renderer if EGL initialization fails. Log which renderer is active
- [ ] T041 [US1] Validate: run SamplesApp, verify GPU rendering is active (check log output), resize window smoothly, confirm no visual artifacts

**Checkpoint**: Full P1 with GPU rendering — app is usable with good performance

---

## Phase 7: User Story 1 (continued) — DPI Scaling (Priority: P1)

**Goal**: Correct rendering at compositor scale factors including fractional scaling

**Independent Test**: Run SamplesApp at 200% scale (Weston `--scale=2`), verify UI renders at correct size with crisp text

### Implementation for User Story 1 (DPI)

- [ ] T042 [P] [US1] Create fractional scale bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WpFractionalScaleBindings.cs` — P/Invoke for `wp_fractional_scale_manager_v1` and `wp_fractional_scale_v1` interfaces, listener for `preferred_scale` event
- [ ] T043 [P] [US1] Create viewporter bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/WpViewporterBindings.cs` — P/Invoke for `wp_viewporter` and `wp_viewport` interfaces, `set_destination` and `set_source` requests
- [ ] T044 [US1] Update `WaylandXamlRootHost` and `WaylandWindowWrapper` in their respective files — bind `wp_fractional_scale_manager_v1` from registry if available, attach `wp_fractional_scale_v1` to each surface, listen for `preferred_scale` event. Apply scale to buffer size (render at `width * scale × height * scale`), set `wp_viewport` destination to logical size. Fall back to `wl_output.scale` (integer) if fractional scale protocol unavailable. Update `RasterizationScale` on `WaylandWindowWrapper` when scale changes
- [ ] T045 [US1] Update `WaylandDisplayInformationExtension` in `src/Uno.UI.Runtime.Skia.Wayland/Graphics/Display/WaylandDisplayInformationExtension.cs` — incorporate fractional scale into `RawPixelsPerViewPixel` when available
- [ ] T046 [US1] Validate: run with Weston `--scale=2`, verify text is crisp and UI is at correct size. Test with fractional scale if compositor supports it

**Checkpoint**: Complete User Story 1 — app renders correctly at all scale factors with GPU acceleration and full input

---

## Phase 8: User Story 3 — Clipboard Support (Priority: P2)

**Goal**: Copy/paste text and data between the Uno app and other Wayland applications

**Independent Test**: Copy text from a terminal, paste into Uno TextBox. Copy from Uno TextBox, paste into terminal. Both directions work.

### Implementation for User Story 3

- [ ] T047 [US3] Create `WaylandClipboardExtension` in `src/Uno.UI.Runtime.Skia.Wayland/ApplicationModel/DataTransfer/WaylandClipboardExtension.cs` — implement `IClipboardExtension`. **Set clipboard**: create `wl_data_source` with offered MIME types (`text/plain`, `text/plain;charset=utf-8`), set on `wl_data_device` via `set_selection()`. Handle `wl_data_source.send` event by writing data to the provided fd. **Get clipboard**: listen for `wl_data_device.selection` event providing a `wl_data_offer`, enumerate MIME types via `wl_data_offer.offer` events, on paste request `wl_data_offer.receive()` with a pipe, read data from pipe fd. Handle async nature with `TaskCompletionSource`
- [ ] T048 [US3] Register clipboard extension in `WaylandApplicationHost` static constructor — `ApiExtensibility.Register(typeof(IClipboardExtension), _ => WaylandClipboardExtension.Instance)`
- [ ] T049 [US3] Validate: run SamplesApp, copy text from another app, paste into TextBox; copy from TextBox, paste into another app

**Checkpoint**: Clipboard works between Uno app and native Wayland apps

---

## Phase 9: User Story 4 — Multi-Window Support (Priority: P2)

**Goal**: Support multiple independent Wayland toplevel windows from a single Uno app

**Independent Test**: Open a second window from SamplesApp, verify both windows render and accept input independently, close one without affecting the other

### Implementation for User Story 4

- [ ] T050 [US4] Update `WaylandXamlRootHost` in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandXamlRootHost.cs` — ensure each new `WaylandWindowWrapper` creates its own `wl_surface` + `xdg_surface` + `xdg_toplevel`, with independent renderer instance and frame callback. Track all hosts in the static `ConcurrentDictionary`. Route input events to the correct host based on `wl_pointer.enter`/`wl_keyboard.enter` surface matching
- [ ] T051 [US4] Update `WaylandWindowWrapper.CloseCore()` in `src/Uno.UI.Runtime.Skia.Wayland/UI/Xaml/Window/WaylandWindowWrapper.cs` — destroy only this window's Wayland objects, remove from host tracking dictionary, fire `Closing` event with cancellation support
- [ ] T052 [US4] Validate: open multiple windows in SamplesApp, interact with each independently, close one and verify the other continues working

**Checkpoint**: Multi-window works correctly

---

## Phase 10: User Story 6 — Cursor Management (Priority: P2)

**Goal**: Display correct pointer cursors (hand, IBeam, resize, etc.) when hovering over interactive elements

**Independent Test**: Hover over a Button (hand cursor), TextBox (IBeam cursor), window edge (resize cursor) — verify cursor changes appropriately

### Implementation for User Story 6

- [ ] T053 [P] [US6] Create cursor shape bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/CursorShapeBindings.cs` — P/Invoke for `wp_cursor_shape_manager_v1` and `wp_cursor_shape_device_v1`, `set_shape()` request with shape enum values (default, text, pointer, grab, etc.)
- [ ] T054 [US6] Update `WaylandPointerInputSource.PointerCursor` setter in `src/Uno.UI.Runtime.Skia.Wayland/Devices/Input/WaylandPointerInputSource.cs` — map Uno `CoreCursorType` to `wp_cursor_shape_device_v1` shape values. If `cursor-shape-v1` protocol unavailable, fall back to loading cursor images from `wl_cursor_theme` (via `libwayland-cursor.so` P/Invoke) and setting cursor surface manually via `wl_pointer_set_cursor()`
- [ ] T055 [US6] Validate: hover over interactive elements in SamplesApp, verify cursor shape changes

**Checkpoint**: Cursor management works

---

## Phase 11: User Story 5 — Drag and Drop (Priority: P3)

**Goal**: Drag files from file manager into Uno app, and drag from Uno app to other apps

**Independent Test**: Drag a file from Nautilus/Dolphin onto a drop target in SamplesApp, verify the app receives the file data

### Implementation for User Story 5

- [ ] T056 [US5] Create `WaylandDragDropExtension` in `src/Uno.UI.Runtime.Skia.Wayland/ApplicationModel/DataTransfer/DragDrop/WaylandDragDropExtension.cs` — implement `IDragDropExtension`. Handle `wl_data_device.enter`, `wl_data_device.motion`, `wl_data_device.leave`, `wl_data_device.drop` events for incoming drags. For outgoing drags: create `wl_data_source`, call `wl_data_device.start_drag()` with source surface and serial. Map `wl_data_offer` MIME types to `DataPackage` formats
- [ ] T057 [US5] Register drag-drop extension in `WaylandApplicationHost` static constructor — `ApiExtensibility.Register<DragDropManager>(typeof(IDragDropExtension), o => new WaylandDragDropExtension(o))`
- [ ] T058 [US5] Validate: drag a file onto SamplesApp drop target, verify data is received

**Checkpoint**: Drag and drop works for incoming and outgoing operations

---

## Phase 12: User Story 7 — Touch and Stylus Input (Priority: P3)

**Goal**: Support touch and stylus input on Wayland for convertible/tablet devices

**Independent Test**: On a touch-enabled device, tap buttons, scroll with gestures, verify touch input works

### Implementation for User Story 7

- [ ] T059 [US7] Create `WaylandTouchInputSource` integration in `src/Uno.UI.Runtime.Skia.Wayland/Devices/Input/WaylandTouchInputSource.cs` — set up `wl_touch` listener with handlers for `down`, `up`, `motion`, `cancel`, `frame` events. Map touch events to Uno pointer events with `PointerDeviceType.Touch`. Track active touch points by id. Dispatch `PointerPressed` on `down`, `PointerMoved` on `motion`, `PointerReleased` on `up`, `PointerCancelled` on `cancel`
- [ ] T060 [US7] Update `WaylandXamlRootHost` seat capability handling in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandXamlRootHost.cs` — create `wl_touch` when `WL_SEAT_CAPABILITY_TOUCH` is present, wire to touch input source
- [ ] T061 [US7] Validate: on touch-enabled device, tap buttons and scroll — verify touch input works

**Checkpoint**: Touch input works

---

## Phase 13: User Story 1 (continued) — Window Decorations

**Goal**: Support server-side and client-side window decorations

**Independent Test**: Run on GNOME (CSD default), KDE (SSD capable), Sway (SSD via protocol) — verify windows have proper decorations

### Implementation for Decorations

- [ ] T062 [P] [US1] Create xdg-decoration bindings in `src/Uno.UI.Runtime.Skia.Wayland/Wayland_Bindings/XdgDecorationBindings.cs` — P/Invoke for `zxdg_decoration_manager_v1` and `zxdg_toplevel_decoration_v1`, listener for `configure` event (server_side/client_side mode)
- [ ] T063 [US1] Update `WaylandWindowWrapper` in `src/Uno.UI.Runtime.Skia.Wayland/UI/Xaml/Window/WaylandWindowWrapper.cs` — if `zxdg_decoration_manager_v1` is available, request server-side decorations via `set_mode(server_side)`. Handle `configure` callback to know which mode the compositor chose. If protocol unavailable, assume CSD. Implement `ExtendContentIntoTitleBar()` to request/remove CSD
- [ ] T064 [US1] Validate: run on compositor with decoration protocol support, verify SSD is used; run on compositor without, verify CSD fallback (no title bar but functional)

**Checkpoint**: Window decorations work across compositors

---

## Phase 14: Polish & Cross-Cutting Concerns

**Purpose**: Final integration, error handling, and cross-story improvements

- [ ] T065 Handle Wayland display disconnect gracefully in `src/Uno.UI.Runtime.Skia.Wayland/Hosting/WaylandXamlRootHost.cs` — detect `wl_display_dispatch()` returning -1 (connection lost), log error, exit cleanly
- [ ] T066 Add logging throughout the Wayland target — use `Uno.Foundation.Logging` to log compositor capabilities, renderer selection, protocol negotiations, and errors. Add at minimum to `WaylandApplicationHost`, `WaylandXamlRootHost`, renderer classes
- [ ] T067 Ensure all `IDisposable` implementations properly clean up Wayland protocol objects — audit all classes with `IntPtr` handles for proper cleanup in `Dispose()` and finalizers
- [ ] T068 Update existing X11 unit tests to verify no regressions — run `dotnet test src/Uno.UI/Uno.UI.Tests.csproj` and ensure all tests pass
- [ ] T069 Validate full end-to-end: build and run SamplesApp on headless Weston, verify rendering, input, clipboard, cursor changes, and window lifecycle all work without errors

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **US1 Rendering (Phase 3)**: Depends on Foundational
- **US2 Detection (Phase 4)**: Depends on Foundational only (can run in parallel with Phase 3)
- **US1 Input (Phase 5)**: Depends on Phase 3 (needs window/rendering to test input)
- **US1 EGL (Phase 6)**: Depends on Phase 3 (needs software renderer working first)
- **US1 DPI (Phase 7)**: Depends on Phase 6 (needs renderer working to test scaling)
- **US3 Clipboard (Phase 8)**: Depends on Phase 5 (needs input to test paste)
- **US4 Multi-Window (Phase 9)**: Depends on Phase 5 (needs input per window)
- **US6 Cursors (Phase 10)**: Depends on Phase 5 (needs pointer input)
- **US5 DnD (Phase 11)**: Depends on Phase 5 (needs input events)
- **US7 Touch (Phase 12)**: Depends on Phase 5 (needs input infrastructure)
- **US1 Decorations (Phase 13)**: Depends on Phase 3 (needs window)
- **Polish (Phase 14)**: Depends on all desired phases being complete

### User Story Dependencies

- **US1 (P1)**: Foundation → Rendering → Input → EGL → DPI → Decorations (sequential build-up)
- **US2 (P1)**: Foundation only (independent of US1 implementation)
- **US3 (P2)**: Foundation + US1 Input (needs working app to test clipboard)
- **US4 (P2)**: Foundation + US1 Input (needs working single-window first)
- **US5 (P3)**: Foundation + US1 Input (needs input infrastructure)
- **US6 (P2)**: Foundation + US1 Input (needs pointer events)
- **US7 (P3)**: Foundation + US1 Input (needs input infrastructure)

### Parallel Opportunities

**Phase 1 parallel group** (all independent files):
- T002, T003, T004, T005, T006, T007 — all binding files can be written in parallel

**Phase 5 parallel group**:
- T032, T033 — pointer and keyboard input sources (different files, no dependencies)

**Phase 7 parallel group**:
- T042, T043 — fractional scale and viewporter bindings (different files)

**After Phase 2, US2 can run in parallel with US1 phases**

**After Phase 5 (US1 Input), US3/US4/US5/US6/US7 can all run in parallel**

---

## Parallel Example: Phase 1 (Setup)

```
# Launch all binding files in parallel:
Task T002: "Create core Wayland P/Invoke bindings in Wayland_Bindings/WaylandBindings.cs"
Task T003: "Create Wayland struct definitions in Wayland_Bindings/WaylandStructs.cs"
Task T004: "Create Wayland enum definitions in Wayland_Bindings/WaylandEnums.cs"
Task T005: "Create xdg-shell bindings in Wayland_Bindings/XdgShellBindings.cs"
Task T006: "Create EGL bindings in Wayland_Bindings/EglBindings.cs"
Task T007: "Create xkbcommon bindings in Wayland_Bindings/XkbCommonBindings.cs"
```

## Parallel Example: After Phase 5 (Input complete)

```
# These stories can all proceed in parallel:
Phase 8:  US3 - Clipboard (T047-T049)
Phase 9:  US4 - Multi-Window (T050-T052)
Phase 10: US6 - Cursors (T053-T055)
Phase 11: US5 - Drag and Drop (T056-T058)
Phase 12: US7 - Touch Input (T059-T061)
Phase 13: US1 - Decorations (T062-T064)
```

---

## Implementation Strategy

### MVP First (User Story 1 — Phases 1-7)

1. Complete Phase 1: Setup (project, bindings, build integration)
2. Complete Phase 2: Foundational (host builder, app host, display connection)
3. Complete Phase 3: Rendering (software renderer, window shows up)
4. **STOP and VALIDATE**: Window appears on Weston, UI renders
5. Complete Phase 5: Input (pointer + keyboard — app is interactive)
6. **STOP and VALIDATE**: Can click buttons and type text
7. Complete Phase 6: EGL (GPU rendering — smooth performance)
8. Complete Phase 7: DPI scaling (correct at all scale factors)
9. **MVP COMPLETE**: App is fully usable on Wayland

### Incremental Delivery

1. Phases 1-3 → Window renders → First visual proof
2. Phase 5 → Input works → App is interactive (critical milestone)
3. Phase 6 → GPU rendering → Performance is good
4. Phase 7 → DPI correct → Looks right on all displays
5. Phase 4 → Auto-detection → Seamless user experience
6. Phases 8-13 → Platform features → Full desktop integration
7. Phase 14 → Polish → Production-ready

### Parallel Team Strategy

With multiple developers after Phase 5 is complete:
- Developer A: US3 Clipboard + US5 DnD (data transfer focus)
- Developer B: US4 Multi-Window + US1 Decorations (window management focus)
- Developer C: US6 Cursors + US7 Touch (input focus)

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Build with `dotnet build src/SamplesApp/SamplesApp.Skia.Generic -p:UnoTargetFrameworkOverride=net10.0` after each phase
- Run with headless Weston for CI: `weston --backend=headless &` then set `WAYLAND_DISPLAY`
- Never cancel builds — set 15+ minute timeouts
