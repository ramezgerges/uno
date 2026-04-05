# Feature Specification: Native Wayland Target for Uno Platform

**Feature Branch**: `004-wayland-target`
**Created**: 2026-04-05
**Status**: Draft
**Input**: User description: "The goal is to add a new Uno Platform target for Wayland. We currently support X11 on Linux and Wayland through XWayland, but we want a new target to support Wayland properly"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Run Uno App on Native Wayland (Priority: P1)

A developer building an Uno Platform application on Linux wants to run their app directly on a Wayland compositor (GNOME, KDE Plasma, Sway, etc.) without relying on XWayland. The app should launch, render correctly, accept pointer and keyboard input, and behave identically to the X11 target in terms of UI fidelity.

**Why this priority**: This is the core value proposition. Without basic window creation, rendering, and input, no other Wayland features matter. Most modern Linux distributions default to Wayland sessions, and XWayland introduces latency, scaling artifacts, and missing features (e.g., per-monitor DPI, fractional scaling).

**Independent Test**: Can be fully tested by launching a sample Uno app under a Wayland compositor and interacting with it (clicking buttons, typing text, resizing the window). Delivers immediate value for all Linux Wayland users.

**Acceptance Scenarios**:

1. **Given** a developer has an existing Uno Platform Skia app targeting Linux, **When** they run the app on a Wayland session, **Then** the app launches in a native Wayland window (not through XWayland) and renders the UI correctly.
2. **Given** a running Uno app on Wayland, **When** the user clicks, scrolls, or types, **Then** the app responds to input identically to the X11 target.
3. **Given** a running Uno app on Wayland, **When** the user resizes the window, **Then** the app re-renders at the new size without visual artifacts or lag.
4. **Given** a Wayland session with a specific display scale (e.g., 150%), **When** the app launches, **Then** the UI renders at the correct DPI with crisp text and graphics, respecting the compositor's scale factor.

---

### User Story 2 - Automatic Platform Detection (Priority: P1)

A developer wants their Uno app to automatically select the best available display server. If running under a Wayland session, the app should use native Wayland. If running under X11, it should use X11. The developer should not need to change code or configuration.

**Why this priority**: Without automatic detection, developers would need manual configuration, reducing adoption and creating a poor developer experience. This is essential for the feature to be practical.

**Independent Test**: Can be tested by launching the same app binary under both a Wayland session and an X11 session and verifying the correct backend is selected each time.

**Acceptance Scenarios**:

1. **Given** a user launches an Uno app under a Wayland session (WAYLAND_DISPLAY is set), **When** the app starts, **Then** the Wayland backend is selected automatically.
2. **Given** a user launches an Uno app under an X11-only session (DISPLAY is set, WAYLAND_DISPLAY is not), **When** the app starts, **Then** the X11 backend is selected.
3. **Given** a user explicitly overrides the backend selection (e.g., via an environment variable or API call), **When** the app starts, **Then** the specified backend is used regardless of session type.
4. **Given** both WAYLAND_DISPLAY and DISPLAY are set, **When** the app starts, **Then** the Wayland backend is preferred unless overridden.

---

### User Story 3 - Clipboard Support (Priority: P2)

A user wants to copy and paste text and other data between an Uno app running on Wayland and other applications.

**Why this priority**: Clipboard is a fundamental desktop interaction. Without it, users cannot integrate the app into their normal workflow. However, P1 covers the minimum viable running app.

**Independent Test**: Can be tested by copying text from another app (e.g., a terminal), pasting it into a TextBox in the Uno app, and vice versa.

**Acceptance Scenarios**:

1. **Given** a user has copied text to the system clipboard from another application, **When** they paste into an Uno TextBox, **Then** the pasted text appears correctly.
2. **Given** a user selects and copies text from an Uno TextBox, **When** they paste into another application, **Then** the text appears correctly.
3. **Given** a user copies rich content (e.g., an image or HTML), **When** they paste into an Uno app that supports that content type, **Then** the content is transferred correctly.

---

### User Story 4 - Multi-Window Support (Priority: P2)

A developer creates an Uno app that opens multiple windows. Each window should be a separate Wayland toplevel surface with independent rendering and input.

