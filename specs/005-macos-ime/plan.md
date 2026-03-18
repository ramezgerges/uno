# Implementation Plan: macOS IME Composition Events

**Branch**: `005-macos-ime` | **Date**: 2026-03-18 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/005-macos-ime/spec.md`

## Summary

Implement IME (Input Method Editor) composition support for the Skia macOS target, enabling CJK text input via the macOS `NSTextInputClient` protocol. This is the fourth and final Skia platform to receive IME support, after Win32, X11, and Android. The implementation follows the established `IImeTextBoxExtension` pattern: native macOS text input events are bridged to the managed composition lifecycle (Started → Updated → Completed → Ended) via P/Invoke callbacks, with candidate window positioning driven by caret coordinates from the managed TextBox.

## Technical Context

**Language/Version**: C# (.NET 10.0) + Objective-C (macOS native)
**Primary Dependencies**: Uno.UI, Uno.UI.Runtime.Skia.MacOS, libUnoNativeMac.dylib (native Objective-C library)
**Storage**: N/A
**Testing**: SamplesApp with TextBox_IME_Debug sample on macOS with CJK input method; runtime tests on Skia desktop
**Target Platform**: macOS (Skia rendering via Metal or Software)
**Project Type**: Cross-platform framework library
**Performance Goals**: No measurable regression in keyboard input latency; candidate window positioning within 1 frame
**Constraints**: Must not break existing keyboard handling; must work with both Metal and Software rendering views; requires macOS for building and testing (native Objective-C compilation and NSTextInputClient protocol); cannot be built or tested in Linux/WSL environments
**Scale/Scope**: ~3 files modified (native), ~2 new managed files, ~1 modified managed file

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | TextComposition events match WinUI API surface |
| II. Cross-Platform Parity | PASS | Completing parity — macOS is the last Skia platform without IME |
| III. Test-First Quality Gates | PASS | Will use existing TextBox_IME_Debug sample for validation; runtime tests for composition events |
| IV. Performance and Resource Discipline | PASS | No hot-path changes; IME callbacks only fire during composition |
| V. Generated Code Boundaries | PASS | No generated files modified |
| VI. Backward Compatibility | PASS | New capability, no breaking changes |
| VII. WinUI Implementation Alignment | PASS | TextComposition events already aligned with WinUI in TextBox.skia.cs |

## Project Structure

### Documentation (this feature)

```text
specs/005-macos-ime/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   └── MacOSImeTextBoxExtension.cs
└── tasks.md             # Phase 2 output (via /speckit.tasks)
```

### Source Code (repository root)

```text
src/Uno.UI.Runtime.Skia.MacOS/
├── Hosting/
│   └── MacSkiaHost.cs                          # MODIFY: Register MacOSImeTextBoxExtension
├── UI/Xaml/Controls/TextBox/
│   └── MacOSImeTextBoxExtension.cs             # NEW: IImeTextBoxExtension implementation
├── UI/Xaml/Window/
│   └── MacOSWindowHost.cs                      # MODIFY: Add IME P/Invoke callbacks
├── Native/
│   └── NativeUno.cs                            # MODIFY: Add IME callback declarations
└── UnoNativeMac/UnoNativeMac/
    ├── UNOWindow.h                             # MODIFY: Add IME callback types and setters
    ├── UNOWindow.m                             # MODIFY: Route key events through text input when IME active
    ├── UNOMetalViewDelegate.h                  # MODIFY: Adopt NSTextInputClient protocol
    └── UNOMetalViewDelegate.m                  # MODIFY: Implement NSTextInputClient methods
    # (and/or UNOSoftView.h/m if software rendering needs same treatment)
```

**Structure Decision**: All changes are within the existing `Uno.UI.Runtime.Skia.MacOS` project structure. Native changes in the `UnoNativeMac` Objective-C library, managed changes in the C# runtime project. Follows the same pattern as all existing platform extensions.
