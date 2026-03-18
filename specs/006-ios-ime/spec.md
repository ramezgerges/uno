# Feature Specification: iOS Skia IME Composition Support

**Feature Branch**: `006-ios-ime`
**Created**: 2026-03-18
**Status**: Draft
**Input**: User description: "Now that we have both X11, Win32, MacOS and Android IME working, the goal is implement a solution for the Skia iOS target."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - CJK Composition Input in TextBox (Priority: P1)

A user on an iOS device running an Uno Platform Skia app focuses a TextBox and types using a CJK input method (e.g., Pinyin for Chinese, Kana/Romaji for Japanese, or Hangul for Korean). As they type phonetic keys, a composition (pre-edit) string appears inline in the TextBox showing the uncommitted text with visual distinction (underline). A candidate window appears near the caret, allowing the user to select the desired character or word. When the user selects a candidate or presses Enter/Space to confirm, the composed text replaces the composition string and becomes committed text in the TextBox.

**Why this priority**: CJK composition is the primary use case for IME support. Without it, users of Chinese, Japanese, and Korean languages cannot type in their native language on iOS Skia apps. This is the core deliverable that enables the entire feature.

**Independent Test**: Can be fully tested by launching an Uno Skia iOS app, focusing a TextBox, switching to a CJK keyboard (e.g., Pinyin), typing phonetic characters, verifying composition text appears with underline, verifying the candidate window appears near the caret, selecting a candidate, and confirming the final text is inserted correctly.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused and the user has Pinyin input enabled, **When** the user types "nihao", **Then** a composition string "nihao" appears in the TextBox with visual distinction (underline) and a candidate list is shown near the caret position.
2. **Given** a composition string is active with candidates showing, **When** the user selects "你好" from the candidate list, **Then** the composition string is replaced with "你好" and the candidate window closes.
3. **Given** a composition string is active, **When** the user presses Escape or deletes all composition characters, **Then** the composition is cancelled, no text is inserted, and the TextBox returns to its previous state.
4. **Given** a TextBox is focused with Pinyin input, **When** the user types and commits multiple characters in sequence, **Then** each committed character is inserted at the correct caret position and the caret advances accordingly.

---

### User Story 2 - Non-Composition Key Passthrough During IME (Priority: P2)

A user on an iOS device with an active CJK input method focuses a TextBox and uses non-composition keys (arrow keys, backspace, tab, Enter without active composition). These keys should behave normally—moving the caret, deleting characters, or navigating—without being intercepted or swallowed by the IME system.

**Why this priority**: Without proper key passthrough, the TextBox becomes unusable for editing once an IME is active. Users frequently switch between composing CJK text and using navigation/editing keys. This is the second most critical behavior after composition itself.

**Independent Test**: Can be tested by focusing a TextBox with CJK input active, typing some committed text, then using arrow keys to move within the text, backspace to delete, and tab to move focus—all without active composition.

**Acceptance Scenarios**:

1. **Given** a TextBox with committed text "你好世界" and no active composition, **When** the user presses the left arrow key, **Then** the caret moves one character to the left.
2. **Given** a TextBox with committed text and no active composition, **When** the user presses backspace, **Then** the character before the caret is deleted.
3. **Given** a TextBox is focused with no active composition, **When** the user presses Tab, **Then** focus moves to the next focusable element (standard tab behavior).

---

### User Story 3 - Candidate Window Positioning at Caret (Priority: P3)

When a user is composing text, the iOS input method's candidate bar or suggestion strip should be contextually aware of the caret position. On iOS, the system keyboard provides its own candidate bar above the keyboard; additionally, if the input method queries the caret rectangle (via `firstRect(for:)` on UITextInput), the correct screen coordinates should be reported so that any auxiliary UI is positioned correctly.

**Why this priority**: On iOS, candidate display is largely handled by the system keyboard's built-in suggestion bar, which works automatically. However, providing correct caret geometry ensures compatibility with third-party keyboards and assistive input methods that may position auxiliary windows relative to the text.

**Independent Test**: Can be tested by focusing a TextBox, starting composition, and verifying the system keyboard's candidate bar shows candidates. For geometry verification, a third-party keyboard or accessibility tool that uses `firstRect(for:)` can confirm the returned rectangle matches the visual caret position.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused and composition is active, **When** the system queries the caret rectangle, **Then** the returned rectangle corresponds to the visual caret position on screen.
2. **Given** the user scrolls or the TextBox layout changes during composition, **When** the system re-queries the caret position, **Then** the updated rectangle reflects the new visual position.

