# Feature Specification: macOS IME Composition Events

**Feature Branch**: `005-macos-ime`
**Created**: 2026-03-18
**Status**: Draft
**Input**: User description: "Now that we have both X11, Win32 and Android IME working, the goal is implement a solution for the Skia MacOS target."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - CJK Text Composition in TextBox (Priority: P1)

A user on macOS running an Uno Platform app (Skia rendering) opens a TextBox, switches to a CJK input method (e.g., macOS built-in Pinyin, Japanese Romaji, or Zhuyin), and types a phonetic sequence. As they type, they see a composition preview (preedit/marked text) in the TextBox. When they select a candidate character, the final text is committed and the composition preview is replaced.

**Why this priority**: This is the core functionality — without it, CJK users cannot input text correctly on macOS Skia. The macOS platform uses the NSTextInputClient protocol for IME integration. Currently, the Skia macOS host does not implement this protocol, so composition events are not received and CJK input methods do not work.

**Independent Test**: Can be tested by launching a Skia macOS app with a TextBox, switching to a CJK input method (e.g., Simplified Pinyin), typing a phonetic sequence (e.g., "nihao"), observing composition underline during typing, pressing Space or selecting a candidate, and verifying the committed text appears correctly.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused on Skia macOS, **When** the user types phonetic keys with a CJK IME, **Then** composition text appears in the TextBox with visual distinction and `TextCompositionStarted` fires.
2. **Given** composition is active, **When** the user continues typing, **Then** the composition text updates in-place and `TextCompositionChanged` fires with the updated text and range.
3. **Given** composition is active, **When** the user selects a candidate (e.g., presses Space to accept the first suggestion or clicks a candidate), **Then** the composition text is replaced with the committed text, `TextCompositionEnded` fires, and the caret advances past the committed text.
4. **Given** composition is active, **When** the user presses Escape to cancel composition, **Then** the composition text is removed and `TextCompositionEnded` fires.

---

### User Story 2 - TextComposition Events for Developers (Priority: P2)

A developer building an Uno Platform app subscribes to `TextCompositionStarted`, `TextCompositionChanged`, and `TextCompositionEnded` events on a TextBox. On Skia macOS, these events fire with correct `StartIndex` and `Length` values during IME composition, matching the behavior already implemented on Win32, X11, and Android.

**Why this priority**: Developers rely on these events for custom composition UIs, input validation during composition, and feature parity with other platforms. Without these events, apps that depend on composition state cannot work correctly on macOS.

**Independent Test**: Can be tested by registering event handlers on all three composition events, performing a CJK composition, and verifying the event arguments contain correct `StartIndex` and `Length` matching the composition region in the TextBox.

**Acceptance Scenarios**:

1. **Given** a developer subscribes to `TextCompositionStarted` on a TextBox, **When** the macOS IME begins composing (marked text appears), **Then** the event fires with the correct `StartIndex` and `Length`.
2. **Given** a developer subscribes to `TextCompositionChanged`, **When** the IME updates the marked text, **Then** the event fires with updated `StartIndex` and `Length` reflecting the current composition region.
3. **Given** a developer subscribes to `TextCompositionEnded`, **When** the IME commits text or finishes composing, **Then** the event fires with the final range of the committed text.

---

### User Story 3 - IME Candidate Window Positioning (Priority: P2)

When the user begins composing text in a TextBox on Skia macOS, the native macOS IME candidate window appears positioned near the text caret (insertion point), not at a default location such as the bottom of the screen.

**Why this priority**: Correct candidate window positioning is essential for usability — users need to see the candidate list near where they are typing to quickly select the correct character. This was a key issue solved on X11 and Android and must also work on macOS.

**Independent Test**: Can be tested by focusing a TextBox, beginning CJK composition, and observing that the macOS candidate window popup appears adjacent to the caret position in the TextBox.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused, **When** the user begins CJK composition, **Then** the macOS candidate window appears near the caret position in the TextBox.
2. **Given** the user moves the caret to a different position in the TextBox, **When** composition starts, **Then** the candidate window appears near the new caret position.
3. **Given** the TextBox is positioned in different areas of the app window, **When** composition starts, **Then** the candidate window correctly tracks the TextBox caret regardless of window position.

---

### User Story 4 - Cross-Platform Consistency (Priority: P3)

An Uno Platform app with a TextBox behaves consistently across Windows (Win32 IME), Linux (X11 D-Bus IME), Android (Skia Android IME), and macOS (Skia macOS IME) when using CJK input methods. The same `TextComposition*` event handlers produce equivalent results on all four platforms.

**Why this priority**: Cross-platform parity is a core Uno Platform value proposition. macOS is the final Skia platform missing IME composition support.

