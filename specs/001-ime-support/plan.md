# Implementation Plan: IME Support for TextBox

**Branch**: `001-ime-support` | **Date**: 2026-03-16 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-ime-support/spec.md`

## Summary

Add Input Method Editor (IME) support to TextBox in Uno Platform, enabling CJK text composition with inline composition string display, candidate window positioning, and WinUI-compatible TextComposition events. Implementation uses `ApiExtensibility` to inject platform-specific IME handling via a new `IImeTextBoxExtension` interface. Win32 is implemented first; other platforms follow sequentially after user verification.

## Technical Context

**Language/Version**: C# / .NET 10.0
**Primary Dependencies**: Uno.UI, Uno.UI.Runtime.Skia.Win32, Win32 IMM32 API (P/Invoke)
**Storage**: N/A
**Testing**: Runtime tests in `Uno.UI.RuntimeTests`, manual CJK IME testing on Windows
**Target Platform**: Skia Win32 (first), then macOS/Linux/WebAssembly/Mobile (sequentially)
**Project Type**: Existing cross-platform framework (Uno Platform)
**Performance Goals**: No measurable regression in TextBox input latency for non-IME typing
**Constraints**: Must match WinUI 3 TextComposition event API contract; must not break existing TextBox behavior
**Scale/Scope**: Affects TextBox control across all Skia platforms

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | Implements existing WinUI TextComposition events (currently stubbed as NotImplemented) |
| II. Cross-Platform Parity | PASS | Interface-based design ensures each platform can provide its own implementation. Win32 first, others follow. |
| III. Test-First Quality Gates | PASS | Runtime tests planned for composition state management and event firing |
| IV. Performance Discipline | PASS | No hot-path changes for non-IME input. Composition handling only activates during IME sessions. |
| V. Generated Code Boundaries | PASS | TextComposition event args will be copied from Generated to non-generated locations with NotImplemented removed |
| VI. Backward Compatibility | PASS | Additive change — implements previously-stubbed APIs. No breaking changes. |
| VII. WinUI Implementation Alignment | PASS | WinUI TextComposition events are the reference API. Win32 IMM32 is the standard Win32 approach. |

## Project Structure

### Documentation (this feature)

```text
specs/001-ime-support/
├── plan.md              # This file
├── spec.md              # Feature specification
├── research.md          # Phase 0 research findings
├── data-model.md        # Composition state model and transitions
├── quickstart.md        # Build and test instructions
├── contracts/
│   └── IImeTextBoxExtension.cs  # Interface contract definition
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root)

```text
src/
├── Uno.UI/UI/Xaml/Controls/TextBox/
│   ├── TextBox.skia.cs                          # MODIFY: Composition state, event wiring
│   ├── Extensions/
│   │   ├── IImeTextBoxExtension.skia.cs         # NEW: Platform abstraction interface
│   │   └── ImeCompositionEventArgs.skia.cs      # NEW: Event args for interface events
│   ├── TextCompositionStartedEventArgs.cs       # NEW: Implement (from Generated stub)
│   ├── TextCompositionChangedEventArgs.cs       # NEW: Implement (from Generated stub)
│   └── TextCompositionEndedEventArgs.cs         # NEW: Implement (from Generated stub)
├── Uno.UI/UI/Xaml/Documents/
│   └── UnicodeText.skia.cs                      # MODIFY: Composition underline rendering
├── Uno.UI.Runtime.Skia.Win32/
│   ├── Hosting/Win32Host.cs                     # MODIFY: Register IImeTextBoxExtension
│   ├── UI/Xaml/Controls/TextBox/
│   │   └── Win32ImeTextBoxExtension.cs          # NEW: Win32 IMM32 implementation
│   ├── UI/Xaml/Window/Win32WindowWrapper.cs      # MODIFY: Route WM_IME_* messages
│   └── Devices/Input/Win32WindowWrapper.Keyboard.cs  # MODIFY: Suppress WM_CHAR during composition
├── Uno.UI.RuntimeTests/Tests/Windows_UI_Xaml_Controls/
│   └── Given_TextBox_Ime.cs                     # NEW: Runtime tests for IME composition
└── SamplesApp/UITests.Shared/
    └── Windows_UI_Xaml_Controls/TextBox/
        └── TextBox_Ime.xaml(.cs)                # NEW: Manual test sample
```

**Structure Decision**: This feature adds files to existing project directories following Uno Platform's established patterns. New platform-specific code goes in the Win32 runtime project. New abstractions go in Uno.UI's TextBox Extensions directory. No new projects are needed.

