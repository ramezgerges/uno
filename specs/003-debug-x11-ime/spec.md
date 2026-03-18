# Feature Specification: Debug X11 IME Implementation

**Feature Branch**: `003-debug-x11-ime`
**Created**: 2026-03-17
**Status**: Draft
**Input**: User description: "The goal is to be able to debug the X11 IME implementation and figure out why it's not working."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Diagnostic Logging for IME Event Flow (Priority: P1)

As a developer debugging the X11 IME implementation, I want comprehensive diagnostic logging of the entire IME event lifecycle so I can trace exactly what happens from key press to text insertion and identify where the pipeline breaks.

**Why this priority**: Without visibility into the event flow, debugging is guesswork. Logging is the foundational tool that enables all other debugging activities.

**Independent Test**: Can be tested by enabling logging, typing in a TextBox with an active IME (e.g., IBus + Pinyin), and verifying that every stage of the event pipeline produces a log entry that can be followed from input to output.

**Acceptance Scenarios**:

1. **Given** a TextBox is focused with an active IME, **When** the developer types a key, **Then** a log entry is produced for each stage: event filter result, text lookup status and returned data, composition state transitions, and final text insertion or key dispatch.
2. **Given** logging is enabled, **When** the developer types an IME composition sequence (e.g., Pinyin "nihao" then selects a candidate), **Then** the log clearly shows the transition from composing to committed text, including the committed string value.
3. **Given** logging is enabled, **When** a filtered event is processed, **Then** the log distinguishes between filtered and non-filtered events and shows whether the event was skipped, forwarded to IME processing, or dispatched as a regular key event.

---

### User Story 2 - IME Debug Sample Page (Priority: P2)

As a developer debugging the X11 IME implementation, I want a dedicated sample page in SamplesApp that displays real-time IME state and event information so I can visually observe what the system is doing without reading raw log output.

**Why this priority**: A visual debug tool is faster to use than log parsing once the logging infrastructure exists. It provides immediate feedback during interactive testing.

**Independent Test**: Can be tested by opening the sample page in SamplesApp on X11, focusing the TextBox, typing with an IME, and verifying that composition state, event details, and committed text are displayed in real time.

**Acceptance Scenarios**:

1. **Given** the debug sample page is open, **When** the developer types with an IME active, **Then** the page displays the current composition state (composing/not composing), the last lookup status returned, and the last committed text.
2. **Given** the debug sample page is open, **When** a composition sequence completes, **Then** the page shows the committed text value, the event sequence that led to the commit, and the final TextBox content.
3. **Given** the debug sample page is open, **When** the developer types without an IME (direct input), **Then** the page shows that events bypass composition and go through the normal key dispatch path.

---

### User Story 3 - Expected Event Sequence Documentation (Priority: P3)

As a developer debugging the X11 IME implementation, I want documented reference event sequences for common IME scenarios so I can compare observed behavior against expected behavior to pinpoint where things go wrong.

**Why this priority**: Once logging and visual tools exist, comparison against expected behavior accelerates root-cause analysis for specific bugs.

**Independent Test**: Can be tested by capturing an event sequence log for a known input scenario and comparing it against the documented expected sequence for that scenario.

**Acceptance Scenarios**:

1. **Given** a reference event sequence for "type 'a' with IBus English mode", **When** the developer performs that action and captures the log, **Then** the log can be compared step-by-step against the reference to identify any divergence.
2. **Given** a reference event sequence for "type Pinyin 'ni' and select first candidate", **When** the developer performs that action, **Then** the captured sequence shows all expected stages: filtered key events during composition, lookup-keysym-only results during preedit, and lookup-chars with committed text on candidate selection.

---

### Edge Cases

- What happens when no IME is configured (input method environment not set or input method initialization fails)?
- How does the system behave when the IME is switched mid-composition (e.g., toggling IBus on/off with a hotkey)?
- What happens when a TextBox loses focus during an active composition?
- How are rapid key sequences handled when the IME has latency in processing?
- What happens when an event is filtered but the text lookup returns no result (event fully consumed by IME)?

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST log the event filter result (filtered or not filtered) for every event processed in the keyboard event loop.
- **FR-002**: The system MUST log the text lookup return status (keysym + text, text only, keysym only, nothing, buffer overflow) along with the returned text and key symbol for every keyboard event processed through IME-aware lookup.
- **FR-003**: The system MUST log composition state transitions (started, updated, committed, ended) with the associated text values.
- **FR-004**: The system MUST log whether a keyboard event was dispatched as a key-down/key-up, routed to IME composition, or dropped, along with the reason for that routing decision.
- **FR-005**: The system MUST provide a sample page that displays real-time IME diagnostic information including composition state, last event details, and TextBox content.
- **FR-006**: The system MUST log the input context handle and window association when IME sessions are started and ended.
- **FR-007**: All diagnostic logging MUST be gated behind a log level check so it has zero overhead when not enabled.
- **FR-008**: The system MUST include documented expected event sequences for at least 3 common scenarios: direct ASCII input, CJK composition with commit, and composition with cancel.

### Key Entities

- **IME Event**: A keyboard event at any stage of processing, characterized by its filter status, lookup result, and dispatch decision.
- **Composition Session**: The lifecycle from when a user begins composing (first filtered event) through commit or cancel, including all intermediate preedit states.
- **Event Sequence**: An ordered list of IME events for a single user interaction, used for comparison against reference sequences.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer can trace any single key press through the entire IME pipeline (from input event to TextBox content change) using log output alone, within 30 seconds of reviewing the log.
- **SC-002**: The diagnostic sample page updates in real time (within 1 second) to reflect composition state changes and committed text.
- **SC-003**: Reference event sequences exist for at least 3 common scenarios (direct ASCII input, CJK composition with commit, composition with cancel), enabling step-by-step comparison.
- **SC-004**: The root cause of the current "double character insertion" bug can be identified using only the diagnostic tools provided by this feature.

## Assumptions

- The developer has access to a Linux environment with IBus or Fcitx installed.
- Logging uses the existing application logging infrastructure at trace and debug levels.
- The debug sample page is added to the existing SamplesApp, not as a standalone application.
- "Real-time" display means the sample page updates on the UI thread after each event is processed, not that it requires sub-millisecond latency.
