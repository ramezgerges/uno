# Tasks: X11 IME Support for TextBox

**Input**: Design documents from `/specs/002-x11-ime-support/`
**Prerequisites**: plan.md (required), spec.md (required), research.md

**Tests**: Not explicitly requested. Manual IME testing with IBus/Fcitx is the primary validation.

**Organization**: Tasks grouped by user story. US1 (CJK composition) is the MVP.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2)
- Include exact file paths in descriptions

---

## Phase 1: Setup (P/Invoke Bindings)

**Purpose**: Add required XIM/XIC P/Invoke function bindings that all subsequent tasks depend on

- [X] T001 Add XIM/XIC P/Invoke bindings (XOpenIM, XCloseIM, XCreateIC, XDestroyIC, XSetICFocus, XUnsetICFocus, XFilterEvent, Xutf8LookupString, XSetICValues) and required constants/structs (XIMPreeditNothing, XIMStatusNothing, XNInputStyle, XNClientWindow, XNFocusWindow, XNSpotLocation, XPoint, XLookupChars/XLookupKeySym/XLookupBoth/XBufferOverflow status constants) in `src/Uno.UI.Runtime.Skia.X11/X11_Bindings/x11bindings_XLib.cs`

---

## Phase 2: Foundational (Enable IME & Event Filtering)

**Purpose**: Remove the IME disable flag and add XFilterEvent to the event loop — MUST complete before user stories

**CRITICAL**: No user story work can begin until this phase is complete

- [X] T002 Change `XSetLocaleModifiers("@im=none")` to `XSetLocaleModifiers("")` in all three locale fallback paths in `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs` (lines 45, 48, 51) to enable system default IME
- [X] T003 Add `XFilterEvent` call in the event loop in `src/Uno.UI.Runtime.Skia.X11/Hosting/X11XamlRootHost.x11events.cs` — call it immediately after `XNextEvent` in the `GetEvents` iterator (line 141), and skip the event (continue) if `XFilterEvent` returns true

**Checkpoint**: IME framework is connected but no composition handling yet. Normal keyboard input should still work.

---

## Phase 3: User Story 1 — CJK Text Input via IME (Priority: P1) MVP

**Goal**: Users can compose and commit CJK text in a TextBox on X11 Linux using IBus or Fcitx

**Independent Test**: Launch SamplesApp on X11 with IBus + Pinyin, focus a TextBox, type a Pinyin sequence, verify composition string appears with underline, select a candidate, verify committed text appears

### Implementation for User Story 1

- [X] T004 [US1] Create `X11ImeTextBoxExtension` singleton class implementing `IImeTextBoxExtension` in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs` — implement `StartImeSession(TextBox)` to get X11 display/window from TextBox's XamlRoot via `XamlRootMap.GetHostForRoot`, open XIM via `XOpenIM` if not yet open, create XIC via `XCreateIC` per window (cache by window handle), call `XSetICFocus`; implement `EndImeSession()` to call `XUnsetICFocus` and reset composition state; implement `IsComposing` property; declare `CompositionStarted`, `CompositionUpdated`, `CompositionCompleted`, `CompositionEnded` events
- [X] T005 [US1] Register `IImeTextBoxExtension` via `ApiExtensibility.Register(typeof(IImeTextBoxExtension), _ => X11ImeTextBoxExtension.Instance)` in the static constructor of `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs`
- [X] T006 [US1] Modify `ProcessKeyboardEvent` in `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs` to replace `XLookupString` with `Xutf8LookupString` when an XIC is available for the current window — handle lookup status: when `XLookupChars` or `XLookupBoth`, use the returned UTF-8 string as committed text; when `XLookupKeySym`, process key normally without text; when `XLookupNone`, skip entirely (IME consumed the event); fall back to `XLookupString` if no XIC available
- [X] T007 [US1] Add composition event raising in `X11ImeTextBoxExtension` — expose a method (e.g., `OnKeyEvent`) called from the keyboard source or event loop that detects composition state changes: raise `CompositionStarted` when preedit text first appears, raise `CompositionUpdated` when preedit text changes, raise `CompositionCompleted` when `Xutf8LookupString` returns committed text with `XLookupChars`/`XLookupBoth`, raise `CompositionEnded` when composition finishes; use the XIM preedit callbacks or query the XIC preedit string to detect composition state
- [X] T008 [US1] Add candidate window positioning — when `StartImeSession` is called or caret position changes, call `XSetICValues` with `XNSpotLocation` set to the TextBox caret position (converted to window-relative coordinates) in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs`

