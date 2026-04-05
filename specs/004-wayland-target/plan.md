# Implementation Plan: Native Wayland Target

**Branch**: `004-wayland-target` | **Date**: 2026-04-05 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/004-wayland-target/spec.md`

## Summary

Add a native Wayland rendering target to Uno Platform's Skia runtime, enabling applications to run directly on Wayland compositors without XWayland. The implementation follows the existing platform host pattern (mirroring X11), using P/Invoke bindings to libwayland-client, EGL for GPU rendering (with wl_shm software fallback), xkbcommon for keyboard input, and the xdg-shell protocol for window management. Five Linux-generic extensions (file pickers, launcher, theme helper) are reused from the X11 target; nine Wayland-specific extensions are implemented new.

## Technical Context

**Language/Version**: C# on .NET 10.0 (multi-target via `$(NetSkiaPreviousAndCurrent)`)
**Primary Dependencies**: SkiaSharp, HarfBuzzSharp, libwayland-client (native), libxkbcommon (native), libEGL (native)
**Storage**: N/A
**Testing**: SamplesApp.Skia.Generic for visual validation; Uno.UI.RuntimeTests for runtime tests; headless Weston for CI
**Target Platform**: Linux with Wayland compositor (GNOME/Mutter, KDE/KWin, Sway, and other wlroots-based compositors)
**Project Type**: New Skia runtime project within existing multi-project solution
**Performance Goals**: 60 fps rendering, input latency ≤ XWayland baseline
**Constraints**: Must not regress existing X11 target; must work with core stable Wayland protocols only (optional protocol degradation for extras)
**Scale/Scope**: ~40-60 source files in new project; 9 new extension implementations; ~15 P/Invoke binding files

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Check

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | **PASS** | No new public APIs. All existing WinUI APIs are rendered through existing Uno abstractions. Wayland limitations (no client-initiated move/raise) are platform constraints documented per the principle's exception clause. |
| II. Cross-Platform Parity | **PASS** | New platform target extends parity. Code isolated in platform-specific project (`Uno.UI.Runtime.Skia.Wayland`). Runtime tests will execute on Skia target. |
| III. Test-First Quality Gates | **PASS** | Runtime tests run via headless Weston. Existing runtime test suite will be executed against Wayland target. New Wayland-specific tests added for protocol handling. |
| IV. Performance and Resource Discipline | **PASS** | EGL rendering matches X11's EGL path. No additional per-frame allocations beyond X11 baseline. Frame rate configurable (default 60fps). |
| V. Generated Code Boundaries | **PASS** | No generated files are modified. All new code is in a new project. |
| VI. Backward Compatibility | **PASS** | Additive change only. No existing APIs are modified or removed. X11 target is unaffected. |
| VII. WinUI Implementation Alignment | **N/A** | This is a platform runtime feature, not a WinUI control or API implementation. |

### Post-Design Check

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | **PASS** | `WaylandWindowWrapper.Move()` and `Activate()` are no-ops due to Wayland design (clients cannot position/raise windows). This is a fundamental platform constraint, documented in code comments per Principle I exception clause. |
| II. Cross-Platform Parity | **PASS** | All 9 new extensions implement existing interfaces. 5 Linux-generic extensions reused. Feature parity with X11 target for P1/P2 stories. |
| III. Test-First Quality Gates | **PASS** | Testing strategy defined: headless Weston + SamplesApp + runtime tests. |
| IV. Performance and Resource Discipline | **PASS** | Rendering pipeline reuses Skia GRContext pattern. Event loop uses single `poll()` fd (simpler than X11's multi-fd model). |
| V. Generated Code Boundaries | **PASS** | No generated files touched. |
| VI. Backward Compatibility | **PASS** | Purely additive. `UseWayland()` is opt-in via builder chain. |

**All gates pass. No violations to track.**

## Project Structure

### Documentation (this feature)

```text
specs/004-wayland-target/
├── plan.md              # This file
├── spec.md              # Feature specification
├── research.md          # Phase 0 research decisions
├── data-model.md        # Entity definitions
├── quickstart.md        # Development setup guide
├── contracts/           # Interface contracts
│   └── wayland-host-contracts.md
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root)

```text
src/Uno.UI.Runtime.Skia.Wayland/
├── Uno.UI.Runtime.Skia.Wayland.csproj
├── Builder/
│   ├── WaylandHostBuilder.cs
│   └── HostBuilder.cs
├── Hosting/
│   ├── WaylandApplicationHost.cs
│   └── WaylandXamlRootHost.cs
├── UI/Xaml/Window/
│   ├── WaylandWindow.cs
│   ├── WaylandWindowWrapper.cs
│   └── WaylandNativeWindowFactoryExtension.cs
├── Rendering/
│   ├── WaylandRenderer.cs
│   ├── WaylandEGLRenderer.cs
│   └── WaylandSoftwareRenderer.cs
├── Devices/Input/
│   ├── WaylandPointerInputSource.cs
│   ├── WaylandKeyboardInputSource.cs
│   └── WaylandTouchInputSource.cs
├── ApplicationModel/
│   ├── Core/
│   │   └── WaylandCoreApplicationExtension.cs
│   └── DataTransfer/
│       ├── WaylandClipboardExtension.cs
│       └── DragDrop/
│           └── WaylandDragDropExtension.cs
├── Graphics/
│   ├── Display/
│   │   └── WaylandDisplayInformationExtension.cs
│   └── WaylandNativeOpenGLWrapper.cs
├── UI/ViewManagement/
│   └── WaylandApplicationViewExtension.cs
└── Wayland_Bindings/
    ├── WaylandBindings.cs
    ├── WaylandStructs.cs
    ├── WaylandEnums.cs
    ├── XdgShellBindings.cs
    ├── XdgDecorationBindings.cs
    ├── WpFractionalScaleBindings.cs
    ├── WpViewporterBindings.cs
    ├── CursorShapeBindings.cs
    ├── EglBindings.cs
    └── XkbCommonBindings.cs
```

**Modified files** (outside the new project):

```text
src/SamplesApp/SamplesApp.Skia.Generic/Program.cs          # Add .UseWayland()
src/SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj  # Add project reference
src/Uno.UI-Skia-only.slnf                                  # Add new project to solution filter
src/Uno.UI.sln                                              # Add new project to solution
```

**Structure Decision**: New standalone Skia runtime project following the exact same directory layout as `Uno.UI.Runtime.Skia.X11`. This is the established pattern for all Skia platform targets in the codebase.

## Complexity Tracking

> No constitution violations. No complexity to justify.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| *(none)* | — | — |
