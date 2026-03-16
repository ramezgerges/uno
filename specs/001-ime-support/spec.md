# Feature Specification: IME (Input Method Editor) Support

**Feature Branch**: `001-ime-support`
**Created**: 2026-03-16
**Status**: Draft
**Input**: User description: "The goal is to investigate how to add IME support for Uno Platform and then implement it."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - CJK Text Composition in TextBox (Priority: P1)

A developer building an Uno Platform application needs their users to type Chinese, Japanese, or Korean text using an Input Method Editor (IME). When a user begins composing text in a TextBox, the composition string (pre-committed characters) must be displayed inline with visual distinction (typically underlined), and the IME candidate window must appear near the text cursor. The user selects a candidate to commit the final text.

**Why this priority**: CJK languages represent billions of users worldwide. Without IME composition support, TextBox controls are unusable for these languages. This is the foundational IME capability that all other stories depend on.

**Independent Test**: Can be fully tested by activating a CJK IME on any supported platform, typing a composition sequence in a TextBox, and verifying the composed text is correctly committed. Delivers basic CJK text input capability.

**Acceptance Scenarios**:

1. **Given** a TextBox has focus and the user has a Japanese IME active, **When** the user types a romanized sequence (e.g., "nihongo"), **Then** the composition string is displayed inline in the TextBox with visual distinction (underline) and the IME candidate window appears positioned near the text cursor.
2. **Given** an IME candidate list is displayed, **When** the user selects a candidate and presses Enter, **Then** the composed text replaces the composition string in the TextBox and the candidate window closes.
3. **Given** an active composition is in progress, **When** the user presses Escape, **Then** the composition is cancelled and the TextBox reverts to its pre-composition state.
4. **Given** a TextBox contains existing text and the cursor is positioned mid-text, **When** the user begins an IME composition, **Then** the composition string is inserted at the cursor position without disrupting surrounding text.

---

### User Story 2 - TextComposition Events for Developers (Priority: P2)

A developer building a text editor or search-as-you-type feature needs to differentiate between in-progress IME composition and committed text. The application must fire composition lifecycle events (started, changed, ended) so the developer can customize behavior — for example, suppressing search queries while composition is active and only searching on committed text.

**Why this priority**: Many applications need to react differently to composition vs. committed text (search boxes, code editors, chat applications). Without these events, developers cannot build correct IME-aware behavior.

**Independent Test**: Can be tested by subscribing to TextCompositionStarted, TextCompositionChanged, and TextCompositionEnded events on a TextBox, activating an IME, and verifying events fire at the correct lifecycle stages with accurate index and length information.

**Acceptance Scenarios**:

1. **Given** a TextBox with a TextCompositionStarted handler, **When** the user begins an IME composition, **Then** the event fires with the correct StartIndex and Length of the composition region.
2. **Given** an active composition, **When** the user modifies the composition string (e.g., extends it or narrows candidates), **Then** TextCompositionChanged fires with updated StartIndex and Length.
3. **Given** an active composition, **When** the user commits text from the candidate list, **Then** TextCompositionEnded fires with the final StartIndex and Length of the committed text.
4. **Given** a developer suppresses text processing during composition, **When** the composition ends, **Then** the developer can read the final committed text and process it.

---

### User Story 3 - IME Support Across All Platforms (Priority: P2)

An Uno Platform developer expects consistent IME behavior when their application runs on desktop (Windows, macOS, Linux), mobile (Android, iOS), and web (WebAssembly). Each platform must provide IME composition support appropriate to its native input system, and the same application code must work without platform-specific conditionals.

**Why this priority**: Cross-platform consistency is Uno Platform's core value proposition. IME support that only works on one platform undermines developer confidence and forces platform-specific workarounds.

**Independent Test**: Can be tested by running the same Uno application with a TextBox on each supported platform, activating the platform's native IME, and verifying composition and commitment work correctly on each.

**Acceptance Scenarios**:

1. **Given** an Uno application running on Windows (Skia), **When** the user activates a CJK IME and composes text, **Then** the composition string appears inline and the IME candidate window is positioned correctly near the caret.
2. **Given** an Uno application running on macOS or Linux (Skia), **When** the user activates a CJK IME, **Then** composition and candidate selection work correctly using the platform's native IME framework.
3. **Given** an Uno application running in a web browser (WebAssembly), **When** the user activates the browser's IME, **Then** composition is handled correctly through the browser's native input mechanisms.
4. **Given** an Uno application running on Android or iOS, **When** the user uses the platform's built-in IME keyboard, **Then** composition and text commitment work correctly.

---

### Edge Cases