**Checkpoint**: CJK text input via IME should work end-to-end. Composition underline and text rendering are handled by the existing managed TextBox layer.

---

## Phase 4: User Story 2 — TextComposition Events for X11 (Priority: P2)

**Goal**: TextCompositionStarted, TextCompositionChanged, and TextCompositionEnded events fire correctly during X11 IME composition

**Independent Test**: Subscribe to TextComposition events on a TextBox, compose text via IME, verify events fire with correct StartIndex and Length

### Implementation for User Story 2

- [X] T009 [US2] Verify TextComposition events fire correctly by testing the end-to-end flow — the managed TextBox layer already raises TextCompositionStarted/Changed/Ended in response to `IImeTextBoxExtension` events; verify that `CompositionStarted` from X11 triggers `TextCompositionStarted` with correct `StartIndex`, `CompositionUpdated` triggers `TextCompositionChanged` with correct `StartIndex` and `Length`, and `CompositionCompleted`/`CompositionEnded` trigger `TextCompositionEnded`; fix any gaps in event argument population in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs`

**Checkpoint**: TextComposition events work correctly. This is largely validated by US1 working since the managed layer handles event raising.

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Edge cases, graceful degradation, and cleanup

- [X] T010 Handle no-IME scenario gracefully in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs` — if `XOpenIM` returns null (no IME configured), the extension should still be registered but effectively no-op; `StartImeSession`/`EndImeSession` should not crash; normal keyboard input via `XLookupString` fallback in `X11KeyboardInputSource.cs` must continue working
- [X] T011 Ensure thread safety for all XIM/XIC calls by using `X11Helper.XLock` pattern in `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs` — XIM calls from the event loop thread and managed layer calls from the UI thread must be properly synchronized
- [X] T012 Build and verify with `dotnet build src/SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj -p:UnoTargetFrameworkOverride=net10.0`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately
- **Phase 2 (Foundational)**: Depends on Phase 1 — BLOCKS all user stories
- **Phase 3 (US1)**: Depends on Phase 2
- **Phase 4 (US2)**: Depends on Phase 3 (uses composition events from US1)
- **Phase 5 (Polish)**: Depends on Phase 3 at minimum

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Phase 2. MVP — delivers core IME functionality.
- **User Story 2 (P2)**: Depends on US1. The managed TextBox layer already raises TextComposition events from `IImeTextBoxExtension` events, so US2 is primarily verification.

### Within Each Phase

- T001 must complete before T002-T003
- T004 must complete before T005-T008
- T005 is required for T006 (ApiExtensibility registration needed for keyboard source to find XIC)
- T006-T008 can proceed after T005
- T009 depends on T004-T008 being complete

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: P/Invoke bindings (T001)
2. Complete Phase 2: Enable IME + XFilterEvent (T002-T003)
3. Complete Phase 3: X11ImeTextBoxExtension + keyboard integration (T004-T008)
4. **STOP and VALIDATE**: Test with IBus/Fcitx on X11
5. If working, proceed to Phase 4 and 5

### Incremental Delivery

1. T001-T003 → IME framework enabled, normal input still works
2. T004-T008 → CJK composition works end-to-end (MVP!)
3. T009 → TextComposition events verified
4. T010-T012 → Edge cases handled, build validated

---

## Notes

- The managed TextBox composition logic (state tracking, text replacement, underline rendering, TextComposition events) is already implemented from Win32 — no changes needed in `src/Uno.UI/`
- All new code goes in `src/Uno.UI.Runtime.Skia.X11/`
- Follow existing P/Invoke patterns in `x11bindings_XLib.cs` for new bindings
- Follow existing singleton + ApiExtensibility pattern from Win32 (`Win32ImeTextBoxExtension`)
- Use `X11Helper.XLock` for all X11 calls from non-event-loop threads