## Implementation Phases

### Phase 1: Interface & Event Args Foundation

**Goal**: Define the `IImeTextBoxExtension` interface and implement the TextComposition event args classes.

**Steps**:
1. Create `IImeTextBoxExtension` interface in `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/IImeTextBoxExtension.skia.cs`
   - `StartImeSession(TextBox textBox)` — called on focus
   - `EndImeSession()` — called on unfocus
   - `UpdateCaretPosition(int x, int y)` — caret position updates
   - Events: `CompositionStarted`, `CompositionUpdated`, `CompositionCompleted`, `CompositionEnded`

2. Create `ImeCompositionEventArgs` in `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/ImeCompositionEventArgs.skia.cs`
   - `string Text` property for composition/committed text

3. Implement `TextCompositionStartedEventArgs`, `TextCompositionChangedEventArgs`, `TextCompositionEndedEventArgs`
   - Copy from `Generated/3.0.0.0/Microsoft.UI.Xaml.Controls/` to non-generated locations
   - Remove `[Uno.NotImplemented]` attributes for `__SKIA__`
   - Add internal constructor accepting `int startIndex, int length`
   - Add backing fields for `StartIndex` and `Length` properties

4. Update TextBox to expose the composition events (replace generated stubs)
   - Add real event declarations in TextBox.skia.cs or TextBox.cs
   - Wire up event invocation helpers

**Validation**: Unit tests compile; existing TextBox tests still pass.

### Phase 2: Managed Composition State in TextBox

**Goal**: Add composition state tracking and event firing to TextBox on Skia.

**Steps**:
1. Add composition state fields to `TextBox.skia.cs`:
   - `_isComposing`, `_compositionStartIndex`, `_compositionLength`, `_compositionText`
   - `_originalTextBeforeComposition`, `_originalSelectionStart`

2. Add methods to handle composition lifecycle (composition text lives in `TextBox.Text`, matching WinUI):
   - `OnImeCompositionStarted()` — saves `_originalTextBeforeComposition = Text` and `_originalSelectionStart = SelectionStart`, sets `_isComposing = true`, `_compositionStartIndex = SelectionStart`, fires `TextCompositionStarted`
   - `OnImeCompositionUpdated(string compositionText)` — builds new text by replacing the composition region (`Text[..compositionStart] + compositionText + Text[compositionStart+compositionLength..]`), calls `ProcessTextInput(newText)` to insert it into `TextBox.Text`, updates `_compositionLength = compositionText.Length`, fires `TextCompositionChanged`. This means `TextChanged` fires on each composition update, consistent with WinUI.
   - `OnImeCompositionCompleted(string committedText)` — replaces composition region with committed text via `ProcessTextInput()`, fires `TextCompositionEnded`, clears composition state (`_isComposing = false`, etc.)
   - `OnImeCompositionEnded()` — if still composing (cancel case), reverts `Text` to `_originalTextBeforeComposition` via `ProcessTextInput()`, restores selection, clears state

3. Wire `IImeTextBoxExtension` into TextBox initialization:
   - In `InitializePartial()`: resolve `IImeTextBoxExtension` via `ApiExtensibility.CreateInstance()`
   - Subscribe to extension's composition events → call the `OnIme*` methods
   - In focus handling: call `_imeExtension.StartImeSession(this)` / `EndImeSession()`
   - In selection change: call `_imeExtension.UpdateCaretPosition(x, y)`

4. Modify `OnKeyDownSkia()` to skip normal character insertion when `_isComposing` is true (the platform extension manages text updates via `OnImeCompositionUpdated` → `ProcessTextInput()` instead; without this guard, the peeked `WM_CHAR` would cause double insertion).

**Validation**: TextBox still handles non-IME input correctly. Composition state transitions work in unit tests.

### Phase 3: Win32 IMM32 Implementation

**Goal**: Implement `Win32ImeTextBoxExtension` that intercepts Win32 IME messages and raises composition events.

**Steps**:
1. Create `Win32ImeTextBoxExtension` in `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/TextBox/Win32ImeTextBoxExtension.cs`
   - Implements `IImeTextBoxExtension`
   - Singleton pattern (like `Win32ClipboardExtension`)
   - Holds reference to active TextBox's HWND

2. Register in `Win32Host.cs` static constructor:
   ```csharp
   ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => Win32ImeTextBoxExtension.Instance);
   ```