---

### Edge Cases

- What happens when the user switches between IME and non-IME keyboards while text is being composed? The active composition should be committed or cancelled before the keyboard switch completes.
- What happens when the TextBox loses focus during active composition? The composition should be committed (matching iOS standard behavior) and the IME state should be cleaned up.
- What happens when the TextBox is read-only or disabled? IME composition should not activate, and the system keyboard should reflect the non-editable state.
- How does the system handle very long composition strings (e.g., typing an entire sentence in Pinyin before committing)? The TextBox should scroll to keep the composition caret visible.
- What happens when a PasswordBox receives focus? IME composition should be suppressed, matching the behavior of standard iOS password fields.
- What happens when the user rotates the device during composition? The composition state should be preserved and the caret position should update to reflect the new layout.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST forward iOS UITextInput composition events (marked text) from the hidden text input proxy to the managed TextBox via the `IImeTextBoxExtension` interface.
- **FR-002**: The system MUST display composition (pre-edit) text inline in the TextBox with an underline visual indicator while composition is active.
- **FR-003**: The system MUST replace composition text with the final committed text when the user confirms a candidate selection.
- **FR-004**: The system MUST cancel composition and remove pre-edit text when the user cancels input (e.g., pressing Escape or deleting all composition characters).
- **FR-005**: The system MUST pass non-composition keys (arrows, backspace, tab, Enter without active composition) through to the normal key handling pipeline when no composition is active.
- **FR-006**: The system MUST report the correct caret rectangle in screen coordinates when the input method queries `firstRect(for:)`, enabling proper positioning of candidate UI.
- **FR-007**: The system MUST register the `IImeTextBoxExtension` via `ApiExtensibility` so that the shared TextBox code discovers and uses it on iOS Skia.
- **FR-008**: The system MUST commit or cancel any active composition when the TextBox loses focus.
- **FR-009**: The system MUST support the full composition lifecycle: Started → Updated (repeated) → Completed/Cancelled → Ended, matching the event sequence used on X11, Win32, macOS, and Android.
- **FR-010**: The system MUST NOT activate IME composition on PasswordBox or read-only TextBox controls.

### Key Entities

- **IImeTextBoxExtension**: The platform-agnostic interface that connects native IME events to the shared TextBox control. Fires CompositionStarted, CompositionUpdated, CompositionCompleted, and CompositionEnded events.
- **Hidden Text Input Proxy**: The off-screen native text input view (UITextField/UITextView) that receives iOS keyboard events and IME composition callbacks via UITextInput protocol.
- **Composition State**: Tracks whether composition is active, the current pre-edit text, the selected range within the composition, and the caret position for candidate window placement.

## Assumptions

- The existing hidden UITextField/UITextView proxy in the iOS Skia runtime already handles basic text input and keyboard display. The IME feature extends this existing mechanism rather than replacing it.
- iOS's UITextInput protocol already provides `setMarkedText(_:selectedRange:)`, `insertText(_:)`, and `unmarkText()` methods that map directly to the composition lifecycle.
- The system keyboard on iOS handles candidate display natively via its built-in suggestion bar; no custom candidate window rendering is needed.
- The `IImeTextBoxExtension` interface and composition event model established on other platforms (X11, Win32, macOS, Android) will be reused without modification.
- The composition underline rendering is handled by the shared TextBox rendering code once `CompositionUpdated` events are fired with the pre-edit text.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can type and commit CJK characters (Chinese via Pinyin, Japanese via Romaji, Korean via Hangul) in any TextBox within an Uno Platform Skia iOS app.
- **SC-002**: Composition text appears inline with visual distinction within 100ms of each keystroke, matching the responsiveness of native iOS text fields.
- **SC-003**: Non-composition keys (arrows, backspace, delete, tab, Enter) work correctly 100% of the time when no composition is active, with no key swallowing.
- **SC-004**: The IME composition behavior on iOS Skia matches the behavior already implemented on X11, Win32, macOS, and Android Skia targets—same event sequence, same visual feedback, same edge case handling.
- **SC-005**: The candidate selection bar on the iOS system keyboard displays and functions correctly during composition, allowing users to browse and select candidates.
