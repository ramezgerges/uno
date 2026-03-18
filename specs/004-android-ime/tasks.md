# Tasks: Android IME Composition Events

**Input**: Design documents from `/specs/004-android-ime/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup

**Purpose**: Understand existing code and prepare the project for changes

- [ ] T001 Read existing TextInputConnection composition handling in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/TextInputConnection.cs — understand how SetComposingText, CommitText, FinishComposingText flow through DidChangeEditingState
- [ ] T002 Read existing ObservableEditingState composing span tracking in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/ObservableEditingState.cs — understand ComposingStart/ComposingEnd properties and DidChangeEditingState callback
- [ ] T003 Read Win32 reference implementation in src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/TextBox/Win32ImeTextBoxExtension.cs — understand the event firing pattern (CompositionStarted → CompositionUpdated → CompositionCompleted → CompositionEnded)
- [ ] T004 Read TextBox.skia.cs IME integration in src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs — understand how _imeExtension is created via ApiExtensibility.CreateInstance, event subscriptions in InitializePartial(), and the OnImeComposition* methods

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Add the composition state notification mechanism to TextInputConnection

**CRITICAL**: The IME extension cannot work without a way to receive composition state changes from the existing input connection.

- [ ] T005 Add a composition state change callback delegate to TextInputConnection in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/TextInputConnection.cs — add an `Action<int, int, string?, string>?` property (composingStart, composingEnd, composingText, fullText) that external consumers can subscribe to
- [ ] T006 Fire the composition callback from DidChangeEditingState in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/TextInputConnection.cs — when `composingRegionChanged` is true in the existing DidChangeEditingState listener, read ComposingStart/ComposingEnd from _editable, extract composing text substring, and invoke the callback
- [ ] T007 Expose the active TextInputConnection from TextInputPlugin in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/TextInputPlugin.cs — add an internal property to access the current TextInputConnection so AndroidImeTextBoxExtension can subscribe to its composition callback during StartImeSession

**Checkpoint**: TextInputConnection now emits composition state change notifications that an external consumer can subscribe to.

---

## Phase 3: User Story 1 — CJK Text Composition in TextBox (Priority: P1)

**Goal**: Enable CJK text composition on Skia Android so that composing text appears in the TextBox and committed text replaces the composition region.

**Independent Test**: Launch SamplesApp.Skia.netcoremobile on Android emulator, enable Gboard Pinyin, focus a TextBox, type "nihao", select "你好" from candidates, verify text appears correctly.

### Implementation for User Story 1

- [ ] T008 [US1] Create AndroidImeTextBoxExtension implementing IImeTextBoxExtension in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — implement the composition state machine: track _isComposing, _lastComposingStart, _lastComposingEnd; implement OnCompositionStateChanged to detect Idle→Composing (fire CompositionStarted), Composing→Composing (fire CompositionUpdated), Composing→Idle with text change (fire CompositionCompleted + CompositionEnded), Composing→Idle without text change (fire CompositionEnded)
- [ ] T009 [US1] Implement StartImeSession in AndroidImeTextBoxExtension in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — get the active TextInputConnection via UnoSKCanvasView.Instance.TextInputPlugin, subscribe OnCompositionStateChanged to the connection's composition callback
- [ ] T010 [US1] Implement EndImeSession in AndroidImeTextBoxExtension in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — unsubscribe from the TextInputConnection composition callback, if _isComposing is true fire CompositionEnded, reset state
- [ ] T011 [US1] Register AndroidImeTextBoxExtension in AndroidHost.cs in src/Uno.UI.Runtime.Skia.Android/Hosting/AndroidHost.cs — add ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => new AndroidImeTextBoxExtension()) alongside existing extension registrations
- [ ] T012 [US1] Handle PasswordBox suppression in AndroidImeTextBoxExtension in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — in StartImeSession, check if the textBox is a PasswordBox and skip composition event wiring (FR-010)

**Checkpoint**: CJK composition works on Skia Android — composing text appears and committed text replaces it. TextCompositionStarted/Changed/Ended events fire from TextBox.skia.cs.

---

## Phase 4: User Story 2 — TextComposition Events for Developers (Priority: P2)

**Goal**: Ensure TextComposition events fire with correct StartIndex and Length values so developers can use them for custom composition UIs and input validation.

**Independent Test**: Subscribe to all three TextComposition events on a TextBox, perform Pinyin composition "ni" → "你", verify event args contain correct StartIndex matching caret position and Length matching composition/committed text length.

### Implementation for User Story 2

- [ ] T013 [US2] Verify composition text extraction in OnCompositionStateChanged in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — ensure the composingText passed to CompositionUpdated is the correct substring from the editable (composingStart to composingEnd), and that CompositionCompleted receives the committed text (the text that replaced the composing region)
- [ ] T014 [US2] Handle edge case: direct commit without prior composition in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — when CommitText is called without a prior SetComposingText (composing region was never set), fire CompositionStarted → CompositionCompleted → CompositionEnded in sequence, matching X11 OnCommittedText behavior
- [ ] T015 [US2] Handle edge case: focus loss during composition in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — in EndImeSession, if _isComposing, fire CompositionEnded so TextBox.skia.cs clears composition state correctly
- [ ] T016 [US2] Handle edge case: MaxLength interaction in src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs — verify that when TextBox has MaxLength set, the TextBox.skia.cs ProcessTextInput truncation works correctly with the composition text replacement (this should work automatically via existing TextBox.skia.cs logic, but verify)

**Checkpoint**: TextComposition events fire with correct StartIndex and Length. Edge cases (direct commit, focus loss, MaxLength) are handled.

---

## Phase 5: User Story 3 — Cross-Platform Consistency (Priority: P3)

**Goal**: Verify Android composition behavior matches Win32 and X11 implementations.

**Independent Test**: Run the same SamplesApp on Windows/Linux/Android, perform identical Pinyin input, compare TextComposition event sequences.

### Implementation for User Story 3

- [ ] T017 [US3] Extend the debug sample page for Android in src/SamplesApp/UITests.Shared/Windows_UI_Xaml_Controls/TextBox/TextBox_X11_IME_Debug.xaml.cs — update the existing debug page to also work on Android (remove Linux-only guard or add Android detection), show IME backend as "Android BaseInputConnection", log composition events
- [ ] T018 [US3] Verify event sequence parity with Win32 and X11 — manually test on all three platforms: type "nihao" → select "你好", document the exact event sequence (Start, Changed×N, Ended), verify Android matches. If differences found, adjust AndroidImeTextBoxExtension to align

**Checkpoint**: Android IME composition events match Win32 and X11 behavior. Debug sample page works on all platforms.

---

## Phase 6: Testing Environment Setup

**Purpose**: Set up Android emulator with Pinyin IME for manual validation

- [ ] T019 [P] Set up Android emulator with Gboard Pinyin — create AVD using system-images;android-35;google_apis_playstore;x86_64, boot emulator, enable Chinese Pinyin in Gboard settings (Settings → System → Languages & input → Gboard → Languages → Add Chinese Simplified Pinyin)
- [ ] T020 Build and deploy SamplesApp.Skia.netcoremobile to emulator — run `dotnet build src/SamplesApp/SamplesApp.Skia.netcoremobile/SamplesApp.Skia.netcoremobile.csproj -p:UnoTargetFrameworkOverride=net10.0-android -f net10.0-android -t:Install`
- [ ] T021 Run manual validation of all acceptance scenarios — test English input (no regression), Pinyin composition "nihao" → "你好", composition cancel via backspace, focus switch during composition, verify all TextComposition events fire via debug sample page

**Checkpoint**: All acceptance scenarios validated on Android emulator.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final cleanup and documentation

- [ ] T022 [P] Update quickstart.md with actual test results and any adjustments discovered during testing in specs/004-android-ime/quickstart.md
- [ ] T023 Verify non-CJK regression — test English typing, backspace, selection, cut/copy/paste on Android emulator to confirm no regression (SC-003)
- [ ] T024 Code review: ensure AndroidImeTextBoxExtension follows the same patterns as Win32ImeTextBoxExtension and X11ImeTextBoxExtension for consistency

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — read-only research
- **Foundational (Phase 2)**: Depends on Phase 1 understanding — adds notification mechanism to TextInputConnection
- **User Story 1 (Phase 3)**: Depends on Phase 2 — creates and registers AndroidImeTextBoxExtension
- **User Story 2 (Phase 4)**: Depends on Phase 3 — refines edge cases in AndroidImeTextBoxExtension
- **User Story 3 (Phase 5)**: Depends on Phase 3 — cross-platform comparison
- **Testing Environment (Phase 6)**: Can start in parallel with Phase 2-3 (emulator setup is independent)
- **Polish (Phase 7)**: Depends on Phases 3-6

### User Story Dependencies

- **User Story 1 (P1)**: Depends on Foundational (Phase 2) only — core composition bridging
- **User Story 2 (P2)**: Depends on US1 completion — refines the same file with edge case handling
- **User Story 3 (P3)**: Depends on US1 completion — cross-platform validation and debug page updates

### Parallel Opportunities

- T001-T004 (read-only setup tasks) can all run in parallel
- T019 (emulator setup) can run in parallel with Phase 2-3 implementation
- T022 (quickstart update) can run in parallel with T023-T024

---

## Parallel Example: Phase 1 (Setup)

```bash
# All read-only — can run simultaneously:
Task T001: "Read TextInputConnection.cs"
Task T002: "Read ObservableEditingState.cs"
Task T003: "Read Win32ImeTextBoxExtension.cs"
Task T004: "Read TextBox.skia.cs IME integration"
```

## Parallel Example: Emulator + Implementation

```bash
# These can run in parallel:
Task T019: "Set up Android emulator with Gboard Pinyin"  # Independent of code changes
Task T005-T007: "Add composition callback to TextInputConnection"  # Code changes
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (read existing code)
2. Complete Phase 2: Foundational (add composition callback to TextInputConnection)
3. Complete Phase 3: User Story 1 (create AndroidImeTextBoxExtension, register, verify)
4. **STOP and VALIDATE**: Test CJK composition on Android emulator
5. If working: CJK text input is functional — MVP delivered

### Incremental Delivery

1. Setup + Foundational → Notification mechanism ready
2. User Story 1 → CJK composition works → MVP
3. User Story 2 → Edge cases handled → Production-quality
4. User Story 3 → Cross-platform parity verified → Feature complete
5. Polish → Documentation, regression testing → Ship-ready

---

## Notes

- This is a small, focused feature: 1 new file, 2 modified files, 1 registration line
- The core implementation (T005-T012) is estimated at ~200 lines of new code
- Manual testing with a real CJK keyboard is required — adb `input text` bypasses InputConnection
- The existing TextBox.skia.cs composition machinery (ReplaceCompositionText, OnImeComposition*) handles all the TextBox-side logic; the extension only needs to fire the right events at the right time