**Why this priority**: Multi-window is a common desktop app pattern. The Uno framework already supports multiple windows, and the Wayland target must honor this contract.

**Independent Test**: Can be tested by opening a second window from an Uno app and verifying both windows render and respond to input independently.

**Acceptance Scenarios**:

1. **Given** an Uno app requests a new window, **When** the window is created, **Then** a new Wayland toplevel surface appears managed by the compositor.
2. **Given** multiple Uno windows are open, **When** the user interacts with one, **Then** only that window receives the input events.
3. **Given** multiple Uno windows are open, **When** one window is closed, **Then** the other windows continue to function normally.

---

### User Story 5 - Drag and Drop (Priority: P3)

A user wants to drag files or content from the system file manager or another application into an Uno app, and vice versa.

**Why this priority**: Drag-and-drop is important for desktop integration but is not required for initial viability.

**Independent Test**: Can be tested by dragging a file from the file manager onto an Uno drop target and verifying the app receives the file data.

**Acceptance Scenarios**:

1. **Given** a user drags a file from the file manager, **When** they drop it onto an Uno app area that accepts drops, **Then** the app receives the file path/data.
2. **Given** an Uno app initiates a drag operation, **When** the user drops onto another application that accepts drops, **Then** the data is transferred correctly.

---

### User Story 6 - Cursor Management (Priority: P2)

A developer sets custom cursors in their Uno app (e.g., hand cursor on buttons, resize cursors on edges). These cursors should display correctly under Wayland.

**Why this priority**: Cursor feedback is essential for a polished desktop experience. Wayland handles cursors differently from X11 (client-side cursors via wl_cursor or cursor-shape protocol).

**Independent Test**: Can be tested by hovering over various interactive elements and verifying the cursor shape changes appropriately.

**Acceptance Scenarios**:

1. **Given** an Uno app sets the pointer cursor to a specific type (e.g., Hand, IBeam, SizeWE), **When** the cursor is over that element, **Then** the correct cursor shape is displayed.
2. **Given** the Wayland compositor supports the cursor-shape-v1 protocol, **When** the app requests a standard cursor, **Then** the compositor's native cursor theme is used.

---

### User Story 7 - Touch and Stylus Input (Priority: P3)

A user with a touchscreen or stylus device wants to interact with an Uno app using touch gestures or pen input on Wayland.

**Why this priority**: Touch and stylus are important for convertible laptops and tablets running Linux, but represent a smaller user base than pointer and keyboard.

**Independent Test**: Can be tested on a touch-enabled device by tapping, scrolling with gestures, and using a stylus.

**Acceptance Scenarios**:

1. **Given** a user taps on a button with a touchscreen, **When** the tap occurs, **Then** the app processes it as a pointer press/release.
2. **Given** a user uses two-finger scroll on a touchscreen, **When** the gesture occurs, **Then** the app processes it as a scroll/wheel event.
3. **Given** a user uses a stylus with pressure sensitivity, **When** the stylus contacts the screen, **Then** the app receives pressure data through the pointer event system.

---

### Edge Cases

