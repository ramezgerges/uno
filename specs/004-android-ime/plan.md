# Implementation Plan: Android IME Composition Events

**Branch**: `004-android-ime` | **Date**: 2026-03-17 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/004-android-ime/spec.md`

## Summary

Bridge Android's `BaseInputConnection` composition events (`SetComposingText`, `CommitText`, `FinishComposingText`) to Uno Platform's `IImeTextBoxExtension` interface on Skia Android. The existing `TextInputConnection` and `ObservableEditingState` already receive composition state from the Android IME framework — the gap is that no `IImeTextBoxExtension` is registered, so `TextCompositionStarted`/`Changed`/`Ended` events never fire. This is primarily a bridging/wiring task, not implementing IME from scratch.

## Technical Context

**Language/Version**: C# / .NET 10.0 (net10.0-android target)
**Primary Dependencies**: Android SDK (BaseInputConnection, InputMethodManager), Uno.UI.Runtime.Skia.Android, existing TextInputPlugin/TextInputConnection/ObservableEditingState
**Storage**: N/A
**Testing**: Android emulator with Gboard Pinyin IME, SamplesApp.Skia.netcoremobile, runtime tests via headless Skia
**Target Platform**: Android 10+ (API 29+) via Skia rendering
**Project Type**: Mobile (Uno Platform cross-platform framework)
**Performance Goals**: No measurable regression in text input latency or frame rendering
**Constraints**: Must not break existing non-CJK text input; must integrate with existing TextInputPlugin lifecycle
**Scale/Scope**: 1 new file (AndroidImeTextBoxExtension), 2 modified files (TextInputConnection, AndroidHost), 1 test environment setup

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | TextComposition events match WinUI API surface |
| II. Cross-Platform Parity | PASS | Brings Android to parity with Win32 and X11 IME implementations |
| III. Test-First Quality Gates | PASS | Testing via Android emulator with CJK IME + SamplesApp |
| IV. Performance and Resource Discipline | PASS | No hot-path changes; composition callbacks are infrequent (per-keystroke during CJK input) |
| V. Generated Code Boundaries | PASS | No generated files involved |
| VI. Backward Compatibility | PASS | No breaking changes; additive only |
| VII. WinUI Implementation Alignment | PASS | TextComposition events already defined by WinUI; Android IME maps naturally to Start→Update→Complete→End lifecycle |

## Project Structure

### Documentation (this feature)

```text
specs/004-android-ime/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output - testing environment setup
├── contracts/           # Phase 1 output - interface contracts
└── tasks.md             # Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
src/Uno.UI.Runtime.Skia.Android/
├── Hosting/
│   └── AndroidHost.cs                              # MODIFIED: register IImeTextBoxExtension
├── UI/Xaml/Controls/TextBox/
│   ├── AndroidImeTextBoxExtension.cs               # NEW: IImeTextBoxExtension implementation
│   ├── TextInputConnection.cs                      # MODIFIED: add composition state change notifications
│   ├── TextInputPlugin.cs                          # MODIFIED: expose composition notification hookpoint
│   ├── ObservableEditingState.cs                    # READ-ONLY: source of ComposingStart/ComposingEnd
│   └── AndroidSkiaTextBoxNotificationsProviderSingleton.cs  # READ-ONLY: focus lifecycle reference
```

**Structure Decision**: All changes are within the existing `Uno.UI.Runtime.Skia.Android` project. One new file, two modified files, one registration line. Follows the same pattern established by Win32 (`Win32ImeTextBoxExtension`) and X11 (`X11ImeTextBoxExtension`).
