# Data Model: Native Wayland Target

**Branch**: `004-wayland-target` | **Date**: 2026-04-05

## Entities

### WaylandDisplay

Represents the connection to the Wayland compositor. One per application lifetime.

- **Connection**: `wl_display*` — the core Wayland connection handle
- **Registry**: `wl_registry*` — global object registry for protocol discovery
- **File Descriptor**: `int` — the Wayland socket fd, used for `poll()` in the event loop
- **Globals**: Dictionary mapping interface names to bound global objects (compositor, shm, seat, etc.)

**Lifecycle**: Created at application startup, destroyed at application exit. The connection is thread-safe for `wl_display_flush()` but `wl_display_dispatch()` must be called from a single thread.

### WaylandWindow

Represents a single application window as a Wayland surface with xdg-shell role.

- **Surface**: `wl_surface*` — the core Wayland surface
- **XdgSurface**: `xdg_surface*` — the xdg-shell surface wrapper
- **XdgToplevel**: `xdg_toplevel*` — the toplevel (window) role
- **EglWindow**: `wl_egl_window*` — the EGL native window (for GPU rendering), or null for software
- **ShmPool**: `wl_shm_pool*` — shared memory pool (for software rendering), or null for EGL
- **Size**: `(int width, int height)` — current surface size in surface-local coordinates
- **Scale**: `float` — current scale factor (integer from `wl_output.scale` or fractional from `wp_fractional_scale_v1`)
- **BufferSize**: `(int width, int height)` — size in buffer (pixel) coordinates = Size × Scale
- **Configured**: `bool` — whether the initial `xdg_surface.configure` has been acknowledged
- **Title**: `string` — window title (set via `xdg_toplevel.set_title`)
- **Decorations**: `enum { ServerSide, ClientSide }` — active decoration mode

**Lifecycle**: Created when `INativeWindowFactoryExtension.CreateWindow()` is called. Destroyed when the window is closed. Must acknowledge `xdg_surface.configure` before the first commit.

**Relationships**: Belongs to one WaylandDisplay. Has one WaylandRenderer. Has one WaylandPointerInputSource and one WaylandKeyboardInputSource (via the seat).

### WaylandSeat

Represents the user's input devices (pointer, keyboard, touch).

- **Seat**: `wl_seat*` — the seat global
- **Pointer**: `wl_pointer*` — pointer device (or null if no pointer capability)
- **Keyboard**: `wl_keyboard*` — keyboard device (or null)
- **Touch**: `wl_touch*` — touch device (or null)
- **Capabilities**: `flags { Pointer, Keyboard, Touch }` — current device capabilities
- **FocusedSurface**: `wl_surface*` — the surface currently receiving input (pointer or keyboard focus)
- **PointerPosition**: `(double x, double y)` — last known pointer position in surface-local coordinates
- **KeyboardState**: xkb_state handle for keymap processing

**Lifecycle**: Created when `wl_seat` is bound from the registry. Device sub-objects are created/destroyed as capabilities change (e.g., a mouse is plugged in or removed).

**Relationships**: Belongs to one WaylandDisplay. Can deliver events to any WaylandWindow that has focus.

### WaylandOutput

Represents a physical display/monitor.

- **Output**: `wl_output*` — the output global
- **Scale**: `int` — integer scale factor
- **FractionalScale**: `float` — fractional scale (if `wp_fractional_scale_v1` is available)
- **Size**: `(int width, int height)` — physical size in mm
- **Resolution**: `(int width, int height)` — resolution in pixels
- **RefreshRate**: `int` — refresh rate in mHz
- **Name**: `string` — output name (from `wl_output.name`, protocol version 4+)

**Lifecycle**: Created when `wl_output` is bound from the registry. Updated on `wl_output.geometry`, `wl_output.mode`, and `wl_output.scale` events. Destroyed when the output is removed.

### WaylandDataDevice

Represents the clipboard and drag-and-drop state for a seat.

- **DataDevice**: `wl_data_device*` — the data device for the seat
- **SelectionOffer**: `wl_data_offer*` — the current clipboard offer (what other apps have copied)
- **DndOffer**: `wl_data_offer*` — the current drag-and-drop offer
- **CurrentSource**: `wl_data_source*` — the data source when this app is the clipboard owner

**Lifecycle**: Created when `wl_data_device_manager` is bound and a seat exists. Updated via `data_device.data_offer`, `data_device.selection`, `data_device.enter/leave/motion/drop` events.

**Relationships**: Belongs to one WaylandSeat (one data device per seat).

## State Transitions

### Window Lifecycle

```
[Created] → configure(width, height, states) → [Configured] → map/show → [Visible]
    ↕ configure events (resize, state changes)
[Visible] → close → [Closing] → destroy → [Destroyed]
```

### Surface Commit Flow

```
[Idle] → render requested → [Rendering]
    → attach buffer → set damage → commit → [Committed]
    → frame callback → [Idle]
```

### Clipboard Ownership

```
[Empty] → app copies data → create wl_data_source → set_selection → [Owner]
[Owner] → another app copies → selection event with new offer → [Consumer]
[Consumer] → app copies again → set_selection → [Owner]
[Owner/Consumer] → send request received → write data to fd → [same state]
```