- What happens when the user switches IME languages mid-composition? The active composition should be cancelled or committed (platform-dependent), and the new IME should activate cleanly.
- What happens when focus leaves the TextBox during an active composition? The composition should be committed or cancelled gracefully without data loss or visual artifacts.
- What happens when the TextBox has a MaxLength constraint and the committed text would exceed it? The committed text should be truncated to respect the MaxLength, consistent with WinUI behavior.
- What happens when programmatic text changes occur during an active composition (e.g., data binding update)? The composition should be cancelled to prevent conflicting state.
- What happens with dead-key composition (e.g., accent characters in European languages)? Dead-key sequences must produce the correct composed character (e.g., ´ + e → é).
- What happens when an IME composition occurs in a read-only TextBox? Composition should not be initiated in read-only controls.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: TextBox MUST display IME composition strings inline with visual distinction (underline) during active composition. The composition string MUST be part of TextBox.Text (inserted via ProcessTextInput()), with a tracked composition range identifying the temporary portion. TextChanged MUST fire during composition, consistent with WinUI behavior.
- **FR-002**: The IME candidate window MUST be positioned near the text caret on platforms where the application controls candidate window placement (desktop Skia platforms).
- **FR-003**: TextBox MUST fire TextCompositionStarted, TextCompositionChanged, and TextCompositionEnded events with accurate StartIndex and Length properties during IME composition lifecycle.
- **FR-004**: IME composition MUST work correctly when the cursor is at any position within existing text (beginning, middle, end).
- **FR-005**: IME composition MUST respect TextBox constraints including MaxLength, IsReadOnly, and AcceptsReturn.
- **FR-006**: Focus changes during active composition MUST result in graceful composition commitment or cancellation without data loss or visual artifacts.
- **FR-007**: Dead-key composition sequences (e.g., accent characters) MUST produce correct composed characters on all platforms.
- **FR-008**: IME composition MUST work consistently across Skia desktop (Windows, macOS, Linux), Skia mobile (Android, iOS), and WebAssembly platforms.
- **FR-009**: The TextComposition event args MUST provide StartIndex and Length properties matching the WinUI API contract.

### Key Entities

- **Composition String**: The in-progress text being composed by the IME before commitment. Has a start index, length, and visual state (typically underlined). Exists transiently during active composition.
- **Candidate Window**: The popup UI provided by the operating system's IME displaying possible text candidates for the user to select. Positioned relative to the composition string.
- **TextComposition Event**: Lifecycle event fired during IME composition (started, changed, ended). Contains StartIndex and Length identifying the composition region within the text control's content.
- **InputScope**: Existing Uno Platform property that hints to the IME what type of input is expected (e.g., number, URL, search), influencing candidate suggestions and keyboard layout.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Users can successfully compose and commit CJK text (Chinese, Japanese, Korean) in TextBox controls on all supported platforms without text corruption or visual glitches.
- **SC-002**: TextCompositionStarted, TextCompositionChanged, and TextCompositionEnded events fire correctly during IME composition, matching WinUI behavior for event timing and argument values.
- **SC-003**: IME candidate windows appear within 50 pixels of the text caret position on desktop platforms where application controls placement.
- **SC-004**: 100% of existing TextBox unit and runtime tests continue to pass after IME support is added (no regressions to non-IME text input).
- **SC-005**: Dead-key composition (European accent characters) produces correct output on all platforms.
- **SC-006**: Applications using search-as-you-type patterns can use TextComposition events to distinguish between in-progress composition and committed text, eliminating spurious intermediate queries.

## Clarifications

### Session 2026-03-16

- Q: How should the composition string be reflected in TextBox.Text during active composition? → A: Composition text is part of TextBox.Text — inserted via ProcessTextInput(), tracked by a composition range (startIndex, length). TextChanged fires during composition. Matches WinUI behavior.

## Assumptions

- WinUI 3 (WinAppSDK) serves as the reference implementation for IME behavior and API contracts. Uno's implementation should match WinUI behavior where feasible.
- Each platform's native IME framework will be leveraged rather than building a custom IME system. Platform-specific rendering of candidate windows is acceptable since these are OS-provided UI.
- The existing partial IME infrastructure in Uno (Android composing regions, Win32 caret manager, WebAssembly native input) will be extended rather than replaced.
- Composition visual indicators (underline styling) will follow each platform's native conventions rather than enforcing a single cross-platform visual style.
- The CoreText APIs (Windows.UI.Text.Core namespace) are out of scope for this feature, as they represent a lower-level text services API primarily used by custom text input controls rather than standard TextBox usage.
- RichEditBox, AutoSuggestBox, and other text input controls are out of scope. This feature focuses exclusively on TextBox IME support.
