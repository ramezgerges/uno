# Feature Specification: Android IME Composition Events

**Feature Branch**: `004-android-ime`
**Created**: 2026-03-17
**Status**: Draft
**Input**: User description: "Now that we have both X11 and Win32 IME working, the goal is implement a solution for the Android target."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - CJK Text Composition in TextBox (Priority: P1)

A user on an Android device running Uno Platform (Skia rendering) opens an app with a TextBox, switches to a CJK IME (e.g., Gboard Pinyin, Google Japanese Input), and types a word. As they type phonetic keys, they see a composition preview (preedit) in the TextBox. When they select a candidate character, the final text is committed and the composition preview is replaced.

**Why this priority**: This is the core functionality — without it, CJK users cannot input text correctly on Android. The existing `TextInputConnection` already receives `SetComposingText` and `CommitText` from the Android IME framework, but these composition state changes are not surfaced to the managed TextBox layer via `IImeTextBoxExtension`, so `TextCompositionStarted`/`Changed`/`Ended` events never fire and composition-aware TextBox behavior is missing.

**Independent Test**: Can be tested by launching a Skia Android app with a TextBox, enabling a CJK keyboard (Gboard Pinyin), typing a phonetic sequence (e.g., "nihao"), observing composition underline during typing, selecting a candidate, and verifying the committed text appears correctly.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused on Skia Android, **When** the user types phonetic keys with a CJK IME, **Then** composition text appears in the TextBox with visual distinction and `TextCompositionStarted` fires.
2. **Given** composition is active, **When** the user continues typing, **Then** the composition text updates in-place and `TextCompositionChanged` fires with the updated text and range.
3. **Given** composition is active, **When** the user selects a candidate (e.g., taps a suggestion), **Then** the composition text is replaced with the committed text, `TextCompositionEnded` fires, and the caret advances past the committed text.
4. **Given** composition is active, **When** the user presses backspace to cancel composition, **Then** the composition text is removed and `TextCompositionEnded` fires.

---

### User Story 2 - TextComposition Events for Developers (Priority: P2)

A developer building an Uno Platform app subscribes to `TextCompositionStarted`, `TextCompositionChanged`, and `TextCompositionEnded` events on a TextBox. On Skia Android, these events fire with correct `StartIndex` and `Length` values during IME composition, matching the behavior already implemented on Win32 and X11.

**Why this priority**: Developers rely on these events for custom composition UIs, input validation during composition, and feature parity with Windows/Linux. Without these events, apps that depend on composition state cannot work correctly on Android.

**Independent Test**: Can be tested by registering event handlers on all three composition events, performing a CJK composition, and verifying the event arguments contain correct `StartIndex` and `Length` matching the composition region in the TextBox.

**Acceptance Scenarios**:

1. **Given** a developer subscribes to `TextCompositionStarted` on a TextBox, **When** the Android IME begins composing (first `SetComposingText` call), **Then** the event fires with the correct `StartIndex` and `Length`.
2. **Given** a developer subscribes to `TextCompositionChanged`, **When** the IME updates the composing text, **Then** the event fires with updated `StartIndex` and `Length` reflecting the current composition region.
3. **Given** a developer subscribes to `TextCompositionEnded`, **When** the IME commits text or finishes composing, **Then** the event fires with the final range of the committed text.
4. **Given** the TextBox has `MaxLength` set, **When** the committed text would exceed `MaxLength`, **Then** the text is truncated to respect the constraint.

---

### User Story 3 - Cross-Platform Consistency (Priority: P3)

An Uno Platform app with a TextBox behaves consistently across Windows (Win32 IME), Linux (X11 D-Bus IME), and Android (Skia Android IME) when using CJK input methods. The same `TextComposition*` event handlers produce equivalent results on all three platforms.

**Why this priority**: Cross-platform parity is a core Uno Platform value proposition. Once Android composition events work, developers can write one set of composition event handlers that work everywhere.

**Independent Test**: Can be tested by running the same app on all three platforms, performing identical CJK input sequences, and comparing `TextComposition*` event arguments and final TextBox content.