- What happens when the Wayland compositor crashes or the connection is lost? The app should handle the disconnect gracefully and either attempt to recover or exit cleanly with an appropriate error.
- What happens when the user switches between Wayland and X11 sessions while the app is running via XWayland? The app should continue using the backend it was initialized with for its lifetime.
- What happens when a Wayland compositor does not support an optional protocol (e.g., xdg-decoration for server-side decorations)? The app should fall back to client-side decorations or a reasonable default.
- What happens on Wayland compositors with fractional scaling (e.g., 125%, 175%)? The app should render at the correct fractional scale if the wp-fractional-scale-v1 protocol is available, otherwise fall back to integer scaling.
- What happens when the user runs the app under a minimal Wayland compositor that only implements core protocols? The app should still function for basic rendering and input, with graceful degradation for missing optional protocols.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST create and manage native Wayland surfaces (via xdg-shell) for application windows, without relying on XWayland.
- **FR-002**: System MUST render the Uno visual tree onto Wayland surfaces using the Skia rendering engine, supporting both GPU-accelerated (EGL/OpenGL, Vulkan) and software rendering paths.
- **FR-003**: System MUST process Wayland pointer events (motion, button, scroll, enter, leave) and translate them into the Uno pointer input model.
- **FR-004**: System MUST process Wayland keyboard events (key press, release, modifiers) and translate them into the Uno keyboard input model, using xkbcommon for keymap handling.
- **FR-005**: System MUST detect the active display server at startup and automatically select the Wayland backend when a Wayland session is available, with fallback to X11.
- **FR-006**: System MUST support reading from and writing to the system clipboard via the Wayland data-device protocol (wl_data_device_manager).
- **FR-007**: System MUST support drag-and-drop operations using the Wayland data-device protocol.
- **FR-008**: System MUST support multiple application windows, each as an independent xdg_toplevel surface.
- **FR-009**: System MUST respect the compositor's output scale factor and support high-DPI rendering, including fractional scaling via wp-fractional-scale-v1 when available.
- **FR-010**: System MUST display appropriate pointer cursors using either the cursor-shape-v1 protocol or client-side cursor surfaces (wp_cursor_image / wl_cursor).
- **FR-011**: System MUST support window decorations, preferring server-side decorations via xdg-decoration-unstable-v1 when available, with a client-side decoration fallback.
- **FR-012**: System MUST support touch input via wl_touch events and map them to the Uno pointer input model.
- **FR-013**: System MUST support window lifecycle events (configure, close, minimize, maximize, fullscreen) through the xdg-shell protocol.
- **FR-014**: System MUST allow developers to explicitly override the backend selection via an environment variable or programmatic API.
- **FR-015**: System MUST provide file picker dialogs on Wayland, either via the xdg-desktop-portal or an equivalent mechanism.

### Key Entities

- **Wayland Application Host**: The top-level host that manages the Wayland connection (wl_display), global registry, and application lifecycle. Analogous to the existing X11ApplicationHost.
- **Wayland XamlRoot Host**: Per-window host that owns a Wayland surface and its associated xdg_toplevel, handles rendering, and routes input events. Analogous to X11XamlRootHost.
- **Wayland Window Wrapper**: The native window abstraction (INativeWindowWrapper) backed by a Wayland wl_surface + xdg_surface + xdg_toplevel.
- **Wayland Input Sources**: Pointer, keyboard, and touch input handlers that translate Wayland protocol events into Uno input events.
- **Wayland Renderer**: The rendering backend that draws Skia content onto Wayland buffer surfaces, supporting EGL/OpenGL and software (wl_shm) paths.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: An Uno Platform sample app launches and renders correctly on at least three major Wayland compositors (GNOME/Mutter, KDE/KWin, wlroots-based such as Sway) without XWayland.
- **SC-002**: All existing Uno Platform runtime tests that pass on the X11 target also pass on the Wayland target, with exceptions only for X11-specific functionality.
- **SC-003**: User input (pointer, keyboard, scroll) latency on Wayland is equal to or better than the same app running through XWayland.
- **SC-004**: The app correctly renders at fractional display scales (e.g., 125%, 150%, 175%) with no blurriness or scaling artifacts, matching or exceeding XWayland rendering quality.
- **SC-005**: Clipboard copy/paste operations between the Uno app and native Wayland applications complete successfully for text and common data formats.
- **SC-006**: Automatic platform detection correctly selects the Wayland backend on Wayland sessions and the X11 backend on X11 sessions, with no developer configuration required.
- **SC-007**: The Wayland target introduces no regressions to the existing X11 target functionality.

## Assumptions

- The Wayland C libraries (libwayland-client) and xkbcommon are available on the target system. These are standard on all modern Linux distributions with Wayland support.
- The Wayland compositor implements at minimum: wl_compositor, wl_shm, xdg_wm_base (xdg-shell), wl_seat, wl_output. These are the core stable protocols present in all production compositors.
- P/Invoke or a managed Wayland binding will be used to interact with libwayland-client, following the same pattern as the X11 target's use of P/Invoke for Xlib.
- The existing Skia rendering pipeline (SkiaSharp, Composition layer) is reusable; only the surface/buffer presentation layer needs to be Wayland-specific.
- File picker functionality will use xdg-desktop-portal (D-Bus), which is the standard mechanism on Wayland since there is no direct window embedding like X11.
- Client-side decorations (CSD) will be needed as a fallback since not all compositors support xdg-decoration.
