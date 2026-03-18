# Implementation Plan: iOS Skia IME Composition Support

**Branch**: `006-ios-ime` | **Date**: 2026-03-18 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/006-ios-ime/spec.md`

## Summary

Add IME composition support (CJK input methods) to the Skia iOS target by intercepting UITextInput protocol methods (`SetMarkedText`, `InsertText`, `UnmarkText`) on the existing invisible UITextField/UITextView proxies and forwarding composition events through the `IImeTextBoxExtension` interface to the shared TextBox composition system. This completes IME support across all Skia targets (X11, Win32, macOS, Android, iOS).

## Technical Context

**Language/Version**: C# (.NET 10.0, net10.0-ios target framework)
**Primary Dependencies**: UIKit (UITextField, UITextView, UITextInput protocol), Uno.UI shared TextBox composition system
**Storage**: N/A
**Testing**: Uno.UI.RuntimeTests on Skia desktop (headless) for composition event logic. End-to-end IME validation requires macOS + iOS Simulator (out of scope for this environment).
**Target Platform**: iOS 16+ (Skia rendering via AppleUIKit runtime)
**Project Type**: Platform extension within existing cross-platform framework
**Performance Goals**: Composition text display within 100ms of keystroke (matching native UITextField responsiveness)
**Constraints**: Must not break existing non-IME text input; must not interfere with native UITextInput lifecycle; must suppress double text processing during composition. Build environment is Linux (no iOS build/test — code review and Skia desktop compilation only).
**Scale/Scope**: ~7 files modified, ~1 new file, ~200 lines of new code

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Research Check

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | TextCompositionStarted/Changed/Ended events match WinUI API surface. IImeTextBoxExtension is internal Uno API. |
| II. Cross-Platform Parity | PASS | This feature specifically closes an iOS parity gap. All platform-specific code isolated in `.AppleUIKit` project. |
| III. Test-First Quality Gates | PASS | Runtime tests for composition event sequence run on Skia desktop (headless). End-to-end IME requires macOS + iOS Simulator — out of scope for this build environment. Code review + compile validation only for iOS-specific files. |
| IV. Performance and Resource Discipline | PASS | No hot-path changes. Composition events fire only during active IME input. No per-frame allocations. |
| V. Generated Code Boundaries | PASS | No generated files affected. |
| VI. Backward Compatibility | PASS | No breaking changes. New functionality only. Existing text input behavior unchanged when IME is not composing. |
| VII. WinUI Implementation Alignment | PASS | Composition events follow WinUI TextComposition event pattern already established in shared TextBox. |

**Gate result**: All principles PASS. Proceeding to Phase 0.

### Post-Design Re-Check

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | No new public APIs introduced. Internal IImeTextBoxExtension follows established pattern. |
| II. Cross-Platform Parity | PASS | iOS now matches X11, Win32, macOS, Android IME composition behavior. |
| III. Test-First Quality Gates | PASS | Composition state machine testable via runtime tests on Skia desktop. iOS-specific files validated by code review + compile check only (no iOS SDK in environment). |
| IV. Performance and Resource Discipline | PASS | Composition flag check is O(1). No allocations in non-composition path. |
| V. Generated Code Boundaries | PASS | No generated files affected. |
| VI. Backward Compatibility | PASS | Composition-inactive path unchanged. `ProcessNativeTextInput` suppression only activates when `IsComposing` is true. |
| VII. WinUI Implementation Alignment | PASS | Same TextComposition events as WinUI. |

**Gate result**: All principles PASS.

## Project Structure

### Documentation (this feature)

```text
specs/006-ios-ime/
├── plan.md              # This file
├── spec.md              # Feature specification
├── research.md          # Phase 0 research decisions
├── data-model.md        # Composition state model
├── quickstart.md        # Integration scenarios and test guide
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/Uno.UI.Runtime.Skia.AppleUIKit/
├── Hosting/
│   └── ExtensionsRegistrar.cs                          # MODIFY: Register IImeTextBoxExtension
└── UI/Xaml/Controls/TextBox/
    ├── AppleUIKitImeTextBoxExtension.cs                # NEW: IImeTextBoxExtension implementation
    ├── InvisibleTextBoxViewExtension.cs                # MODIFY: Suppress ProcessNativeTextInput during composition
    ├── SinglelineInvisibleTextBoxView.cs               # MODIFY: Override SetMarkedText/InsertText/UnmarkText
    ├── MultilineInvisibleTextBoxView.cs                # MODIFY: Override SetMarkedText/InsertText/UnmarkText
    ├── SinglelineInvisibleTextBoxDelegate.cs           # MODIFY: Composition-aware validation
    ├── MultilineInvisibleTextBoxDelegate.cs            # MODIFY: Composition-aware validation
    ├── IInvisibleTextBoxView.cs                        # MODIFY: Add IsComposing property
    └── NativeTextSelection.cs                          # NO CHANGE

