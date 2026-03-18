# Tasks: D-Bus IME Support for X11

**Input**: Design documents from `/specs/003-debug-x11-ime/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Not explicitly requested in spec. Headless validation with SamplesApp + Xvfb + fcitx5 is the primary testing strategy (documented in quickstart.md).

**Organization**: Tasks are grouped by user story. The spec's user stories (diagnostic logging, debug sample page, event sequence docs) are reframed around the D-Bus IME implementation since the plan has evolved from "debug XIM" to "implement D-Bus IME".

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

All paths relative to repo root `/uno/`:
- **Source**: `src/Uno.UI.Runtime.Skia.X11/`
- **IME directory**: `src/Uno.UI.Runtime.Skia.X11/IME/` (new)
- **D-Bus XML**: `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/`
- **SamplesApp**: `src/SamplesApp/UITests.Shared/`
- **Specs**: `specs/003-debug-x11-ime/`
- **Avalonia reference**: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/`

---

## Phase 1: Setup

**Purpose**: Add D-Bus XML interface definitions and verify Tmds.DBus.Generator produces proxy classes.

- [ ] T001 [P] Create IBus D-Bus XML interface definition in `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.freedesktop.IBus.Portal.xml` — define `org.freedesktop.IBus.Portal` interface with `CreateInputContext(string) → ObjectPath` method. Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusXml/org.freedesktop.IBus.Portal.xml`
- [ ] T002 [P] Create IBus InputContext D-Bus XML interface definition in `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.freedesktop.IBus.InputContext.xml` — define `org.freedesktop.IBus.InputContext` with methods (`ProcessKeyEvent`, `SetCursorLocation`, `FocusIn`, `FocusOut`, `Reset`, `SetCapabilities`) and signals (`CommitText`, `ForwardKeyEvent`, `UpdatePreeditText`, `ShowPreeditText`, `HidePreeditText`). Reference: Avalonia's XML definitions
- [ ] T003 [P] Create Fcitx5 D-Bus XML interface definitions in `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.fcitx.Fcitx.InputMethod1.xml` and `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.fcitx.Fcitx.InputContext1.xml` — define `org.fcitx.Fcitx.InputMethod1` (CreateInputContext) and `org.fcitx.Fcitx.InputContext1` (ProcessKeyEvent, SetCursorRect, FocusIn/Out, signals). Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusXml/`
- [ ] T004 [P] Create Fcitx4 legacy D-Bus XML interface definitions in `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.fcitx.Fcitx.InputMethod.xml` and `src/Uno.UI.Runtime.Skia.X11/dbus-interfaces/org.fcitx.Fcitx.InputContext.xml` — define `org.fcitx.Fcitx.InputMethod` (CreateICv3) and `org.fcitx.Fcitx.InputContext` (ProcessKeyEvent with int type param, signals). Reference: Avalonia's XML definitions
- [ ] T005 Register all 6 D-Bus XML files as `AdditionalFiles` with `GenerateDBusTypes="true"` in `src/Uno.UI.Runtime.Skia.X11/Uno.UI.Runtime.Skia.X11.csproj` under the existing `dbus-interfaces` ItemGroup, using namespace `Uno.WinUI.Runtime.Skia.X11.DBus`
- [ ] T006 Build `src/Uno.UI.Runtime.Skia.X11/Uno.UI.Runtime.Skia.X11.csproj` and verify generated proxy classes compile. Fix any XML definition issues.

**Checkpoint**: D-Bus proxy classes generated and accessible in code.

---

## Phase 2: Foundational (Core Interface + Detection)

**Purpose**: Create the `IX11InputMethod` abstraction and environment variable detection that all backends depend on.

**⚠️ CRITICAL**: No D-Bus IME backend can be implemented until this phase is complete.

- [ ] T007 Create `IX11InputMethod` interface in `src/Uno.UI.Runtime.Skia.X11/IME/IX11InputMethod.cs` per contract in `specs/003-debug-x11-ime/contracts/IX11InputMethod.cs` — `IsEnabled`, `HandleKeyEventAsync`, `SetCursorLocation`, `SetFocus`, `Reset`, `Dispose`, `Commit`/`ForwardKey`/`PreeditChanged` events
- [ ] T008 Create `X11InputMethodDetector` in `src/Uno.UI.Runtime.Skia.X11/IME/X11InputMethodDetector.cs` — check `UNO_IM_MODULE` → `GTK_IM_MODULE` → `QT_IM_MODULE` → `XMODIFIERS` (parse `@im=<name>` pattern), return `"ibus"`, `"fcitx"`, `"fcitx5"`, `"none"`, or `null`. Factory method `DetectAndCreate()` returns `IX11InputMethod?`. Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/X11DBusImeHelper.cs`
- [ ] T009 Create `DBusInputMethodBase` abstract class in `src/Uno.UI.Runtime.Skia.X11/IME/DBusInputMethodBase.cs` — shared D-Bus session bus connection management, `NameOwnerChanged` monitoring for service lifecycle, async call queue, `Commit`/`ForwardKey`/`PreeditChanged` event plumbing, diagnostic logging (gated behind `LogLevel.Trace`). Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/DBusTextInputMethodBase.cs` (if it exists) or inline in IBus/Fcitx implementations