3. Add WM_IME_* message handling in `Win32WindowWrapper.WndProcInner()`:
   - `WM_IME_STARTCOMPOSITION`: Notify extension → raise `CompositionStarted`. Return 0 to suppress default composition window (Uno renders inline).
   - `WM_IME_COMPOSITION`: Extract composition string via `ImmGetCompositionString(himc, GCS_COMPSTR)` → raise `CompositionUpdated`. Extract committed text via `ImmGetCompositionString(himc, GCS_RESULTSTR)` → raise `CompositionCompleted`. Return 0 to suppress default handling.
   - `WM_IME_ENDCOMPOSITION`: Raise `CompositionEnded`. Return 0.
   - Route these messages through the extension singleton.

4. Modify `Win32WindowWrapper.Keyboard.cs` `OnKey()`:
   - When composition is active, suppress peeked `WM_CHAR` to prevent double text insertion (the committed text is already handled via `GCS_RESULTSTR`).

5. Integrate with existing `Win32ImeCaretManager`:
   - The existing caret manager already handles `ImmSetCompositionWindow`/`ImmSetCandidateWindow` positioning.
   - The extension's `UpdateCaretPosition` delegates to the existing manager.

**Validation**: On Windows with Japanese IME:
- Type "nihongo" in TextBox → see hiragana inline with underline
- Press Space → candidate window appears near caret
- Press Enter → kanji committed
- Press Escape during composition → text reverts
- Existing non-IME typing still works

### Phase 4: Composition Underline Rendering

**Goal**: Render the composition string with an underline visual indicator in the TextBox.

**Steps**:
1. Add composition range properties to `TextBoxView.skia.cs`:
   - `CompositionStartIndex` and `CompositionLength` properties (set by TextBox during composition)

2. Modify `UnicodeText.skia.cs` `Draw()` method to draw composition underline:
   - The `Draw()` method already supports spell-check wavy underlines (rendered as `SKPath` per cluster, collected in `spellCheckUnderlines` list). Composition underlines follow the same cluster-iteration pattern.
   - Add a composition range parameter (start index, length) to `Draw()` or pass it through a property.
   - During the cluster loop, check if the current cluster falls within the composition range.
   - For composition clusters, draw a straight underline (not wavy) at `y + line.baselineOffset + yOffset` using `SKCanvas.DrawLine()`.
   - Use the text foreground color for the underline.

3. Trigger re-render when composition state changes:
   - When `OnImeCompositionUpdated` is called, invalidate the TextBoxView to trigger a repaint.

**Validation**: Composition text appears underlined during typing. Underline disappears after commit/cancel.

### Phase 5: Runtime Tests & Sample

**Goal**: Add automated tests and a manual test sample.

**Steps**:
1. Create `Given_TextBox_Ime.cs` in `src/Uno.UI.RuntimeTests/Tests/Windows_UI_Xaml_Controls/`:
   - Test composition state management (simulate composition events through the interface)
   - Test TextCompositionStarted/Changed/Ended event firing with correct StartIndex and Length
   - Test composition cancel reverts text
   - Test focus loss during composition commits text
   - Test MaxLength constraint during composition
   - Test IsReadOnly prevents composition

2. Create `TextBox_Ime.xaml` sample in SamplesApp:
   - TextBox with event handlers that display composition event details
   - Register in `UITests.Shared.projitems`
   - Add `[Sample]` attribute

**Validation**: All runtime tests pass headlessly. Sample works for manual verification.

### Phase 6+: Other Platforms (after user verification of Win32)

**Goal**: Implement `IImeTextBoxExtension` for remaining platforms. Each platform proceeds only after user verifies the previous one.

**Sequence** (each is a separate implementation cycle):
1. **macOS (Skia)**: Use NSTextInputClient protocol for IME composition
2. **Linux (Skia)**: Use IBus/Fcitx via GTK/X11 input method integration
3. **WebAssembly**: Leverage browser's native `compositionstart`/`compositionupdate`/`compositionend` DOM events on the hidden `<input>` element
4. **Android (Skia)**: Extend existing `ObservableEditingState` composing region tracking
5. **iOS (Skia)**: Use UIKit's `UITextInput` protocol composition support

Each platform implementation:
1. Create platform-specific `IImeTextBoxExtension` implementation
2. Register in the platform's host static constructor
3. Handle platform-native IME events → raise interface events
4. Test with platform's native IME
5. User verification before proceeding to next platform

## Complexity Tracking

No constitution violations. This is an additive feature implementing existing WinUI API stubs.
