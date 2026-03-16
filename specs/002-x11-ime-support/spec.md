# Feature Specification: X11 IME Support for TextBox

**Feature Branch**: `002-x11-ime-support`
**Created**: 2026-03-16
**Status**: Draft
**Input**: User description: "Now that IME is implemented for Win32, we want to do the same for the X11 target."

## Context

The Win32 Skia target already has IME support via the `IImeTextBoxExtension` interface, registered through `ApiExtensibility`. The managed TextBox layer handles composition state, text insertion, underline rendering, and TextComposition events in a platform-agnostic way. This feature adds the X11-specific implementation of `IImeTextBoxExtension` so that IME input (CJK, Vietnamese, etc.) works on Linux desktops running X11.

## Assumptions

- The existing `IImeTextBoxExtension` interface and managed TextBox composition logic (composition state tracking, text replacement, underline rendering, TextComposition events) are complete and working from the Win32 implementation.
- The X11 implementation only needs to provide the platform-specific bridge: intercepting X11 IME events and raising the interface events.
- XIM (X Input Method) is the standard protocol for IME on X11 and is supported by all major input method frameworks (IBus, Fcitx, SCIM).
- The implementation targets TextBox only (not RichEditBox or other text controls), consistent with the Win32 scope.
- The implementation follows the same singleton + ApiExtensibility registration pattern used by Win32.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - CJK Text Input via IME (Priority: P1)

A user on a Linux desktop (X11) focuses a TextBox in an Uno Platform application and types CJK characters using their system IME (e.g., IBus with a Chinese/Japanese/Korean input method). As they type, a composition string appears in the TextBox with an underline indicating uncommitted text. When they select a candidate or press Enter, the final text is committed into the TextBox. The composition underline disappears and the cursor moves to the end of the committed text.

**Why this priority**: This is the core IME functionality — without it, CJK users cannot type in their language on Linux X11.

**Independent Test**: Can be fully tested by launching the SamplesApp on an X11 Linux desktop with an IME configured (e.g., IBus + Pinyin), focusing a TextBox, composing CJK characters, and verifying the composition string and committed text appear correctly.

**Acceptance Scenarios**:

1. **Given** a TextBox has focus on X11 with IBus active, **When** the user types a Pinyin sequence (e.g., "nihao"), **Then** the composition string appears in the TextBox with an underline.
2. **Given** a composition is active, **When** the user selects a candidate (e.g., presses Space or a number key), **Then** the composed text is committed into the TextBox and the underline disappears.
3. **Given** a composition is active, **When** the user presses Escape, **Then** the composition is cancelled and the composing text is removed.
4. **Given** a composition is active, **When** the TextBox loses focus, **Then** the active composition is committed (or cancelled per IME behavior) and the IME session ends cleanly.
5. **Given** a TextBox has focus, **When** the user types non-IME characters (e.g., ASCII letters with no IME active), **Then** text input works normally without interference.

---

### User Story 2 - TextComposition Events for X11 (Priority: P2)

A developer building an Uno Platform application uses the `TextCompositionStarted`, `TextCompositionChanged`, and `TextCompositionEnded` events on TextBox to monitor IME composition state on X11. These events fire at the correct times during composition, matching WinUI behavior.

**Why this priority**: Enables programmatic monitoring of IME state, important for advanced text input scenarios but secondary to basic text entry.

**Independent Test**: Can be tested by subscribing to TextComposition events in code-behind, composing text via IME, and verifying the events fire with correct `StartIndex` and `Length` values.

**Acceptance Scenarios**:

1. **Given** a TextBox with TextCompositionStarted handler, **When** the user begins an IME composition, **Then** the event fires with the correct start index.
2. **Given** an active composition, **When** the composition string changes, **Then** TextCompositionChanged fires with updated start index and length.
3. **Given** an active composition, **When** the user commits the text, **Then** TextCompositionEnded fires.

---

### Edge Cases

- What happens when the user switches between multiple TextBoxes during an active composition? The active composition should be committed in the old TextBox before the new TextBox starts a new IME session.
- What happens when the application has multiple windows on X11? Each window should manage its own XIM context independently.
- What happens when no IME is configured on the system? The extension should handle this gracefully — normal keyboard input continues to work, and the IME extension simply never receives composition events.
- What happens when the user switches IME input methods mid-session (e.g., from Pinyin to direct input)? The active composition should be committed or cancelled cleanly.
- What happens with Vietnamese input (e.g., Telex method)? The IME composition flow should work the same as CJK — composition string appears with underline, committed on confirmation.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST implement the `IImeTextBoxExtension` interface for the X11 Skia target, providing composition start, update, commit, and end events.
- **FR-002**: System MUST register the X11 IME extension via `ApiExtensibility` in the X11 host startup, following the same pattern as Win32.
- **FR-003**: System MUST intercept X11 IME events (XIM protocol) and forward them as `CompositionStarted`, `CompositionUpdated`, `CompositionCompleted`, and `CompositionEnded` events on the interface.
- **FR-004**: System MUST suppress normal key input processing during an active IME composition to prevent duplicate character insertion.
- **FR-005**: System MUST commit or end the active composition when the TextBox loses focus.
- **FR-006**: System MUST position the IME candidate window near the text cursor so candidates appear at the correct screen location.
- **FR-007**: System MUST handle the case where no IME is available on the system without errors or degraded non-IME input behavior.

### Key Entities

- **X11 IME Extension**: The platform-specific implementation of `IImeTextBoxExtension` that bridges X11/XIM events to the managed composition interface.
- **XIM Context**: The X11 Input Method context associated with each window, used to receive and process IME events.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can compose and commit CJK text (Chinese, Japanese, Korean) in a TextBox on X11 Linux using IBus or Fcitx input methods.
- **SC-002**: The IME candidate window appears near the text cursor position, not at a fixed screen location.
- **SC-003**: Composition text displays with an underline in the TextBox during active composition, matching Win32 behavior.
- **SC-004**: TextCompositionStarted, TextCompositionChanged, and TextCompositionEnded events fire correctly during X11 IME composition.
- **SC-005**: Non-IME keyboard input continues to work correctly when no IME is active or configured.