**Checkpoint**: Foundation ready — D-Bus IME backends can now be implemented.

---

## Phase 3: User Story 1 — IBus D-Bus Client (Priority: P1) 🎯 MVP

**Goal**: Implement IBus D-Bus client so CJK text input works on Ubuntu/Fedora/GNOME (majority of Linux desktop users). Includes diagnostic logging (FR-001 through FR-004, FR-006, FR-007).

**Independent Test**: Start SamplesApp in headless Xvfb + IBus environment, focus TextBox, type Pinyin "ni" and commit — Chinese character 你 appears in TextBox.

### Implementation for User Story 1

- [ ] T010 [US1] Implement `IBusInputMethod` in `src/Uno.UI.Runtime.Skia.X11/IME/IBusInputMethod.cs` — connect to `org.freedesktop.portal.IBus` service, `CreateInputContext`, subscribe to `CommitText`/`ForwardKeyEvent`/`UpdatePreeditText`/`ShowPreeditText`/`HidePreeditText` signals, implement `ProcessKeyEvent`/`SetCursorLocation`/`FocusIn`/`FocusOut`/`Reset`/`SetCapabilities`. Extract commit text from IBus variant struct (index 2). Map X11 modifier masks to IBus modifier masks (`ReleaseMask = 1 << 30`). Add trace logging for all D-Bus calls and signal callbacks. Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/IBus/IBusX11TextInputMethod.cs`
- [ ] T011 [US1] Wire `IBusInputMethod` into `X11InputMethodDetector.DetectAndCreate()` in `src/Uno.UI.Runtime.Skia.X11/IME/X11InputMethodDetector.cs` — when detected IME is "ibus", create and return `IBusInputMethod`
- [ ] T012 [US1] Modify `X11KeyboardInputSource.ProcessKeyboardEvent()` in `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs` — at initialization, call `X11InputMethodDetector.DetectAndCreate()`. When D-Bus IME is available, route KeyPress/KeyRelease through `IX11InputMethod.HandleKeyEventAsync()` before the existing XIM path. If D-Bus IME handles the event, skip XIM/KeyDown dispatch. Wire `Commit` event to `X11ImeTextBoxExtension.OnCommittedText()` via `QueueAction`. Wire `ForwardKey` event back into `ProcessKeyboardEvent()`. Add trace logging for routing decisions (FR-004).
- [ ] T013 [US1] Modify `X11ImeTextBoxExtension` in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs` — when D-Bus IME is available, delegate `SetCursorLocation` to `IX11InputMethod` instead of XIM `XSetICValues`/`XVaCreateNestedList`. Add `OnPreeditChanged()` handler for `PreeditChanged` event. Add trace logging for composition state transitions (FR-003) and IME session start/end with backend type (FR-006).
- [ ] T014 [US1] Modify `X11XamlRootHost.x11events.cs` in `src/Uno.UI.Runtime.Skia.X11/Hosting/X11XamlRootHost.x11events.cs` — make `XFilterEvent` call conditional: only call when D-Bus IME is NOT active (XIM fallback mode). When D-Bus IME is active, skip `XFilterEvent` entirely and pass all KeyPress/KeyRelease directly to `_keyboardSource`. Add trace logging for event routing (FR-001).
- [ ] T015 [US1] Headless validation: Set up Xvfb + dbus-launch + ibus-daemon + SamplesApp, verify English typing works and Pinyin commit delivers Chinese text via D-Bus IBus `CommitText` signal. Verify trace logs show complete event flow. Use environment from `specs/003-debug-x11-ime/quickstart.md` (adapted for IBus).

**Checkpoint**: IBus D-Bus IME works end-to-end. English typing and CJK commit both functional. Diagnostic logging covers FR-001 through FR-004, FR-006, FR-007.

---

## Phase 4: User Story 2 — Fcitx D-Bus Client (Priority: P2)

**Goal**: Implement Fcitx D-Bus client so CJK text input works on Arch/CJK-focused distros. Includes debug sample page (FR-005).

