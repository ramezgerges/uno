# Data Model: iOS Skia IME Composition Support

**Feature Branch**: `006-ios-ime`
**Date**: 2026-03-18

## Entities

### CompositionState (per TextBox)

Tracks the current IME composition lifecycle within the active TextBox.

| Field | Type | Description |
|-------|------|-------------|
| IsComposing | bool | Whether a composition session is active |
| CompositionStartIndex | int | Caret position where composition began |
| CompositionLength | int | Length of current pre-edit text |
| LastComposingText | string | Most recent composition string (for change detection) |

**State Transitions**:

```
Idle → Composing: SetMarkedText called with non-empty text
  Actions: Fire CompositionStarted, then CompositionUpdated

Composing → Composing: SetMarkedText called with different text
  Actions: Fire CompositionUpdated

Composing → Idle (committed): InsertText called after composition
  Actions: Fire CompositionCompleted (with committed text), then CompositionEnded

Composing → Idle (cancelled): UnmarkText or SetMarkedText with empty text
  Actions: Fire CompositionEnded (no committed text)

Idle → Idle (direct input): InsertText called without prior composition
  Actions: Fire CompositionStarted, CompositionCompleted, CompositionEnded
  Note: Matches the non-composing insertText pattern from macOS implementation
```

### ImeSession (singleton)

Tracks which TextBox currently has IME focus.

| Field | Type | Description |
|-------|------|-------------|
| ActiveTextBox | TextBox? | The TextBox that currently owns the IME session |
| ActiveNativeView | IInvisibleTextBoxView? | The native view receiving keyboard input |
| IsSessionActive | bool | Whether StartImeSession has been called without EndImeSession |

**Lifecycle**:
- Created when TextBox gains focus (`StartImeSession`)
- Destroyed when TextBox loses focus (`EndImeSession`)
- Only one session active at a time (enforced by iOS single first responder)

## Relationships

```
AppleUIKitImeTextBoxExtension (singleton)
  ├── owns → CompositionState
  ├── references → ActiveTextBox (via ImeSession)
  └── receives events from → SinglelineInvisibleTextBoxView OR MultilineInvisibleTextBoxView

InvisibleTextBoxViewExtension
  ├── creates → SinglelineInvisibleTextBoxView OR MultilineInvisibleTextBoxView
  ├── checks → CompositionState.IsComposing (to suppress ProcessNativeTextInput)
  └── references → AppleUIKitImeTextBoxExtension

TextBox.skia.cs (shared)
  ├── discovers → IImeTextBoxExtension via ApiExtensibility
  ├── subscribes to → CompositionStarted/Updated/Completed/Ended events
  └── manages → inline composition text display and underline rendering
```
