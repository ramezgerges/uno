# Tasks: macOS IME Composition Events

**Input**: Design documents from `/specs/005-macos-ime/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Not explicitly requested. Test validation via existing TextBox_IME_Debug sample.

**Environment constraint**: This feature requires macOS for building and testing. The native Objective-C code (`UnoNativeMac`) and macOS IME framework are only available on macOS. C# managed code can be authored on any platform but cannot be compiled or tested without macOS.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Project structure preparation (code authoring only — build verification requires macOS)

- [x] T001 Create directory `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/TextBox/` for new managed IME files
- [x] T002 Review existing native macOS keyboard handling in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWindow.m` (sendEvent: method) and rendering views (`UNOMetalViewDelegate.m`, `UNOSoftView.m`) to understand current key event flow

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Native Objective-C infrastructure for NSTextInputClient protocol that ALL user stories depend on

**CRITICAL**: No user story work can begin until this phase is complete

- [x] T003 Add IME callback function pointer typedefs and setter declaration in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWindow.h` — define `ime_insert_text_callback_fn_ptr`, `ime_set_marked_text_callback_fn_ptr`, `ime_unmark_text_callback_fn_ptr`, and `uno_set_ime_callbacks()` function
- [x] T004 Implement `uno_set_ime_callbacks()` function and callback storage in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWindow.m` — store callback function pointers in static variables, add `uno_set_ime_active()` function to toggle IME key event routing
- [x] T005 Adopt `NSTextInputClient` protocol on the Metal rendering view in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOMetalViewDelegate.h` — add protocol conformance declaration, add `_markedText`, `_markedRange`, `_selectedRange`, `_imeActive` instance variables
- [x] T006 Implement `NSTextInputClient` protocol methods on the Metal rendering view in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOMetalViewDelegate.m` — implement `insertText:replacementRange:` (calls `ime_insert_text_callback`), `setMarkedText:selectedRange:replacementRange:` (calls `ime_set_marked_text_callback`), `unmarkText` (calls `ime_unmark_text_callback`), `hasMarkedText`, `markedRange`, `selectedRange`, `validAttributesForMarkedText`, `attributedSubstringForProposedRange:actualRange:`, `characterIndexForPoint:`
- [x] T007 Adopt `NSTextInputClient` protocol on the Software rendering view in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOSoftView.h` — same protocol conformance as Metal view
- [x] T008 Implement `NSTextInputClient` protocol methods on the Software rendering view in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOSoftView.m` — same implementations as Metal view
- [x] T009 Modify key event routing in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWindow.m` — when `_imeActive` is true, call `[self.contentView interpretKeyEvents:@[event]]` for NSEventTypeKeyDown instead of directly extracting unicode and calling the key callback; non-IME path remains unchanged
- [x] T010 Add P/Invoke declarations for IME callbacks in `src/Uno.UI.Runtime.Skia.MacOS/Native/NativeUno.cs` — declare `uno_set_ime_callbacks()` and `uno_set_ime_active()` with matching signatures for the native functions defined in T003/T004
- [x] T011 Add static IME callback handlers in `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Window/MacOSWindowHost.cs` — add `[UnmanagedCallersOnly]` static methods `OnImeInsertText`, `OnImeSetMarkedText`, `OnImeUnmarkText` that bridge to `MacOSImeTextBoxExtension.Instance`; register callbacks via `NativeUno.uno_set_ime_callbacks()` in the initialization path

**Checkpoint**: Native NSTextInputClient infrastructure is in place; managed callbacks are wired up

---

## Phase 3: User Story 1 — CJK Text Composition in TextBox (Priority: P1) MVP

**Goal**: Users can input CJK text via IME composition on Skia macOS with correct preedit display and text commit

**Independent Test**: Launch SamplesApp on macOS, navigate to TextBox_IME_Debug, switch to Pinyin, type "nihao", observe preedit, press Space to commit, verify "你好" appears

### Implementation for User Story 1

- [x] T012 [US1] Create `MacOSImeTextBoxExtension` class implementing `IImeTextBoxExtension` in `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/TextBox/MacOSImeTextBoxExtension.cs` — implement composition state machine: `OnSetMarkedText` fires `CompositionStarted` + `CompositionUpdated` (Idle→Composing) or just `CompositionUpdated` (Composing→Composing); `OnInsertText` fires `CompositionCompleted` + `CompositionEnded` when composing, or is ignored when not composing (direct typing handled by existing key path); `OnUnmarkText` fires `CompositionEnded`; `StartImeSession` stores active TextBox reference and calls `NativeUno.uno_set_ime_active(true)`; `EndImeSession` fires `CompositionEnded` if composing and calls `NativeUno.uno_set_ime_active(false)`; suppress composition for PasswordBox
- [x] T013 [US1] Register `MacOSImeTextBoxExtension` via `ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => MacOSImeTextBoxExtension.Instance)` in `src/Uno.UI.Runtime.Skia.MacOS/Hosting/MacSkiaHost.cs` static constructor
- [ ] T014 [US1] Manual verification on macOS (requires macOS) — run SamplesApp with TextBox_IME_Debug sample, test Pinyin composition: type phonetic keys, verify preedit appears in TextBox, select candidate, verify committed text appears; verify event log shows correct TextCompositionStarted/Changed/Ended sequence

**Checkpoint**: CJK text composition works on macOS — preedit display and commit functional

---

## Phase 4: User Story 2 — TextComposition Events for Developers (Priority: P2)

**Goal**: TextCompositionStarted/Changed/Ended events fire with correct StartIndex and Length values

**Independent Test**: Subscribe to all three composition events, perform Pinyin composition, verify event args match composition region

### Implementation for User Story 2

- [ ] T015 [US2] Verify composition event arguments are correct in `MacOSImeTextBoxExtension` — ensure `ImeCompositionEventArgs.Text` contains the correct preedit text during `CompositionUpdated` and the correct committed text during `CompositionCompleted`; verify `TextCompositionStartedEventArgs`, `TextCompositionChangedEventArgs`, and `TextCompositionEndedEventArgs` have correct `StartIndex` and `Length` by testing with TextBox_IME_Debug sample on macOS
- [ ] T016 [US2] Test edge cases — verify composition cancel (Escape key) fires `CompositionEnded` without `CompositionCompleted`; verify focus loss mid-composition fires `CompositionEnded`; verify `IsReadOnly` TextBox rejects composition; verify `MaxLength` constraint is respected during commit

**Checkpoint**: Developers can rely on TextComposition events with correct arguments on macOS

---

## Phase 5: User Story 3 — IME Candidate Window Positioning (Priority: P2)

**Goal**: The macOS IME candidate window appears near the TextBox caret position

**Independent Test**: Focus TextBox, begin Pinyin composition, verify candidate window appears near the caret, not at bottom of screen

### Implementation for User Story 3

- [x] T017 [US3] Add caret rect callback typedef and setter in `src/Uno.UI.Runtime.Skia.MacOS/UnoNativeMac/UnoNativeMac/UNOWindow.h` — define `ime_get_caret_rect_callback_fn_ptr` that returns screen-space caret rectangle (x, y, width, height)
- [x] T018 [US3] Implement `firstRectForCharacterRange:actualRange:` in Metal and Software rendering views (`UNOMetalViewDelegate.m` and `UNOSoftView.m`) — call the managed caret rect callback to get the caret position, convert to screen coordinates using `[self.window convertRectToScreen:[self convertRect:caretRect toView:nil]]`, return the NSRect
- [x] T019 [US3] Add caret rect P/Invoke declaration in `src/Uno.UI.Runtime.Skia.MacOS/Native/NativeUno.cs` and register the callback in `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Window/MacOSWindowHost.cs`
- [x] T020 [US3] Implement managed caret rect computation in `MacOSImeTextBoxExtension` — compute caret screen position from active TextBox using `ParsedText.GetRectForIndex()` + `TransformToVisual(null)` + `RasterizationScale`, matching the pattern used in Android's `TextInputConnection.GetCursorAnchorInfo()`
- [x] T021 [US3] Send initial caret position when `StartImeSession` is called — proactively compute and cache caret rect so it's available for the first `firstRectForCharacterRange:` query, preventing the first-composition positioning bug seen on Android and X11

**Checkpoint**: IME candidate window appears near the caret position in all cases

---

## Phase 6: User Story 4 — Cross-Platform Consistency (Priority: P3)

**Goal**: macOS composition behavior matches Win32, X11, and Android

**Independent Test**: Run TextBox_IME_Debug sample on macOS and compare event sequences with other platforms

### Implementation for User Story 4

- [ ] T022 [US4] Verify event sequence parity — run TextBox_IME_Debug sample on macOS and compare the TextCompositionStarted → TextCompositionChanged (n times) → TextCompositionEnded event sequence with the sequence produced on X11 and Android for the same Pinyin input "nihao" → "你好"
- [ ] T023 [US4] Verify cancel behavior parity — start composition and press Escape on macOS, verify the TextBox state (text content, caret position) matches what happens on X11/Android when cancelling mid-composition
- [ ] T024 [US4] Handle macOS-specific "press and hold" accent input — verify that macOS press-and-hold (e.g., holding 'a' to get à/á/â options) works correctly through the NSTextInputClient path; this is a macOS-specific composition flow that should produce correct TextComposition events

**Checkpoint**: macOS IME behavior is consistent with all other Skia platforms

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Code quality, edge cases, documentation

- [x] T025 Add trace-level logging to `MacOSImeTextBoxExtension` for all composition state transitions in `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/TextBox/MacOSImeTextBoxExtension.cs` — use `this.Log().Trace()` pattern matching Android and X11 implementations
- [ ] T026 Verify existing non-CJK keyboard input is not regressed — test English typing, backspace, selection, copy/paste, keyboard shortcuts on macOS after IME changes
- [ ] T027 Run quickstart.md validation — execute all 5 test scenarios from `specs/005-macos-ime/quickstart.md` and verify they pass

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — verify build works
- **Foundational (Phase 2)**: Depends on Phase 1 — creates all native infrastructure
- **US1 (Phase 3)**: Depends on Phase 2 — implements managed extension and registration
- **US2 (Phase 4)**: Depends on US1 — validates event correctness
- **US3 (Phase 5)**: Depends on Phase 2 — can run in parallel with US1/US2 (different files)
- **US4 (Phase 6)**: Depends on US1 + US3 — cross-platform validation
- **Polish (Phase 7)**: Depends on all user stories

### User Story Dependencies

- **US1 (P1)**: Depends on Foundational only — core IME composition
- **US2 (P2)**: Depends on US1 — event validation builds on working composition
- **US3 (P2)**: Depends on Foundational only — candidate window positioning is independent of composition state machine
- **US4 (P3)**: Depends on US1 + US3 — parity testing needs both working

### Parallel Opportunities

- T005 + T007 can run in parallel (Metal view + Software view protocol adoption)
- T006 + T008 can run in parallel (Metal view + Software view implementation)
- T003 + T010 can be done together (native header + managed P/Invoke declarations)
- US3 (Phase 5) can start in parallel with US1/US2 (different code paths)

---

## Parallel Example: Phase 2 (Foundational)

```bash
# Parallel group 1: Native header + Managed P/Invoke
Task T003: "Add IME callback typedefs in UNOWindow.h"
Task T010: "Add P/Invoke declarations in NativeUno.cs"

