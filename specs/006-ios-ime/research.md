# Research: iOS Skia IME Composition Support

**Feature Branch**: `006-ios-ime`
**Date**: 2026-03-18

## R1: iOS Text Input Architecture for Composition

**Decision**: Override `SetMarkedText`, `InsertText`, and `UnmarkText` methods on the existing `SinglelineInvisibleTextBoxView` (UITextField subclass) and `MultilineInvisibleTextBoxView` (UITextView subclass) to intercept composition events.

**Rationale**: UITextField and UITextView already conform to UITextInput protocol and handle marked text natively. Since the Uno runtime already subclasses these views, we can override the UITextInput methods directly to detect composition state changes. This is the most direct interception point and avoids KVO complexity or delegate method limitations.

**Alternatives considered**:
- KVO on `MarkedTextRange` property — fragile, timing-dependent, UIKit discourages KVO on UI properties
- UITextInputDelegate monitoring — requires implementing a separate delegate, adds indirection
- Custom UITextInput implementation from scratch — unnecessary since UITextField/UITextView already implement it

## R2: Composition Event Flow and Double-Processing Prevention

**Decision**: During active composition, suppress the normal `ProcessNativeTextInput` path in `InvisibleTextBoxViewExtension` and let the shared `TextBox.skia.cs` composition handlers (`ReplaceCompositionText`) manage text updates through `IImeTextBoxExtension` events.

**Rationale**: The shared TextBox has a composition text management system (`OnImeCompositionUpdated` → `ReplaceCompositionText` → `ProcessTextInput`) that handles inline display, underline rendering, and undo/redo correctly. If we also let the native text change flow through `ProcessNativeTextInput`, text would be processed twice. The composition path must take precedence.

**Alternatives considered**:
- Let both paths run and deduplicate — error-prone, text length mismatches during composition
- Disable the native view's text changes entirely during composition — would break the native IME since UITextInput relies on its own text storage
- Use only the native text path without composition events — would lose composition underline rendering and WinUI TextComposition events

## R3: Native View Text Synchronization During Composition

**Decision**: Allow the native UITextField/UITextView to manage its own text storage during composition (the system IME requires this). After composition completes, sync the managed TextBox text back to the native view to ensure consistency.

**Rationale**: iOS IME system directly reads/writes the native view's text storage during composition. Attempting to control the native view's text during active composition would break the IME. After composition completes and the managed TextBox has the final text, we perform a one-way sync (managed → native) to ensure both are consistent.

**Alternatives considered**:
- Two-way sync during composition — breaks IME because native text changes would trigger managed → native → native loop
- Native-only text during composition — would require the Skia renderer to read from native view instead of managed TextBox for display

## R4: Composition Detection in Delegate Methods

**Decision**: Add a composition-active flag to the `IInvisibleTextBoxView` interface. The delegates (`SinglelineInvisibleTextBoxDelegate`, `MultilineInvisibleTextBoxDelegate`) check this flag in `ShouldChangeCharacters`/`ShouldChangeText` to allow composition commits through without MaxLength validation interference, and `OnTextChanged` skips `ProcessNativeTextInput` when composition is active.

**Rationale**: During composition, iOS may call `shouldChangeCharacters` before committing text. MaxLength validation in the delegate should not reject composition commits. Similarly, intermediate text changes during composition should not flow through the normal `ProcessNativeTextInput` path since composition events handle the text.

**Alternatives considered**:
- Remove all validation from delegates — too permissive, would break non-IME behavior
- Validate against final committed text length — impossible to know at `shouldChangeCharacters` time

## R5: IImeTextBoxExtension Registration Pattern

**Decision**: Create `AppleUIKitImeTextBoxExtension` as a singleton implementing `IImeTextBoxExtension`, registered in `ExtensionsRegistrar.cs` via `ApiExtensibility.Register`. The native views hold a reference to the extension and call methods on it when composition state changes.

**Rationale**: Matches the pattern used by macOS, Win32, and X11 implementations. The singleton pattern works because only one TextBox can have IME focus at a time (iOS enforces single first responder).

**Alternatives considered**:
- Per-TextBox extension instances — unnecessary since only one TextBox can be composing at a time
- Event-based communication via C# events on the native views — adds coupling between view and extension layers

## R6: Caret Rectangle Reporting

**Decision**: Implement `UpdateCaretPosition` on the extension by reading the caret position from the focused TextBox's `TextBoxView` display block and converting to screen coordinates using the iOS coordinate system.

**Rationale**: iOS's `firstRect(for:)` on UITextInput is already implemented by UITextField/UITextView for their own invisible text storage, but this returns coordinates for the hidden off-screen view. For the system keyboard's suggestion bar, this is usually sufficient. For `IImeTextBoxExtension.UpdateCaretPosition`, we need the visual caret position in the Skia-rendered TextBox to support any auxiliary UI that queries caret geometry.

**Alternatives considered**:
- Override `firstRect(for:)` on the native views — would return the visual caret position but might confuse the system IME which expects positions relative to the native view
- Do nothing — the system keyboard suggestion bar works without explicit caret reporting, but third-party keyboards may not position correctly