**Independent Test**: Start SamplesApp in headless Xvfb + fcitx5 environment, focus TextBox, type Pinyin "ni" and commit — Chinese character 你 appears in TextBox.

### Implementation for User Story 2

- [ ] T016 [P] [US2] Implement `FcitxICWrapper` in `src/Uno.UI.Runtime.Skia.X11/IME/FcitxICWrapper.cs` — unified API wrapping both fcitx4 (`OrgFcitxFcitxInputContext` proxy) and fcitx5 (`OrgFcitxFcitxInputContext1` proxy). Map fcitx5 `bool` return/type to fcitx4 `int`. Handle `SetCapability` (`uint64` for fcitx5 vs `uint` for fcitx4). Convert fcitx preedit cursor from UTF-8 byte offset to character offset via `Encoding.UTF8.GetCharCount()`. Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/Fcitx/FcitxICWrapper.cs`
- [ ] T017 [US2] Implement `FcitxInputMethod` in `src/Uno.UI.Runtime.Skia.X11/IME/FcitxInputMethod.cs` — try `org.freedesktop.portal.Fcitx` (fcitx5) first, fall back to `org.fcitx.Fcitx` (fcitx4). `CreateInputContext`/`CreateICv3`, subscribe to `CommitString`/`ForwardKey`/`UpdateFormattedPreedit` signals, implement key forwarding and cursor updates via `FcitxICWrapper`. Add trace logging. Reference: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/Fcitx/FcitxX11TextInputMethod.cs`
- [ ] T018 [US2] Wire `FcitxInputMethod` into `X11InputMethodDetector.DetectAndCreate()` in `src/Uno.UI.Runtime.Skia.X11/IME/X11InputMethodDetector.cs` — when detected IME is "fcitx" or "fcitx5", create and return `FcitxInputMethod`
- [ ] T019 [US2] Headless validation: Set up Xvfb + dbus-launch + fcitx5 + SamplesApp (using environment from `specs/003-debug-x11-ime/quickstart.md`), verify English typing works and Pinyin commit delivers Chinese text via fcitx5 `CommitString` signal.
- [ ] T020 [P] [US2] Create IME debug sample page XAML in `src/SamplesApp/UITests.Shared/Microsoft_UI_Xaml_Controls/TextBox/TextBox_X11_IME_Debug.xaml` — TextBox for input, TextBlocks showing: current IME backend (IBus/Fcitx/XIM/None), composition state (composing/idle), last committed text, last preedit text, last event routing decision. Apply `[Sample("TextBox", "X11 IME Debug")]` attribute.
- [ ] T021 [US2] Create IME debug sample page code-behind in `src/SamplesApp/UITests.Shared/Microsoft_UI_Xaml_Controls/TextBox/TextBox_X11_IME_Debug.xaml.cs` — subscribe to `IX11InputMethod` events (Commit, PreeditChanged) and update display TextBlocks in real-time. Show backend type from `X11InputMethodDetector`.
- [ ] T022 [US2] Register debug sample page in `src/SamplesApp/UITests.Shared/UITests.Shared.projitems` — add both XAML `<Page>` and `<Compile>` entries per AGENTS.md registration requirements.

**Checkpoint**: Fcitx D-Bus IME works end-to-end. Debug sample page shows real-time IME state (FR-005, SC-002).

---

## Phase 5: User Story 3 — Event Sequence Documentation + XIM Cleanup (Priority: P3)

**Goal**: Document expected event sequences for common IME scenarios (FR-008). Clean up experimental XIM debugging code.

**Independent Test**: Capture event log for documented scenario, compare against reference — they match.

### Implementation for User Story 3

- [ ] T023 [P] [US3] Document expected D-Bus IME event sequences in `specs/003-debug-x11-ime/event-sequences.md` — cover at least 3 scenarios: (1) Direct ASCII input "a" with IBus English mode (key event → ProcessKeyEvent → not handled → KeyDown dispatch), (2) Pinyin "ni" composition + commit via D-Bus (key events → ProcessKeyEvent handled → CommitString signal → OnCommittedText), (3) Composition cancel via Escape (key events → ProcessKeyEvent handled → preedit cleared). Include trace log format for each step.
- [ ] T024 [P] [US3] Document XIM fallback event sequences in `specs/003-debug-x11-ime/event-sequences.md` — cover: (1) Direct ASCII via XIM (XFilterEvent → not filtered → Xutf8LookupString → XLookupBoth → KeyDown), (2) Known XIM commit limitation (XFilterEvent filtered → keycode=0 → XLookupNone). Reference existing trace log format.
- [ ] T025 [US3] Clean up experimental debug code in `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs` — remove event pumping workaround for keycode=0 (`while (X11Helper.XPending...)` block), remove `XmbLookupString` fallback, clean up verbose PRE-FILTER logging in `src/Uno.UI.Runtime.Skia.X11/Hosting/X11XamlRootHost.x11events.cs`. Keep clean trace logging that follows the documented event sequence format.