# Parallel group 2: Metal + Software view protocol (after T003)
Task T005: "Adopt NSTextInputClient on Metal view in UNOMetalViewDelegate.h"
Task T007: "Adopt NSTextInputClient on Software view in UNOSoftView.h"

# Parallel group 3: Metal + Software view implementation (after T004, T005, T007)
Task T006: "Implement NSTextInputClient on Metal view in UNOMetalViewDelegate.m"
Task T008: "Implement NSTextInputClient on Software view in UNOSoftView.m"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (verify build)
2. Complete Phase 2: Foundational (native NSTextInputClient + callbacks)
3. Complete Phase 3: US1 (managed extension + registration)
4. **STOP and VALIDATE**: Test CJK composition on macOS with TextBox_IME_Debug
5. This alone delivers the core value: CJK text input works on macOS

### Incremental Delivery

1. Setup + Foundational → Native infrastructure ready
2. Add US1 → CJK composition works → **MVP!**
3. Add US3 → Candidate window positioning → Better UX
4. Add US2 → Event correctness validated → Developer confidence
5. Add US4 → Cross-platform parity confirmed → Full feature complete

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- **All build and test steps require macOS** — native Objective-C compilation (`libUnoNativeMac.dylib`) and macOS IME framework are only available on macOS
- Managed C# code can be authored on any platform but cannot be compiled (the project targets `net10.0-macos14.0`) or tested without macOS
- Tasks marked as verification/testing (T014, T015, T016, T022-T024, T026-T027) must be deferred to a macOS environment
- The existing TextBox_IME_Debug sample works cross-platform — no new sample needed
- Follow singleton pattern (static `Instance`) matching Win32 and X11 implementations
