# Tasks: iOS Skia IME Composition Support

**Input**: Design documents from `/specs/006-ios-ime/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, quickstart.md

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup

**Purpose**: Extension registration and interface preparation

- [X] T001 Register IImeTextBoxExtension in src/Uno.UI.Runtime.Skia.AppleUIKit/Hosting/ExtensionsRegistrar.cs (add ApiExtensibility.Register call for AppleUIKitImeTextBoxExtension.Instance)
- [X] T002 Add IsComposing property to IInvisibleTextBoxView interface in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/IInvisibleTextBoxView.cs

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core IME extension that all user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

- [X] T003 Create AppleUIKitImeTextBoxExtension singleton implementing IImeTextBoxExtension in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/AppleUIKitImeTextBoxExtension.cs — include StartImeSession (store active TextBox, skip PasswordBox), EndImeSession (commit active composition, clear state), composition state fields (_isComposing, _lastComposingText), and event firing methods (OnSetMarkedText, OnInsertText, OnUnmarkText) following the macOS MacOSImeTextBoxExtension pattern

**Checkpoint**: Foundation ready — extension registered and core class created

---

## Phase 3: User Story 1 — CJK Composition Input in TextBox (Priority: P1)

**Goal**: Users can type CJK characters via composition (SetMarkedText → candidate selection → InsertText) in any TextBox

**Independent Test**: Focus a TextBox, switch to CJK keyboard, type phonetic keys, verify composition text appears with underline, select candidate, confirm committed text inserted correctly

### Implementation for User Story 1

- [X] T004 [P] [US1] Override SetMarkedText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxView.cs — extract marked text string, forward to AppleUIKitImeTextBoxExtension.OnSetMarkedText, call base implementation, implement IsComposing property delegating to extension
- [X] T005 [P] [US1] Override SetMarkedText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxView.cs — same overrides as SinglelineInvisibleTextBoxView, implement IsComposing property delegating to extension
- [X] T006 [US1] Override InsertText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxView.cs — call base implementation, then forward to AppleUIKitImeTextBoxExtension.OnInsertText to fire CompositionCompleted+Ended (or Started+Completed+Ended for non-composing direct input)
- [X] T007 [US1] Override InsertText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxView.cs — same override as SinglelineInvisibleTextBoxView
- [X] T008 [P] [US1] Override UnmarkText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxView.cs — forward to AppleUIKitImeTextBoxExtension.OnUnmarkText to fire CompositionEnded, call base implementation
- [X] T009 [P] [US1] Override UnmarkText in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxView.cs — same override as SinglelineInvisibleTextBoxView
- [X] T010 [US1] Suppress ProcessNativeTextInput during composition in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/InvisibleTextBoxViewExtension.cs — add early return when _nativeView.IsComposing is true; after composition completes, sync managed TextBox text back to native view via SetTextNative

**Checkpoint**: CJK composition input fully functional — composition text appears with underline, candidate selection commits text

---

## Phase 4: User Story 2 — Non-Composition Key Passthrough During IME (Priority: P2)

**Goal**: Arrow keys, backspace, tab, Enter work normally when no composition is active, even with CJK keyboard enabled

**Independent Test**: Focus TextBox with CJK keyboard, type and commit text, use arrow keys to navigate, backspace to delete, tab to change focus — all should work normally

### Implementation for User Story 2

- [X] T011 [P] [US2] Add composition-aware validation in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxDelegate.cs — in ShouldChangeCharacters, bypass MaxLength check when IsComposing is true to allow composition commits through; ensure non-composition text changes continue normal validation
- [X] T012 [P] [US2] Add composition-aware validation in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxDelegate.cs — in ShouldChangeText, bypass MaxLength check when IsComposing is true; ensure non-composition text changes continue normal validation
- [X] T013 [US2] Handle focus loss during composition in AppleUIKitImeTextBoxExtension.EndImeSession in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/AppleUIKitImeTextBoxExtension.cs — if _isComposing is true when EndImeSession is called, fire CompositionEnded to clean up state; ensure PasswordBox and read-only TextBox skip IME activation in StartImeSession

**Checkpoint**: Non-composition keys pass through normally; focus loss commits/cancels composition cleanly

---

## Phase 5: User Story 3 — Candidate Window Positioning at Caret (Priority: P3)

**Goal**: System keyboard candidate bar and third-party keyboards can query correct caret position for auxiliary UI placement

**Independent Test**: Focus TextBox, start composition, verify system keyboard candidate bar appears; for geometry verification use accessibility inspector or third-party keyboard

### Implementation for User Story 3

- [X] T014 [US3] Implement UpdateCaretPosition in src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/AppleUIKitImeTextBoxExtension.cs — read caret rect from the active TextBox's TextBoxView display block, convert to screen coordinates using iOS coordinate system; call from StartImeSession and after each CompositionUpdated

**Checkpoint**: Caret rectangle accurately reported for candidate window positioning

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Validation and cleanup

- [X] T015 Build validation — run `dotnet build src/Uno.UI-Skia-only.slnf --no-restore` from src/ to verify shared code compiles with the new extension registered
- [X] T016 Code review — verify all composition state transitions match the data-model.md state machine (Idle→Composing→Idle) and the event sequence matches macOS/X11/Win32/Android implementations

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 (T001, T002) — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Phase 2 (T003) — core composition flow
- **US2 (Phase 4)**: Depends on Phase 2 (T003) — can run in parallel with US1
- **US3 (Phase 5)**: Depends on Phase 2 (T003) — can run in parallel with US1 and US2
- **Polish (Phase 6)**: Depends on all user stories complete

### Within Each User Story

- T004 and T005 (SetMarkedText overrides) can run in parallel [P] — different files
- T006 depends on T004 (same file: SinglelineInvisibleTextBoxView.cs)
- T007 depends on T005 (same file: MultilineInvisibleTextBoxView.cs)
- T008 and T009 (UnmarkText overrides) can run in parallel [P] — different files
- T010 depends on T004-T009 (needs IsComposing flag to be available)
- T011 and T012 (delegate changes) can run in parallel [P] — different files
- T013 depends on T003 (modifies the extension created in foundational phase)

### Parallel Opportunities

- T004 + T005 (SetMarkedText on singleline + multiline) — different files
- T008 + T009 (UnmarkText on singleline + multiline) — different files
- T011 + T012 (delegate updates on singleline + multiline) — different files
- US1, US2, US3 can proceed in parallel after Phase 2

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001, T002)
2. Complete Phase 2: Foundational (T003)
3. Complete Phase 3: US1 (T004–T010)
4. **STOP and VALIDATE**: Build with Skia desktop to verify compilation
5. CJK composition input works end-to-end (requires iOS device for full validation)

### Incremental Delivery

1. Setup + Foundational → Extension registered, core class ready
2. US1 → CJK composition works → MVP complete
3. US2 → Non-composition keys pass through correctly
4. US3 → Caret positioning for candidate window
5. Polish → Build validation, code review

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Build environment is Linux — no iOS SDK available; validation is Skia desktop compile + code review
- End-to-end IME testing requires macOS + iOS Simulator (manual validation by maintainer)
- The macOS implementation (MacOSImeTextBoxExtension.cs) is the primary reference for composition state machine and event patterns
- The Android implementation (AndroidImeTextBoxExtension.cs) is a secondary reference for the hidden text input proxy pattern
