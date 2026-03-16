# Data Model: IME Support

**Branch**: `001-ime-support` | **Date**: 2026-03-16

## Entities

### CompositionState (managed, in TextBox.skia.cs)

Transient state tracking an active IME composition session.

| Field | Type | Description |
|-------|------|-------------|
| IsComposing | bool | Whether an IME composition is currently active |
| CompositionStartIndex | int | Start index of the composition region in TextBox.Text |
| CompositionLength | int | Length of the composition region |
| CompositionText | string | The current composition string (pre-committed) |
| OriginalText | string | TextBox.Text before composition started (for cancel/revert) |
| OriginalSelectionStart | int | Selection start before composition started |

### State Transitions

```
Idle → Composing:  CompositionStarted event from platform extension
  - Save OriginalText = Text, OriginalSelectionStart = SelectionStart
  - Set IsComposing = true, CompositionStartIndex = SelectionStart
  - Fire TextCompositionStarted

Composing → Composing:  CompositionUpdated event from platform extension
  - Replace composition region in text with new composition string
  - Update CompositionLength
  - Render underline on composition region
  - Fire TextCompositionChanged

Composing → Idle:  CompositionCompleted event from platform extension
  - Replace composition region with committed text
  - Clear composition state (IsComposing = false)
  - Fire TextCompositionEnded
  - Call ProcessTextInput() with final text

Composing → Idle:  CompositionEnded without commit (cancel)
  - Revert text to OriginalText
  - Restore selection to OriginalSelectionStart
  - Clear composition state
  - Fire TextCompositionEnded with length 0

Composing → Idle:  Focus lost during composition
  - Commit current composition text (platform-dependent)
  - Clear composition state
```

## Interface Contracts

### IImeTextBoxExtension (platform → managed)

See `contracts/IImeTextBoxExtension.cs` for the full interface definition.

**Registration pattern** (per platform):
```
Win32Host static ctor:
  ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => new Win32ImeTextBoxExtension());

TextBox.skia.cs static init:
  ApiExtensibility.CreateInstance(null, out _imeExtension);
```

### TextComposition Events (managed → developer)

These are the existing WinUI API stubs that will be implemented:

| Event | EventArgs | Properties | When Fired |
|-------|-----------|------------|------------|
| TextCompositionStarted | TextCompositionStartedEventArgs | StartIndex, Length | Composition begins |
| TextCompositionChanged | TextCompositionChangedEventArgs | StartIndex, Length | Composition string updates |
| TextCompositionEnded | TextCompositionEndedEventArgs | StartIndex, Length | Text committed or cancelled |
