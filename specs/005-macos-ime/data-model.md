# Data Model: macOS IME Composition Events

## Entities

### MacOSImeTextBoxExtension

Implementation of `IImeTextBoxExtension` for Skia macOS.

**Fields**:
- `_isComposing: bool` — whether an IME composition is currently active
- `_composingText: string?` — current marked (preedit) text
- `_activeTextBox: TextBox?` — the TextBox that currently has IME focus

**State Transitions**:
```
Idle --[setMarkedText with text]--> Composing
  fires: CompositionStarted, CompositionUpdated

Composing --[setMarkedText with updated text]--> Composing
  fires: CompositionUpdated

Composing --[insertText]--> Idle
  fires: CompositionCompleted, CompositionEnded

Composing --[unmarkText / focus loss]--> Idle
  fires: CompositionEnded
```

**Relationships**:
- Registered via `ApiExtensibility.Register(typeof(IImeTextBoxExtension), ...)`
- Events consumed by `TextBox.skia.cs` via the static `_imeExtension` field
- Communicates with native layer via P/Invoke callbacks

### Native Text Input State (Objective-C side)

Maintained in the NSView implementing `NSTextInputClient`.

**Fields**:
- `_markedText: NSString?` — current marked text from IME
- `_markedRange: NSRange` — range of marked text
- `_selectedRange: NSRange` — selection within marked text
- `_imeActive: BOOL` — whether to route key events through text input system

**Protocol Methods** (NSTextInputClient):
- `insertText:replacementRange:` → calls managed `ime_insert_text_callback`
- `setMarkedText:selectedRange:replacementRange:` → calls managed `ime_set_marked_text_callback`
- `unmarkText` → calls managed `ime_unmark_text_callback`
- `hasMarkedText` → returns `_markedText != nil && _markedText.length > 0`
- `markedRange` → returns `_markedRange`
- `selectedRange` → returns `_selectedRange`
- `firstRectForCharacterRange:actualRange:` → calls managed callback for caret rect
- `validAttributesForMarkedText` → returns empty array (composition rendering handled by managed layer)

### P/Invoke Callbacks

New callback types to add to `NativeUno.cs`:

- `ime_insert_text_callback(nint window, char* text, int length)` — committed text
- `ime_set_marked_text_callback(nint window, char* text, int length, int selectedStart, int selectedLength)` — composition update
- `ime_unmark_text_callback(nint window)` — composition end without commit
- `ime_get_caret_rect_callback(nint window, double* x, double* y, double* width, double* height)` — returns caret screen rect for candidate window positioning

### Composition Event Flow

```
macOS IME Framework
  │
  ▼
NSView (NSTextInputClient protocol)
  │  setMarkedText / insertText / unmarkText
  ▼
P/Invoke Callbacks (NativeUno)
  │
  ▼
MacOSWindowHost (static callback handlers)
  │
  ▼
MacOSImeTextBoxExtension
  │  CompositionStarted / Updated / Completed / Ended
  ▼
TextBox.skia.cs (_imeExtension events)
  │
  ▼
TextCompositionStarted / Changed / Ended (public events)
```
