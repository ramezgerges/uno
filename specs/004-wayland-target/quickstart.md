# Quickstart: Wayland Target Development

**Branch**: `004-wayland-target` | **Date**: 2026-04-05

## Prerequisites

```bash
# System packages (Ubuntu/Debian)
sudo apt-get install libwayland-dev libwayland-client0 libxkbcommon-dev \
    libegl-dev libgles2-mesa-dev weston

# .NET 10 SDK must be installed
dotnet --version  # Should be 10.0.x
```

## Build Setup

### 1. Configure cross-targeting

```bash
cd /workspace/uno/src
cp crosstargeting_override.props.sample crosstargeting_override.props
```

Edit `crosstargeting_override.props`:
```xml
<Project>
  <PropertyGroup>
    <UnoTargetFrameworkOverride>net10.0</UnoTargetFrameworkOverride>
  </PropertyGroup>
</Project>
```

### 2. Add UseWayland() to SamplesApp

Edit `src/SamplesApp/SamplesApp.Skia.Generic/Program.cs`, add `.UseWayland()` to the builder chain:

```csharp
var builder = UnoPlatformHostBuilder.Create()
    .App(() => _app = new SamplesApp.App())
    .UseWayland()       // ← Add this line
    .UseX11(hostBuilder => hostBuilder.PreloadMediaPlayer(true))
    // ... rest of chain
```

**Important**: Place `.UseWayland()` before `.UseX11()` so Wayland is preferred when both are available.

### 3. Build

```bash
cd /workspace/uno/src

# Restore
dotnet restore Uno.UI-Skia-only.slnf

# Build the Wayland runtime project
dotnet build Uno.UI.Runtime.Skia.Wayland/Uno.UI.Runtime.Skia.Wayland.csproj --no-restore

# Build SamplesApp
dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
    -p:UnoTargetFrameworkOverride=net10.0 --no-restore
```

**Build timeout**: Set to 15+ minutes. Never cancel builds.

### 4. Run with Headless Weston (for testing without display)

```bash
# Terminal 1: Start headless Weston
weston --backend=headless &

# Terminal 2: Run the app
export WAYLAND_DISPLAY=wayland-1
cd /workspace/uno/src
dotnet run --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
    -p:UnoTargetFrameworkOverride=net10.0
```

### 5. Run on a real Wayland session

If you have a Wayland desktop session (GNOME, KDE Plasma, Sway):

```bash
# WAYLAND_DISPLAY is already set by the compositor
cd /workspace/uno/src
dotnet run --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
    -p:UnoTargetFrameworkOverride=net10.0
```

## Project Structure

New project location: `src/Uno.UI.Runtime.Skia.Wayland/`

```
src/Uno.UI.Runtime.Skia.Wayland/
├── Uno.UI.Runtime.Skia.Wayland.csproj
├── Builder/
│   ├── WaylandHostBuilder.cs          # IPlatformHostBuilder implementation
│   └── HostBuilder.cs                 # UseWayland() extension method
├── Hosting/
│   ├── WaylandApplicationHost.cs      # SkiaHost + ApiExtensibility registrations
│   └── WaylandXamlRootHost.cs         # Per-window host
├── UI/Xaml/Window/
│   ├── WaylandWindowWrapper.cs        # INativeWindowWrapper
│   ├── WaylandWindow.cs               # Native window struct
│   └── WaylandNativeWindowFactoryExtension.cs
├── Rendering/
│   ├── WaylandRenderer.cs             # Abstract base
│   ├── WaylandEGLRenderer.cs          # GPU rendering
│   └── WaylandSoftwareRenderer.cs     # CPU fallback
├── Devices/Input/
│   ├── WaylandPointerInputSource.cs   # Pointer events
│   ├── WaylandKeyboardInputSource.cs  # Keyboard events + xkbcommon
│   └── WaylandTouchInputSource.cs     # Touch events
├── ApplicationModel/
│   ├── Core/WaylandCoreApplicationExtension.cs
│   └── DataTransfer/
│       ├── WaylandClipboardExtension.cs
│       └── DragDrop/WaylandDragDropExtension.cs
├── Graphics/
│   ├── Display/WaylandDisplayInformationExtension.cs
│   └── WaylandNativeOpenGLWrapper.cs
├── UI/ViewManagement/
│   └── WaylandApplicationViewExtension.cs
└── Wayland_Bindings/
    ├── WaylandBindings.cs             # P/Invoke to libwayland-client
    ├── WaylandStructs.cs              # wl_message, wl_interface, etc.
    ├── WaylandEnums.cs                # Format, ShmFormat, etc.
    ├── XdgShellBindings.cs            # xdg_wm_base, xdg_surface, xdg_toplevel
    ├── XdgDecorationBindings.cs       # zxdg_decoration_manager_v1
    ├── WpFractionalScaleBindings.cs   # wp_fractional_scale_v1
    ├── WpViewporterBindings.cs        # wp_viewporter
    ├── CursorShapeBindings.cs         # wp_cursor_shape_v1
    ├── EglBindings.cs                 # EGL P/Invoke (eglGetDisplay, etc.)
    └── XkbCommonBindings.cs           # xkbcommon P/Invoke
```

## Iterative Development Order

Follow this order, testing after each step:

1. **Minimal window** — WaylandHostBuilder + WaylandApplicationHost + WaylandXamlRootHost + WaylandWindow + software renderer → window appears
2. **Input** — Pointer + keyboard → can interact with the app
3. **EGL rendering** — GPU-accelerated rendering → smooth 60fps
4. **DPI scaling** — wl_output.scale + fractional scaling → correct HiDPI
5. **Clipboard** — wl_data_device copy/paste → can paste text
6. **Cursors** — cursor-shape-v1 or wl_cursor → proper cursor feedback
7. **Multi-window** — multiple xdg_toplevel surfaces → multiple windows
8. **Decorations** — xdg-decoration + CSD fallback → window chrome
9. **Touch** — wl_touch → touch input
10. **Drag-and-drop** — wl_data_device DnD → drag files into app

## Validation

After each step:

```bash
# Build
dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
    -p:UnoTargetFrameworkOverride=net10.0 --no-restore

# Run and verify no crashes/exceptions
dotnet run --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj \
    -p:UnoTargetFrameworkOverride=net10.0

# Unit tests (should still pass)
dotnet test Uno.UI/Uno.UI.Tests.csproj --no-build
```
