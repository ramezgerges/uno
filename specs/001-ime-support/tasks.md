# Tasks: IME Support for TextBox

**Input**: Design documents from `/specs/001-ime-support/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Runtime tests included as they are specified in the plan (Phase 5) and required by constitution Principle III.

**Organization**: Tasks grouped by user story. US3 (cross-platform) is deferred — other platforms proceed only after user verifies Win32 implementation.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2)
- Include exact file paths in descriptions

---

## Phase 1: Setup

**Purpose**: Verify build environment and understand existing code

- [X] T001 Verify `src/crosstargeting_override.props` is set to `net10.0` and `dotnet build src/Uno.UI-Skia-only.slnf` succeeds
- [X] T002 Read existing generated stubs to understand WinUI API contract: `src/Uno.UI/Generated/3.0.0.0/Microsoft.UI.Xaml.Controls/TextCompositionStartedEventArgs.cs`, `TextCompositionChangedEventArgs.cs`, `TextCompositionEndedEventArgs.cs`, and the TextComposition events on generated `TextBox.cs`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Create the platform abstraction interface and implement WinUI event args classes that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T003 [P] Create `IImeTextBoxExtension` interface in `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/IImeTextBoxExtension.skia.cs` — define `StartImeSession(TextBox textBox)`, `EndImeSession()`, `UpdateCaretPosition(int x, int y)`, and events `CompositionStarted`, `CompositionUpdated`, `CompositionCompleted`, `CompositionEnded` per the contract in `specs/001-ime-support/contracts/IImeTextBoxExtension.cs`
- [X] T004 [P] Create `ImeCompositionEventArgs` in `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/ImeCompositionEventArgs.skia.cs` — `EventArgs` subclass with `string Text` property
- [X] T005 [P] Implement `TextCompositionStartedEventArgs` in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionStartedEventArgs.cs` — copy from `src/Uno.UI/Generated/3.0.0.0/Microsoft.UI.Xaml.Controls/TextCompositionStartedEventArgs.cs`, remove `[Uno.NotImplemented]` for `__SKIA__`, add internal constructor `(int startIndex, int length)` and backing fields for `StartIndex` and `Length` properties
- [X] T006 [P] Implement `TextCompositionChangedEventArgs` in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionChangedEventArgs.cs` — same pattern as T005 but from `TextCompositionChangedEventArgs.cs` generated stub
- [X] T007 [P] Implement `TextCompositionEndedEventArgs` in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionEndedEventArgs.cs` — same pattern as T005 but from `TextCompositionEndedEventArgs.cs` generated stub
- [X] T008 Add real `TextCompositionStarted`, `TextCompositionChanged`, `TextCompositionEnded` event declarations to TextBox, replacing the generated stubs. Update `#if` directives in the generated `src/Uno.UI/Generated/3.0.0.0/Microsoft.UI.Xaml.Controls/TextBox.cs` to exclude `__SKIA__` for these three events. Add the real event declarations and private `RaiseTextComposition*` helper methods in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs`
- [X] T009 Build `src/Uno.UI-Skia-only.slnf` and run `dotnet test src/Uno.UI/Uno.UI.Tests.csproj` to verify no regressions

**Checkpoint**: Foundation ready — IImeTextBoxExtension interface defined, TextComposition event args implemented, TextBox exposes real composition events. User story implementation can now begin.

---

## Phase 3: User Story 1 — CJK Text Composition in TextBox (Priority: P1) 🎯 MVP

**Goal**: Enable CJK text composition via IME in TextBox on Win32 Skia — composition string displayed inline with underline, candidate window positioned near caret, commit/cancel work correctly.

**Independent Test**: Activate Japanese IME on Windows, type "nihongo" in a TextBox in SamplesApp, verify hiragana appears inline with underline, press Space for candidates, press Enter to commit kanji.

### Implementation for User Story 1

- [X] T010 [US1] Add composition state fields to `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs`: `_isComposing` (bool), `_compositionStartIndex` (int), `_compositionLength` (int), `_compositionText` (string), `_originalTextBeforeComposition` (string), `_originalSelectionStart` (int). Add a static `_imeExtension` field of type `IImeTextBoxExtension` resolved via `ApiExtensibility.CreateInstance()` in the existing `InitializePartial()` method.
- [X] T011 [US1] Implement composition lifecycle methods in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs`: `OnImeCompositionStarted()` — saves original text/selection, sets `_isComposing = true`, `_compositionStartIndex = SelectionStart`, fires `TextCompositionStarted`. `OnImeCompositionUpdated(string compositionText)` — builds new text `Text[..compositionStart] + compositionText + Text[compositionStart+compositionLength..]`, calls `ProcessTextInput(newText)`, updates `_compositionLength`, fires `TextCompositionChanged`. `OnImeCompositionCompleted(string committedText)` — replaces composition region with committed text via `ProcessTextInput()`, fires `TextCompositionEnded`, clears state. `OnImeCompositionEnded()` — if still composing (cancel), reverts to `_originalTextBeforeComposition` via `ProcessTextInput()`, clears state.
- [X] T012 [US1] Wire `IImeTextBoxExtension` events in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs`: subscribe to `_imeExtension.CompositionStarted/Updated/Completed/Ended` events in `InitializePartial()` or on first focus. In focus handling, call `_imeExtension.StartImeSession(this)` on focus gained and `_imeExtension.EndImeSession()` on focus lost. In selection change, call `_imeExtension.UpdateCaretPosition(x, y)`.
- [X] T013 [US1] Modify `OnKeyDownSkia()` in `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs` to skip normal character insertion (the `default:` case with `args.UnicodeKey`) when `_isComposing` is true. The platform extension handles text updates via `OnImeCompositionUpdated` → `ProcessTextInput()` instead.
- [X] T014 [US1] Create `Win32ImeTextBoxExtension` singleton in `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/TextBox/Win32ImeTextBoxExtension.cs` — implements `IImeTextBoxExtension`. Holds `_isComposing` flag. `StartImeSession` stores the active HWND (obtained from the TextBox's XamlRoot → Win32WindowWrapper). `EndImeSession` clears state. `UpdateCaretPosition` delegates to the existing `Win32ImeCaretManager`. Exposes `CompositionStarted`, `CompositionUpdated`, `CompositionCompleted`, `CompositionEnded` events. Provides methods `OnWmImeStartComposition()`, `OnWmImeComposition(LPARAM lParam)`, `OnWmImeEndComposition()` called from the window proc.
- [X] T015 [US1] Implement `OnWmImeComposition` in `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/TextBox/Win32ImeTextBoxExtension.cs` — use `ImmGetContext(hwnd)` to get IME context. If `lParam` has `GCS_RESULTSTR` flag, call `ImmGetCompositionString(himc, GCS_RESULTSTR)` to extract committed text and raise `CompositionCompleted`. If `lParam` has `GCS_COMPSTR` flag, call `ImmGetCompositionString(himc, GCS_COMPSTR)` to extract composition string and raise `CompositionUpdated`. Release context with `ImmReleaseContext`.
- [X] T016 [US1] Register `Win32ImeTextBoxExtension` in `src/Uno.UI.Runtime.Skia.Win32/Hosting/Win32Host.cs` static constructor: `ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => Win32ImeTextBoxExtension.Instance);`
- [X] T017 [US1] Add WM_IME_* message handling in `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Window/Win32WindowWrapper.cs` `WndProcInner()`: handle `WM_IME_STARTCOMPOSITION` (call extension `OnWmImeStartComposition()`, return 0 to suppress default composition window), `WM_IME_COMPOSITION` (call extension `OnWmImeComposition(lParam)`, return 0), `WM_IME_ENDCOMPOSITION` (call extension `OnWmImeEndComposition()`, return 0). Route messages through `Win32ImeTextBoxExtension.Instance`.
- [X] T018 [US1] Modify `OnKey()` in `src/Uno.UI.Runtime.Skia.Win32/Devices/Input/Win32WindowWrapper.Keyboard.cs` — when `Win32ImeTextBoxExtension.Instance.IsComposing` is true, skip the `PeekMessage` for `WM_CHAR` (or consume and discard it) to prevent double text insertion. The committed text is already handled by `GCS_RESULTSTR` in the extension.
- [X] T019 [US1] Add composition range properties to `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBoxView.skia.cs`: `CompositionStartIndex` (int) and `CompositionLength` (int), set by TextBox during `OnImeCompositionUpdated`. Pass these values through to the `UnicodeText.Draw()` call.
- [X] T020 [US1] Modify `Draw()` in `src/Uno.UI/UI/Xaml/Documents/UnicodeText.skia.cs` to accept a composition range parameter (start index, length). In the cluster iteration loop, check if each cluster falls within the composition range. For composition clusters, draw a straight underline at `y + line.baselineOffset + yOffset` using `SKCanvas.DrawLine()` in the text foreground color. Follow the same pattern as the existing spell-check wavy underline rendering.
- [X] T021 [US1] Ensure `OnImeCompositionUpdated` in `TextBox.skia.cs` invalidates the TextBoxView (calls `InvalidateVisual()` or equivalent) to trigger re-render showing/updating the composition underline.
- [X] T022 [US1] Build `src/Uno.UI-Skia-only.slnf` and run `dotnet test src/Uno.UI/Uno.UI.Tests.csproj` to verify no regressions in existing TextBox tests.

**Checkpoint**: CJK text composition works end-to-end on Win32 Skia. User can type with Japanese/Chinese/Korean IME, see inline composition with underline, and commit/cancel. **STOP AND VALIDATE with user before proceeding.**

---

## Phase 4: User Story 2 — TextComposition Events for Developers (Priority: P2)

**Goal**: Verify TextCompositionStarted/Changed/Ended events fire with correct StartIndex and Length values matching WinUI API contract. Add runtime tests and a SamplesApp page for validation.

**Independent Test**: Subscribe to all three TextComposition events on a TextBox, activate IME, compose and commit text, verify events fire at correct lifecycle points with accurate StartIndex and Length.

**Depends on**: US1 (composition infrastructure must be functional)

### Implementation for User Story 2

- [ ] T023 [US2] Create runtime test file `src/Uno.UI.RuntimeTests/Tests/Windows_UI_Xaml_Controls/Given_TextBox_Ime.cs` — test composition state management by simulating composition events through `IImeTextBoxExtension` (mock or test implementation). Tests: (1) TextCompositionStarted fires with correct StartIndex when composition begins, (2) TextCompositionChanged fires with updated StartIndex and Length on each composition update, (3) TextCompositionEnded fires with final StartIndex and Length on commit, (4) Composition cancel reverts Text to original value, (5) Focus loss during composition commits or cancels gracefully, (6) IsReadOnly TextBox rejects composition, (7) MaxLength constraint is respected during composition commit.
- [ ] T024 [US2] Create SamplesApp XAML page `src/SamplesApp/UITests.Shared/Windows_UI_Xaml_Controls/TextBox/TextBox_Ime.xaml` — TextBox with event handlers for TextCompositionStarted/Changed/Ended that display event details (StartIndex, Length, current Text) in TextBlocks below. Add `[Sample("TextBox", "IME")]` attribute to the code-behind class.
- [ ] T025 [US2] Create code-behind `src/SamplesApp/UITests.Shared/Windows_UI_Xaml_Controls/TextBox/TextBox_Ime.xaml.cs` — wire up TextCompositionStarted/Changed/Ended handlers that update status TextBlocks with event args details.
- [ ] T026 [US2] Register the sample in `src/SamplesApp/UITests.Shared/UITests.Shared.projitems` — add both `<Page>` entry for `TextBox_Ime.xaml` and `<Compile>` entry for `TextBox_Ime.xaml.cs` with `<DependentUpon>`.
- [ ] T027 [US2] Build and run runtime tests: `dotnet build src/SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj -c Release -f net10.0` then `dotnet SamplesApp.Skia.Generic.dll --runtime-tests=test-results.xml` — verify all IME tests pass.

**Checkpoint**: TextComposition events verified via automated tests and manual SamplesApp sample. Event args contain correct StartIndex and Length values matching WinUI contract.

---

## Phase 5: User Story 3 — IME Support Across All Platforms (Priority: P2) ⏸️ DEFERRED

**Goal**: Implement `IImeTextBoxExtension` for macOS, Linux, WebAssembly, Android, and iOS platforms.

**⚠️ DEFERRED**: This phase proceeds ONLY after user verifies the Win32 implementation from US1. Each platform is implemented and verified sequentially.

**Sequence** (each sub-phase is a separate implementation cycle):

### Sub-phase 5a: macOS (Skia)
- [ ] T028 [US3] Create macOS `IImeTextBoxExtension` implementation using NSTextInputClient protocol in `src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/TextBox/MacOSImeTextBoxExtension.cs`
- [ ] T029 [US3] Register in macOS host and test with macOS Japanese IME

### Sub-phase 5b: Linux (Skia)
- [ ] T030 [US3] Create Linux `IImeTextBoxExtension` implementation using IBus/Fcitx integration in `src/Uno.UI.Runtime.Skia.Linux/UI/Xaml/Controls/TextBox/LinuxImeTextBoxExtension.cs`
- [ ] T031 [US3] Register in Linux host and test with IBus Japanese IME

### Sub-phase 5c: WebAssembly
- [ ] T032 [US3] Create WebAssembly `IImeTextBoxExtension` implementation using browser `compositionstart`/`compositionupdate`/`compositionend` DOM events in `src/Uno.UI.Runtime.Skia.WebAssembly.Browser/UI/Xaml/Controls/TextBox/BrowserImeTextBoxExtension.cs`
- [ ] T033 [US3] Register in browser host and test with Chrome/Firefox Japanese IME

### Sub-phase 5d: Android (Skia)
- [ ] T034 [US3] Create Android `IImeTextBoxExtension` implementation extending existing `ObservableEditingState` composing region tracking in `src/Uno.UI.Runtime.Skia.Android/UI/Xaml/Controls/TextBox/AndroidImeTextBoxExtension.cs`
- [ ] T035 [US3] Register in Android host and test with Android Japanese keyboard

### Sub-phase 5e: iOS (Skia)
- [ ] T036 [US3] Create iOS `IImeTextBoxExtension` implementation using UITextInput protocol in `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/AppleUIKitImeTextBoxExtension.cs`
- [ ] T037 [US3] Register in iOS host and test with iOS Japanese keyboard

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final cleanup after all implemented platforms are verified

- [ ] T038 [P] Verify all existing TextBox runtime tests still pass (no regressions)
- [ ] T039 [P] Run quickstart.md validation steps end-to-end
- [ ] T040 Code review for edge cases: MaxLength during composition, programmatic Text changes during composition, IME language switch mid-composition

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — verify build environment
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **US1 (Phase 3)**: Depends on Foundational — core Win32 IME implementation
- **US2 (Phase 4)**: Depends on US1 — events need working composition infrastructure
- **US3 (Phase 5)**: DEFERRED — each sub-phase depends on user verification of previous platform
- **Polish (Phase 6)**: Depends on all implemented user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Depends on Phase 2 (Foundational). No other story dependencies. **This is the MVP.**
- **User Story 2 (P2)**: Depends on US1 (composition state must be functional to test events)
- **User Story 3 (P2)**: Deferred. Each platform depends on user verifying the previous one.

### Within Each User Story

- Interface/event args (Foundational) before composition state (US1)
- Composition state management (T010-T013) before Win32 platform implementation (T014-T018)
- Win32 implementation before underline rendering (T019-T021)
- All US1 tasks before US2 tests/sample

### Parallel Opportunities

- T003, T004, T005, T006, T007 (Foundational): All create different files, can run in parallel
- T010 + T014: Composition state fields and Win32 extension are in different projects, can start in parallel once Foundation is done
- T024, T025, T026 (US2 sample): XAML, code-behind, and projitems registration can be done together
- T038, T039 (Polish): Independent validation tasks

---

## Parallel Example: Foundational Phase

```
# Launch all foundational tasks together (different files):
Task T003: "Create IImeTextBoxExtension interface in src/Uno.UI/.../IImeTextBoxExtension.skia.cs"
Task T004: "Create ImeCompositionEventArgs in src/Uno.UI/.../ImeCompositionEventArgs.skia.cs"
Task T005: "Implement TextCompositionStartedEventArgs in src/Uno.UI/.../TextCompositionStartedEventArgs.cs"
Task T006: "Implement TextCompositionChangedEventArgs in src/Uno.UI/.../TextCompositionChangedEventArgs.cs"
Task T007: "Implement TextCompositionEndedEventArgs in src/Uno.UI/.../TextCompositionEndedEventArgs.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (verify build)
2. Complete Phase 2: Foundational (interface + event args)
3. Complete Phase 3: User Story 1 (Win32 IME composition)
4. **STOP AND VALIDATE**: User tests Win32 IME manually
5. If verified → proceed to US2 (events + tests)

### Incremental Delivery

1. Setup + Foundational → Interface and event args ready
2. US1 (Win32 composition) → Test with Japanese IME → **User verification checkpoint**
3. US2 (Events + tests) → Runtime tests + SamplesApp sample
4. US3 platforms (one at a time) → Each verified before next

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- US3 is explicitly deferred per user instruction — Win32 first, other platforms only after verification
- The `IImeTextBoxExtension` singleton pattern means only one TextBox can have active composition at a time (matches system behavior)
- Dead-key composition (European accents) should work via existing WM_CHAR handling — verify during US1 testing
- Commit after each task or logical group