**Acceptance Scenarios**:

1. **Given** the same TextBox with the same event handlers, **When** the user types "nihao" and selects "你好" on each platform, **Then** the same sequence of `TextCompositionStarted` → `TextCompositionChanged` (n times) → `TextCompositionEnded` events fire with consistent `StartIndex` and `Length` values.
2. **Given** the same app running on Android, **When** the user cancels a composition mid-way, **Then** the TextBox state matches what would happen on Windows/Linux (composition text removed, caret returns to pre-composition position).

---

### Edge Cases

- What happens when the user switches IME keyboards mid-composition (e.g., from Pinyin to English)?
- How does the system handle rapid composition sequences where `SetComposingText` is called many times before `CommitText`?
- What happens when the TextBox is `IsReadOnly=true` and the IME sends `SetComposingText`?
- How does composition interact with `TextBox.MaxLength` when composition text is longer than remaining capacity?
- What happens when the app calls `TextBox.Text = "..."` programmatically during an active composition?
- How does composition behave when the TextBox loses focus mid-composition (e.g., user taps outside)?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST bridge Android `BaseInputConnection` composition events (`SetComposingText`, `CommitText`, `FinishComposingText`) to the managed `IImeTextBoxExtension` interface so that `TextCompositionStarted`/`Changed`/`Ended` events fire on Skia Android.
- **FR-002**: The system MUST register an `IImeTextBoxExtension` implementation for the Skia Android platform during host initialization.
- **FR-003**: The system MUST track the composing region and map it to composition event arguments with correct `StartIndex` and `Length`.
- **FR-004**: The system MUST fire `CompositionStarted` when the Android IME first sets composing text (transition from no composing region to a valid composing region).
- **FR-005**: The system MUST fire `CompositionUpdated` when the composing text changes while composition is active.
- **FR-006**: The system MUST fire `CompositionCompleted` when the IME commits text (composing region removed after `CommitText` or `FinishComposingText`).
- **FR-007**: The system MUST fire `CompositionEnded` when composition finishes, whether by commit, cancel, or focus loss.
- **FR-008**: The system MUST handle focus lifecycle correctly — calling `StartImeSession` when a TextBox gains focus and `EndImeSession` when it loses focus, integrating with the existing `ShowTextInput`/`HideTextInput` flow.
- **FR-009**: The system MUST work with the existing text input connection and editing state classes without breaking current text input behavior (non-CJK typing, backspace, selection, cut/copy/paste).
- **FR-010**: The system MUST suppress composition events for password fields since IME composition reveals characters.

### Key Entities

- **Android IME Extension**: New `IImeTextBoxExtension` implementation for Skia Android that bridges composition state to managed composition events.
- **Text Input Connection**: Existing component that receives `SetComposingText`/`CommitText` from the Android IME — needs to notify the new extension when composition state changes.
- **Editing State**: Existing component tracking `ComposingStart`/`ComposingEnd` — the source of truth for whether composition is active and what region is being composed.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can successfully input CJK text via IME composition on Skia Android, with the composed text appearing correctly in the TextBox after candidate selection.
- **SC-002**: All three `TextComposition*` events fire with correct `StartIndex` and `Length` during CJK composition on Skia Android, matching the event sequence observed on Win32 and X11.
- **SC-003**: Existing non-CJK text input (English typing, backspace, selection, clipboard operations) continues to work without regression on Skia Android.
- **SC-004**: Composition correctly handles edge cases: cancel mid-composition, focus loss during composition, `MaxLength` constraints, and `IsReadOnly` TextBoxes.

## Assumptions

- The existing text input connection and editing state classes in the Skia Android runtime are stable and do not need architectural changes — only notification hooks need to be added.
- The `IImeTextBoxExtension` interface (already used by Win32 and X11) is sufficient for Android's composition model; no interface changes are needed.
- Android's `BaseInputConnection` composition model (SetComposingText → CommitText) maps cleanly to the Start → Update → Complete → End event lifecycle.
- The editing state's change callback (with `composingRegionChanged` parameter) provides reliable notification when composition state transitions occur.