**Checkpoint**: Event sequences documented (FR-008, SC-003). Experimental code cleaned up.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories.

- [ ] T026 Update headless test script in `specs/003-debug-x11-ime/run-ime-test.sh` — add D-Bus IME test scenarios (fcitx5 Pinyin commit, IBus Pinyin commit, English typing, IME toggle), verify via app logs that `CommitString`/`CommitText` signals are received.
- [ ] T027 [P] Handle edge cases in `src/Uno.UI.Runtime.Skia.X11/IME/DBusInputMethodBase.cs` — IME service crash/restart (reconnect via `NameOwnerChanged`), focus loss during composition (reset), rapid key sequences (async queue ordering).
- [ ] T028 [P] Verify graceful degradation: test with `UNO_IM_MODULE=none` (XIM fallback), test with no D-Bus session bus (XIM fallback), test with no IME at all (direct keyboard input works).
- [ ] T029 Update `specs/003-debug-x11-ime/quickstart.md` — ensure XIM fallback is documented, add IBus headless test instructions alongside fcitx5.
- [ ] T030 Run quickstart.md validation — execute full headless test environment setup and verify all test scenarios pass.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 completion (needs generated proxy classes)
- **User Story 1 — IBus (Phase 3)**: Depends on Phase 2 (needs `IX11InputMethod` interface)
- **User Story 2 — Fcitx (Phase 4)**: Depends on Phase 2 (needs `IX11InputMethod` interface). Can run in parallel with Phase 3.
- **User Story 3 — Docs/Cleanup (Phase 5)**: Can start after Phase 3 (needs working D-Bus IME for event capture)
- **Polish (Phase 6)**: Depends on Phases 3-5

### User Story Dependencies

- **US1 (IBus)**: Depends on Foundational. No dependency on other stories. **This is the MVP.**
- **US2 (Fcitx)**: Depends on Foundational. Can run in parallel with US1. Debug sample page depends on `IX11InputMethod` events being wired (T012-T014 from US1 or equivalent).
- **US3 (Docs/Cleanup)**: Depends on at least US1 being complete (needs working D-Bus path to document).

### Parallel Opportunities

**Within Phase 1**: T001, T002, T003, T004 can all run in parallel (independent XML files).

**After Phase 2**: US1 (IBus) and US2 (Fcitx) can run in parallel — they implement different backends against the same interface.

**Within US2**: T016 (FcitxICWrapper) and T020-T021 (debug sample page) can run in parallel.

**Within US3**: T023 and T024 can run in parallel (independent doc sections).

---

## Parallel Example: Phase 1 Setup

```
Launch all XML definition tasks together:
  T001: IBus Portal XML
  T002: IBus InputContext XML
  T003: Fcitx5 XMLs
  T004: Fcitx4 XMLs
Then sequentially:
  T005: Register in .csproj
  T006: Build and verify
```

## Parallel Example: User Stories 1 + 2

```
After Phase 2 (Foundational) completes:
  Developer A: US1 (IBus) — T010 → T011 → T012 → T013 → T014 → T015
  Developer B: US2 (Fcitx) — T016 + T020 in parallel → T017 → T018 → T019 → T021 → T022
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (D-Bus XML + proxy generation)
2. Complete Phase 2: Foundational (IX11InputMethod + Detector + DBusBase)
3. Complete Phase 3: User Story 1 (IBus D-Bus Client)
4. **STOP and VALIDATE**: Headless test — Pinyin commit delivers 你 via IBus
5. This covers the majority of Linux desktop users (Ubuntu/Fedora/GNOME)

### Incremental Delivery

1. Setup + Foundational → D-Bus infrastructure ready
2. Add IBus → Test independently → **MVP ships** (covers ~70% of Linux IME users)
3. Add Fcitx → Test independently → Covers remaining CJK users
4. Add docs + cleanup → Polish for maintainability
5. Each story adds value without breaking previous stories

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Avalonia reference at `/tmp/avalonia/` — clone may not persist across sessions; re-clone if needed
- Headless test environment: Xvfb :42 TCP + dbus-launch + fcitx5 --disable wayland,waylandim + fluxbox
- All diagnostic logging MUST be gated behind `LogLevel.Trace` or `LogLevel.Debug` (FR-007)