src/Uno.UI/UI/Xaml/Controls/TextBox/
├── TextBox.skia.cs                                     # NO CHANGE (shared composition logic already exists)
└── Extensions/
    ├── IImeTextBoxExtension.skia.cs                    # NO CHANGE (interface already defined)
    └── ImeCompositionEventArgs.skia.cs                 # NO CHANGE

src/Uno.UI.RuntimeTests/
└── Tests/Windows_UI_Xaml_Controls/TextBox/             # ADD: IME composition runtime tests
```

**Structure Decision**: This feature adds to the existing `Uno.UI.Runtime.Skia.AppleUIKit` project following the established platform extension pattern. One new file (`AppleUIKitImeTextBoxExtension.cs`), six modified files, and runtime tests.

## Design Details

### Architecture

```
iOS System Keyboard (CJK IME)
    ↓ UITextInput protocol calls
SinglelineInvisibleTextBoxView / MultilineInvisibleTextBoxView
    ↓ Override SetMarkedText/InsertText/UnmarkText
AppleUIKitImeTextBoxExtension (singleton, IImeTextBoxExtension)
    ↓ CompositionStarted/Updated/Completed/Ended events
TextBox.skia.cs (shared)
    ↓ OnImeComposition* handlers
Skia rendering (composition underline, text display)
```

### Key Design Decisions

1. **Override UITextInput methods on existing subclasses**: `SinglelineInvisibleTextBoxView` (UITextField) and `MultilineInvisibleTextBoxView` (UITextView) already subclass the native views. Override `SetMarkedText`, `InsertText`, `UnmarkText` to intercept composition lifecycle.

2. **Suppress normal text processing during composition**: Add `IsComposing` flag to `IInvisibleTextBoxView`. When true, `InvisibleTextBoxViewExtension.ProcessNativeTextInput` returns early, preventing double text processing. The shared `TextBox.skia.cs` composition handlers manage text via `ReplaceCompositionText`.

3. **Non-composing InsertText handling**: When `InsertText` is called without prior `SetMarkedText` (direct character input through IME), fire the full Started→Completed→Ended cycle, matching the macOS pattern.

4. **Delegate composition awareness**: Delegates check `IsComposing` in `ShouldChangeCharacters`/`ShouldChangeText` to allow composition commits through without MaxLength interference.

5. **Post-composition native sync**: After `CompositionCompleted`, sync the managed TextBox text back to the native view to ensure consistency.

### File-by-File Changes

#### NEW: `AppleUIKitImeTextBoxExtension.cs`
- Singleton implementing `IImeTextBoxExtension`
- `StartImeSession(TextBox)`: Store reference to active TextBox, skip for PasswordBox
- `EndImeSession()`: Commit any active composition, clear references
- Composition state tracking: `_isComposing`, `_lastComposingText`
- Event firing methods called by native view overrides: `OnSetMarkedText`, `OnInsertText`, `OnUnmarkText`
- `UpdateCaretPosition()`: Read caret rect from TextBoxView display block

#### MODIFY: `SinglelineInvisibleTextBoxView.cs`
- Override `SetMarkedText(NSAttributedString, NSRange)`: Forward to extension's `OnSetMarkedText`
- Override `InsertText(string)`: Call base first, then forward to extension's `OnInsertText`
- Override `UnmarkText()`: Forward to extension's `OnUnmarkText`
- Add `IsComposing` property (delegates to extension)

#### MODIFY: `MultilineInvisibleTextBoxView.cs`
- Same overrides as SinglelineInvisibleTextBoxView

#### MODIFY: `IInvisibleTextBoxView.cs`
- Add `bool IsComposing { get; }` to interface

#### MODIFY: `InvisibleTextBoxViewExtension.cs`
- In `ProcessNativeTextInput`: Return early if `_nativeView.IsComposing`
- After composition completes: Call `SetTextNative` to sync managed → native

#### MODIFY: `SinglelineInvisibleTextBoxDelegate.cs`
- In `ShouldChangeCharacters`: Allow changes when `IsComposing` is true (bypass MaxLength check)

#### MODIFY: `MultilineInvisibleTextBoxDelegate.cs`
- In `ShouldChangeText`: Allow changes when `IsComposing` is true (bypass MaxLength check)

#### MODIFY: `ExtensionsRegistrar.cs`
- Add: `ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => AppleUIKitImeTextBoxExtension.Instance);`

## Complexity Tracking

> No constitution violations to justify. All principles pass.