**Independent Test**: Can be tested by running the same app on all four platforms, performing identical CJK input sequences, and comparing `TextComposition*` event arguments and final TextBox content.

**Acceptance Scenarios**:

1. **Given** the same TextBox with the same event handlers, **When** the user types "nihao" and selects the appropriate candidate on each platform, **Then** the same sequence of `TextCompositionStarted` -> `TextCompositionChanged` (n times) -> `TextCompositionEnded` events fire with consistent `StartIndex` and `Length` values.
2. **Given** the same app running on macOS, **When** the user cancels a composition mid-way, **Then** the TextBox state matches what would happen on Windows/Linux/Android (composition text removed, caret returns to pre-composition position).

---

### Edge Cases

- What happens when the user switches input methods mid-composition (e.g., from Pinyin to English)?
- How does the system handle rapid composition sequences where marked text changes many times before commit?
- What happens when the TextBox is `IsReadOnly=true` and the IME sends marked text?
- How does composition interact with `TextBox.MaxLength` when composition text is longer than remaining capacity?
- What happens when the app calls `TextBox.Text = "..."` programmatically during an active composition?
- How does composition behave when the TextBox loses focus mid-composition (e.g., user clicks outside)?
- How does the system handle macOS "press and hold" accent input (e.g., holding 'a' to get accent options)?
- What happens when the user uses dictation (macOS speech-to-text) which may also use the text input protocol?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST implement the macOS text input protocol to receive composition events (marked text, committed text) from the macOS IME framework on the Skia macOS target.
- **FR-002**: The system MUST register an `IImeTextBoxExtension` implementation for the Skia macOS platform during host initialization.
- **FR-003**: The system MUST track the composition (marked text) region and map it to composition event arguments with correct `StartIndex` and `Length`.
- **FR-004**: The system MUST fire `CompositionStarted` when the macOS IME first sets marked text (transition from no marked text to marked text being present).
- **FR-005**: The system MUST fire `CompositionUpdated` when the marked text content changes while composition is active.
- **FR-006**: The system MUST fire `CompositionCompleted` when the IME commits text (marked text replaced with final text via insert/commit).
- **FR-007**: The system MUST fire `CompositionEnded` when composition finishes, whether by commit, cancel, or focus loss.
- **FR-008**: The system MUST report the correct caret position to the macOS IME framework so the candidate window appears near the text insertion point.
- **FR-009**: The system MUST handle focus lifecycle correctly — calling `StartImeSession` when a TextBox gains focus and `EndImeSession` when it loses focus.
- **FR-010**: The system MUST work alongside existing keyboard input handling without breaking non-CJK typing, backspace, selection, or clipboard operations.
- **FR-011**: The system MUST suppress composition events for password fields since IME composition reveals characters.
- **FR-012**: The system MUST handle macOS "press and hold" accent input correctly, treating it as a composition sequence.

### Key Entities

- **macOS IME Extension**: New `IImeTextBoxExtension` implementation for Skia macOS that bridges the macOS text input protocol to managed composition events.
- **Text Input Protocol Handler**: Component that implements the macOS text input client interface to receive marked text, committed text, and attribute ranges from the input method.
- **Candidate Window Positioning**: Mechanism to report the current caret rectangle to macOS so the native candidate window appears at the correct screen position.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can successfully input CJK text via IME composition on Skia macOS, with the composed text appearing correctly in the TextBox after candidate selection.
- **SC-002**: All three `TextComposition*` events fire with correct `StartIndex` and `Length` during CJK composition on Skia macOS, matching the event sequence observed on Win32, X11, and Android.
- **SC-003**: The macOS IME candidate window appears within 50 pixels of the TextBox caret position, not at a default/fixed screen location.
- **SC-004**: Existing non-CJK text input (English typing, backspace, selection, clipboard operations) continues to work without regression on Skia macOS.
- **SC-005**: Composition correctly handles edge cases: cancel mid-composition, focus loss during composition, `MaxLength` constraints, and `IsReadOnly` TextBoxes.

## Assumptions

- The existing `IImeTextBoxExtension` interface (already used by Win32, X11, and Android) is sufficient for macOS's composition model; no interface changes are needed.
- macOS's text input model (marked text with selected range and replacement range) maps to the Start -> Update -> Complete -> End event lifecycle used by the other platforms.
- The Skia macOS host already has a native view (NSView subclass) that can adopt the text input protocol.
- The existing keyboard input handling in the Skia macOS runtime provides a foundation to build upon, similar to how Android IME was built on top of the existing TextInputConnection.
- macOS "press and hold" accent input follows the same text input protocol as CJK IME composition.
