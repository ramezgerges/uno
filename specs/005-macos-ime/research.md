# Research: macOS IME Composition Events

## R1: macOS Text Input Protocol (NSTextInputClient)

**Decision**: Implement `NSTextInputClient` protocol on the macOS rendering view to receive IME composition events.

**Rationale**: macOS uses the `NSTextInputClient` protocol (successor to `NSTextInput`) for all text input including IME composition. The key methods are:
- `insertText:replacementRange:` — called when text is committed (either direct typing or IME commit)
- `setMarkedText:selectedRange:replacementRange:` — called when the IME updates preedit/composition text
- `unmarkText` — called when composition is finalized
- `hasMarkedText` — returns whether composition is active
- `markedRange` — returns the range of marked (composing) text
- `selectedRange` — returns the current selection range
- `firstRectForCharacterRange:actualRange:` — returns the screen rect for a character range (used for candidate window positioning)
- `validAttributesForMarkedText` — returns supported attributes for marked text

The rendering view (either `UNOMetalFlippedView` or `UNOSoftView`, both NSView subclasses) must adopt this protocol and call `[self.inputContext handleEvent:event]` for key events instead of directly extracting unicode characters.

**Alternatives considered**:
- Carbon Text Services Manager (TSM): Deprecated since macOS 10.5, not viable
- Direct CGEvent Unicode extraction (current approach): Does not support IME — only gets final characters, not composition

## R2: Where to Implement NSTextInputClient

**Decision**: Implement `NSTextInputClient` on a new or existing NSView that serves as the first responder for key events, and route composition callbacks to managed code via P/Invoke callbacks.

**Rationale**: The current architecture intercepts key events in `UNOWindow.sendEvent:` and extracts unicode via `CGEventKeyboardGetUnicodeString`. For IME support, we need the NSView to become first responder and call `[self interpretKeyEvents:@[event]]` instead, which routes the event through the macOS text input system. The text input system then calls back via `NSTextInputClient` methods.

The rendering view (`UNOMetalFlippedView` or `UNOSoftView`) is the natural candidate since it's already the content view. Both need to adopt `NSTextInputClient`.

**Alternatives considered**:
- Adding a hidden NSTextField overlay: Adds complexity, potential z-order issues, unnecessary
- Implementing on UNOWindow: NSWindow is not the right responder for text input — NSView is

## R3: Native-to-Managed Callback Pattern

**Decision**: Follow the existing callback pattern used for keyboard events — register function pointers via `uno_set_*_callbacks()` and invoke them from Objective-C when `NSTextInputClient` methods are called.

**Rationale**: The codebase already uses this pattern extensively:
- `uno_set_window_events_callbacks()` for keyboard/mouse events
- Callbacks are `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]` static methods in managed code
- This is a proven, low-overhead pattern that avoids managed-to-native call overhead

New callbacks needed:
- `ime_insert_text_callback_fn_ptr` — for `insertText:replacementRange:`
- `ime_set_marked_text_callback_fn_ptr` — for `setMarkedText:selectedRange:replacementRange:`
- `ime_get_rect_callback_fn_ptr` — for `firstRectForCharacterRange:actualRange:` (returns screen rect for caret positioning)

**Alternatives considered**:
- ObjCRuntime.Messaging: Would work but adds runtime overhead and is less explicit
- Embedding a managed NSView subclass: Would require Xamarin.Mac/MAUI bindings that aren't available in Uno's architecture

## R4: Candidate Window Positioning

**Decision**: Implement `firstRectForCharacterRange:actualRange:` to return the screen-space rectangle of the caret position, computed from the managed TextBox's caret coordinates.

**Rationale**: macOS automatically positions the IME candidate window based on the rectangle returned by `firstRectForCharacterRange:`. The managed layer already computes caret position for the Android and X11 implementations (using `ParsedText.GetRectForIndex()` and `TransformToVisual(null)`). A callback from native to managed can request this rect when the IME queries for it.

**Alternatives considered**:
- Always returning a fixed position: Would result in candidate window at wrong location
- Returning the full TextBox rect: Would position candidate window at TextBox corner, not caret

## R5: Integration with Existing Key Event Flow

**Decision**: When a TextBox has focus and IME session is active, key events should flow through `interpretKeyEvents:` instead of the direct `get_unicode()` + callback path. When no TextBox is focused, the existing direct path continues to work.

**Rationale**: The existing `sendEvent:` handler in `UNOWindow.m` directly extracts unicode from key events and calls the managed callback. This works for direct typing but bypasses the macOS text input system entirely. For IME support, key events must be routed through `[self.inputContext handleEvent:event]` or `[self interpretKeyEvents:@[event]]` which triggers the NSTextInputClient protocol callbacks.

The switch can be controlled by a flag (`_imeActive`) set from managed code when a TextBox gains/loses focus.

**Alternatives considered**:
- Always routing through interpretKeyEvents: Would change behavior for all key handling even when no TextBox is focused; could break keyboard shortcuts
- Intercepting at NSApplication level: Would be global, harder to control per-window/per-view
