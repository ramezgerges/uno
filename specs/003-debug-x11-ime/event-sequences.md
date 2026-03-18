# Expected IME Event Sequences

**Tasks**: T023 (D-Bus IME sequences), T024 (XIM fallback sequences)
**Purpose**: Reference event sequences for common IME scenarios. Compare observed trace logs against these to pinpoint where the pipeline diverges from expected behavior.
**Prerequisite**: Enable trace logging (`LogLevel.Trace`) for the `Uno.WinUI.Runtime.Skia.X11` namespace.

---

## How to Read These Sequences

Each scenario shows the expected trace log output in order. The log lines come from these source locations:

| Log prefix | Source file | Method |
|------------|------------|--------|
| `Fcitx ProcessKeyEvent` | `IME/FcitxInputMethod.cs` | `HandleKeyEventAsync` |
| `IBus ProcessKeyEvent` | `IME/IBusInputMethod.cs` | `HandleKeyEventAsync` |
| `D-Bus IME key` | `Devices/Input/X11KeyboardInputSource.cs` | `ProcessKeyboardEventDBus` |
| `D-Bus IME Commit` | `Devices/Input/X11KeyboardInputSource.cs` | `OnDBusImeCommit` |
| `D-Bus IME PreeditChanged` | `Devices/Input/X11KeyboardInputSource.cs` | `OnDBusImePreeditChanged` |
| `D-Bus IME ForwardKey` | `Devices/Input/X11KeyboardInputSource.cs` | `OnDBusImeForwardKey` |
| `Fcitx CommitString` | `IME/FcitxInputMethod.cs` | `OnCommitString` |
| `Fcitx UpdateFormattedPreedit` | `IME/FcitxInputMethod.cs` | `OnUpdateFormattedPreedit` |
| `IBus CommitText` | `IME/IBusInputMethod.cs` | `OnCommitText` |
| `IBus UpdatePreeditText` | `IME/IBusInputMethod.cs` | `OnUpdatePreeditText` |
| `Dispatching KeyDown/KeyUp` | `Devices/Input/X11KeyboardInputSource.cs` | `DispatchKeyEvent` |
| `XFilterEvent consumed` | `Hosting/X11XamlRootHost.x11events.cs` | `Run` (event loop) |
| `XIM ProcessKeyboardEvent` | `Devices/Input/X11KeyboardInputSource.cs` | `ProcessKeyboardEventXIM` |

All source files are under `src/Uno.UI.Runtime.Skia.X11/`.

---

## D-Bus IME Event Sequences

When a D-Bus IME backend (IBus or Fcitx) is active, `XFilterEvent` is bypassed entirely. All `KeyPress`/`KeyRelease` X11 events are routed from the event loop directly to `ProcessKeyboardEvent`, which delegates to `ProcessKeyboardEventDBus`. The D-Bus IME's `ProcessKeyEvent` method determines whether the IME consumes the key.

### Event Flow Overview (D-Bus Active)

```
X11 Event Loop (XNextEvent)
  │
  ├── XFilterEvent SKIPPED (D-Bus IME is active)
  │
  └── KeyPress/KeyRelease
        │
        └── X11KeyboardInputSource.ProcessKeyboardEvent()
              │
              └── ProcessKeyboardEventDBus()
                    │
                    ├── XLookupString (get keysym + text for VK mapping)
                    │
                    ├── IX11InputMethod.HandleKeyEventAsync(keyval, keycode, state, isRelease)
                    │     │
                    │     └── D-Bus call: ProcessKeyEvent on IBus/Fcitx
                    │
                    ├── If handled=True → return (IME consumed the key)
                    │     │
                    │     └── Signals arrive asynchronously:
                    │           ├── CommitString / CommitText → OnDBusImeCommit → TextBox
                    │           ├── UpdateFormattedPreedit / UpdatePreeditText → OnDBusImePreeditChanged → TextBox
                    │           └── ForwardKeyEvent / ForwardKey → OnDBusImeForwardKey → DispatchKeyEvent
                    │
                    └── If handled=False → DispatchKeyEvent (normal KeyDown/KeyUp)
```

---

### Scenario 1: Direct ASCII Input "a" (English Mode, Fcitx5)

The IME is active but in English/direct input mode. The key press goes through `ProcessKeyEvent` on D-Bus, the IME returns `handled=false`, and the key is dispatched as a normal `KeyDown` event with the unicode character.

**User action**: Press and release the "A" key with no modifiers.

**Expected trace log (KeyPress)**:

```
Fcitx ProcessKeyEvent: keyval=0x61 keycode=38 state=0x0 type=0 → handled=False
D-Bus IME key: keyval=0x61 keycode=38 state=0x0 pressed=True → handled=False
Dispatching KeyDown: vk=A unicodeKey=a
```

**Expected trace log (KeyRelease)**:

```
Fcitx ProcessKeyEvent: keyval=0x61 keycode=38 state=0x0 type=1 → handled=False
D-Bus IME key: keyval=0x61 keycode=38 state=0x0 pressed=False → handled=False
Dispatching KeyUp: vk=A unicodeKey=a
```

**Key observations**:

- `keyval=0x61` is the X11 keysym for lowercase "a" (`XK_a`).
- `keycode=38` is the physical scancode for the A key on a standard QWERTY layout.
- `state=0x0` means no modifier keys are held.
- `type=0` is Fcitx's `PressKey` constant; `type=1` is `ReleaseKey`.
- The IME returns `handled=False` because it is in direct/English mode and does not consume the key.
- The key is dispatched normally via `DispatchKeyEvent`, producing a `KeyDown` with `unicodeKey=a`.
- No `CommitString` or `UpdateFormattedPreedit` signals are emitted.

---

### Scenario 2: Pinyin "ni" Composition + Commit (Fcitx5)

The user types "n", "i" to compose Pinyin, then presses Space to select the first candidate and commit the Chinese character. Each keystroke is consumed by the IME (`handled=True`). The `UpdateFormattedPreedit` signal shows the evolving preedit text. Space triggers `CommitString` with the final character.

**User action**: Press "N", press "I", press Space.

**Expected trace log**:

```
Fcitx ProcessKeyEvent: keyval=0x6E keycode=57 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0x6E keycode=57 state=0x0 pressed=True → handled=True
Fcitx UpdateFormattedPreedit: text='n' cursor=1 (raw cursor byte offset=1)
D-Bus IME PreeditChanged: text='n' cursor=1

Fcitx ProcessKeyEvent: keyval=0x69 keycode=31 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0x69 keycode=31 state=0x0 pressed=True → handled=True
Fcitx UpdateFormattedPreedit: text='ni' cursor=2 (raw cursor byte offset=2)
D-Bus IME PreeditChanged: text='ni' cursor=2

Fcitx ProcessKeyEvent: keyval=0x20 keycode=65 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0x20 keycode=65 state=0x0 pressed=True → handled=True
Fcitx CommitString: '你'
D-Bus IME Commit: '你'
```

**Key observations**:

- Each key press returns `handled=True` because the IME is in composition mode and consumes the input.
- No `KeyDown` events are dispatched while the IME is composing. The keys do not reach the TextBox's key event handlers.
- `UpdateFormattedPreedit` is a D-Bus signal emitted asynchronously by the Fcitx service. It arrives on the D-Bus connection thread and is forwarded via `QueueAction` to the UI thread.
- The preedit cursor offset for ASCII Pinyin input is simple: 1 byte per character, so the raw byte offset equals the character offset.
- `CommitString` delivers the final committed text as a plain string. This is the reliable commit path that XIM fails to provide (see Scenario 6).
- After `CommitString`, the preedit is implicitly cleared. A subsequent `UpdateFormattedPreedit` with empty text may or may not arrive depending on the engine.
- `KeyRelease` events for composition keys are also sent to the IME (`type=1`). They are typically `handled=True` as well but produce no signals. They are omitted from this trace for brevity.

**Preedit cursor note for CJK preedit text**: When the preedit contains multi-byte UTF-8 characters (e.g., during candidate selection with inline preedit), the raw cursor byte offset and character offset diverge. For example, preedit text "你好" with raw byte offset 3 converts to character offset 1 (because each CJK character is 3 bytes in UTF-8).

---

### Scenario 3: Composition Cancel via Escape (Fcitx5)

The user starts typing Pinyin, then presses Escape to cancel. The preedit is cleared and no text is committed.

**User action**: Press "N", press "I", press Escape.

**Expected trace log**:

```
Fcitx ProcessKeyEvent: keyval=0x6E keycode=57 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0x6E keycode=57 state=0x0 pressed=True → handled=True
Fcitx UpdateFormattedPreedit: text='n' cursor=1 (raw cursor byte offset=1)
D-Bus IME PreeditChanged: text='n' cursor=1

Fcitx ProcessKeyEvent: keyval=0x69 keycode=31 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0x69 keycode=31 state=0x0 pressed=True → handled=True
Fcitx UpdateFormattedPreedit: text='ni' cursor=2 (raw cursor byte offset=2)
D-Bus IME PreeditChanged: text='ni' cursor=2

Fcitx ProcessKeyEvent: keyval=0xFF1B keycode=9 state=0x0 type=0 → handled=True
D-Bus IME key: keyval=0xFF1B keycode=9 state=0x0 pressed=True → handled=True
Fcitx UpdateFormattedPreedit: text='' cursor=0 (raw cursor byte offset=0)
D-Bus IME PreeditChanged: text='' cursor=0
```

**Key observations**:

- `keyval=0xFF1B` is the X11 keysym for Escape (`XK_Escape`).
- The IME handles Escape (`handled=True`) and clears the preedit.
- `UpdateFormattedPreedit` with empty text signals the end of composition.
- No `CommitString` signal is emitted because the composition was cancelled, not committed.
- The `OnDBusImePreeditChanged` handler checks `string.IsNullOrEmpty(preeditText)` and calls `OnPreeditChanged(null, 0)` to end the composition state in the TextBox.
- Some IME engines may also forward the Escape key via `ForwardKey` after cancelling. If so, an additional `Dispatching KeyDown: vk=Escape` line would appear.

---

### Scenario 4: Direct ASCII Input "a" (IBus, English Mode)

Same scenario as Scenario 1 but using IBus instead of Fcitx. The key event flow is identical at the `X11KeyboardInputSource` level; only the backend log prefix and D-Bus method details differ.

**User action**: Press and release the "A" key with no modifiers.

**Expected trace log (KeyPress)**:

```
IBus ProcessKeyEvent: keyval=0x61 keycode=38 state=0x0 → handled=False
D-Bus IME key: keyval=0x61 keycode=38 state=0x0 pressed=True → handled=False
Dispatching KeyDown: vk=A unicodeKey=a
```

**Key differences from Fcitx**:

- IBus `ProcessKeyEvent` takes `(keyval, keycode, state)` with release encoded in the state bitmask (`ReleaseMask = 1 << 30`), not as a separate `type` parameter.
- For a key press, `state=0x0` (no `ReleaseMask`). For a key release, `state` would have bit 30 set: `state=0x40000000`.
- The log line does not show a `type` field because IBus encodes press/release in `state`.

---

### Scenario 5: Pinyin "ni" Composition + Commit (IBus)

Same user action as Scenario 2 but through IBus. The signal names and data formats differ.

**User action**: Press "N", press "I", press Space.

**Expected trace log**:

```
IBus ProcessKeyEvent: keyval=0x6E keycode=57 state=0x0 → handled=True
D-Bus IME key: keyval=0x6E keycode=57 state=0x0 pressed=True → handled=True
IBus UpdatePreeditText: text='n' cursor=0 visible=True
D-Bus IME PreeditChanged: text='n' cursor=0

IBus ProcessKeyEvent: keyval=0x69 keycode=31 state=0x0 → handled=True
D-Bus IME key: keyval=0x69 keycode=31 state=0x0 pressed=True → handled=True
IBus UpdatePreeditText: text='ni' cursor=0 visible=True
D-Bus IME PreeditChanged: text='ni' cursor=0

IBus ProcessKeyEvent: keyval=0x20 keycode=65 state=0x0 → handled=True
D-Bus IME key: keyval=0x20 keycode=65 state=0x0 pressed=True → handled=True
IBus CommitText: '你'
D-Bus IME Commit: '你'
```

**Key differences from Fcitx**:

- IBus `CommitText` signal carries a D-Bus variant struct, not a plain string. The actual text is at index 2 of the struct: `text.GetItem(2).GetString()`. This is an IBus-specific encoding detail handled in `IBusInputMethod.OnCommitText`.
- IBus `UpdatePreeditText` signal carries `(variant text, uint cursor_pos, bool visible)`. The text string is also extracted from a variant struct at index 2, same as `CommitText`.
- IBus preedit `cursor_pos` is a character offset (not a byte offset like Fcitx), so no UTF-8 byte-to-character conversion is needed.
- The `visible` field on `UpdatePreeditText` controls whether the preedit should be shown. When `visible=False`, the preedit text is treated as `null` regardless of the actual text content.
- IBus also has separate `ShowPreeditText` and `HidePreeditText` signals. `HidePreeditText` triggers `PreeditChanged(null, 0)` to clear the composition.
- IBus may fire `CommitText` during a `Reset()` call. The `_insideReset` guard in `IBusInputMethod` suppresses these spurious commits.

---

## XIM Fallback Event Sequences

When no D-Bus IME backend is available (or when `UNO_IM_MODULE=none`), the system falls back to the XIM (X Input Method) protocol. In this mode, `XFilterEvent` is called for every X11 event, and `Xutf8LookupString` is used for text lookup.

### Event Flow Overview (XIM Fallback)

```
X11 Event Loop (XNextEvent)
  │
  ├── XFilterEvent(@event)
  │     │
  │     ├── Returns True → event consumed by XIM (composition keystroke)
  │     │     └── Log: "XFilterEvent consumed KeyPress: keycode=..."
  │     │           └── OnComposing() called on TextBox extension
  │     │
  │     └── Returns False → event not consumed, continue processing
  │
  └── KeyPress/KeyRelease (if not filtered)
        │
        └── X11KeyboardInputSource.ProcessKeyboardEvent()
              │
              └── ProcessKeyboardEventXIM()
                    │
                    ├── Xutf8LookupString(xic, keyEvent, buffer, ..., keySym, status)
                    │
                    ├── status = XLookupBoth → keysym + text available
                    │     └── Dispatch KeyDown with unicodeKey
                    │
                    ├── status = XLookupChars → text only (committed text from IME)
                    │     ├── If composing → OnCommittedText(text), return
                    │     └── If not composing → dispatch as KeyDown with unicodeKey
                    │
                    ├── status = XLookupKeySym → keysym only, no text
                    │     └── Dispatch KeyDown without unicodeKey
                    │
                    └── status = XLookupNone → nothing returned
                          └── return (event dropped)
```

---

### Scenario 6: Direct ASCII Input "a" via XIM

The user types "a" with no IME composition active. `XFilterEvent` returns false (not filtered), and `Xutf8LookupString` returns `XLookupBoth` with both a keysym and text.

**User action**: Press and release the "A" key with no modifiers, no IME composition active.

**Expected trace log (KeyPress)**:

```
XIM ProcessKeyboardEvent pressed=True: keycode=38 keySym=97 status=3 nbytes=1 text='a' window=0x... xic=0x...
Dispatching KeyDown: vk=A unicodeKey=a
```

**Expected trace log (KeyRelease)**:

```
XIM ProcessKeyboardEvent pressed=False: keycode=38 keySym=97 vk=A text='a' nbytes=1
Dispatching KeyUp: vk=A unicodeKey=a
```

**Key observations**:

- `status=3` corresponds to `XLookupBoth` (both keysym and text available).
- `keySym=97` is decimal for `XK_a` (0x61).
- `nbytes=1` means one byte of UTF-8 text was returned.
- For key release events, `Xutf8LookupString` is not called (the code uses `XLookupString` directly since XIC-based lookup is only done for key presses).
- This path works reliably for ASCII input in all tested environments.

---

### Scenario 7: XIM Composition with Filtered Events

When the user is composing with XIM (e.g., IBus in Pinyin mode via XIM protocol), keystrokes during composition are filtered by `XFilterEvent`. These events never reach `ProcessKeyboardEvent`.

**User action**: Press "N" while IME is in composition mode.

**Expected trace log**:

```
XFilterEvent consumed KeyPress: keycode=57
```

**Key observations**:

- `XFilterEvent` returns `true`, meaning the XIM input method consumed the event.
- The event loop calls `OnComposing()` on the TextBox IME extension to signal that a composition is in progress.
- The event is skipped with `continue` and never reaches `ProcessKeyboardEvent`.
- No `KeyDown` is dispatched to the application.
- The XIM input method may internally update its candidate window, but this happens outside of the application's event handling.

---

### Scenario 8: Known XIM Commit Limitation (The Bug D-Bus IME Solves)

This is the critical failure mode that motivated the D-Bus IME implementation. When the user commits a CJK character via XIM, the input method synthesizes a `KeyPress` event with `keycode=0`. However, `Xutf8LookupString` returns `XLookupNone` (no text, no keysym), losing the committed text entirely.

**User action**: Type Pinyin "ni" and press Space to commit via XIM.

**Expected trace log for the commit keystroke**:

```
XIM ProcessKeyboardEvent pressed=True: keycode=0 keySym=0 status=1 nbytes=0 text='' window=0x... xic=0x...
```

Then the event is dropped because `status=1` is `XLookupNone` (the switch statement hits `case XLib.XLookupNone: return`).

**What should have happened (but does not)**:

```
XIM ProcessKeyboardEvent pressed=True: keycode=0 keySym=0 status=2 nbytes=3 text='你' window=0x... xic=0x...
```

With `status=2` (`XLookupChars`), `Xutf8LookupString` would return the committed UTF-8 bytes and the `XLookupChars` handler would call `OnCommittedText("你")`.

**Key observations**:

- `keycode=0` is the synthetic keycode that XIM uses for commit events. It does not correspond to any physical key.
- `status=1` is `XLookupNone`: no keysym, no text, zero bytes. The committed text is lost.
- This is a known issue confirmed with a native C test program in Xvfb + fcitx5/IBus environments.
- The root cause appears to be in how some XIM implementations deliver commit text for the synthetic keycode=0 event. The XIC lookup fails to produce the committed string.
- Firefox, GTK, and Qt do not experience this issue because they use D-Bus IME clients (not XIM) for text commit.
- This is the primary bug that the D-Bus IME implementation resolves. With D-Bus, committed text arrives via the `CommitString`/`CommitText` signal as an explicit string payload, bypassing `Xutf8LookupString` entirely.

**XLookupString status values reference**:

| Constant | Value | Meaning |
|----------|-------|---------|
| `XLookupNone` | 1 | Nothing returned |
| `XLookupChars` | 2 | Text returned, no keysym |
| `XLookupKeySym` | 3 | Keysym returned, no text |
| `XLookupBoth` | 4 | Both keysym and text returned |
| `XBufferOverflow` | -1 | Buffer too small, retry with larger buffer |

Note: The numeric values above match the constants defined in `XLib`. In some references, `XLookupKeySym` and `XLookupBoth` are swapped (3 and 4 vs 1 and 2). Check the actual `XLib` constant values used in the codebase if the status codes in observed logs differ.

---

## Summary: D-Bus IME vs XIM Comparison

| Aspect | D-Bus IME | XIM Fallback |
|--------|-----------|-------------|
| Event filtering | `XFilterEvent` skipped | `XFilterEvent` active |
| Key routing | `ProcessKeyEvent` D-Bus call | `Xutf8LookupString` |
| Commit delivery | `CommitString`/`CommitText` signal (string) | `Xutf8LookupString` text bytes |
| Commit reliability | Reliable (explicit string payload) | Broken for CJK (keycode=0 returns `XLookupNone`) |
| Preedit updates | `UpdateFormattedPreedit`/`UpdatePreeditText` signal | XIM callbacks (if configured) |
| Cursor position | `SetCursorRect`/`SetCursorLocation` D-Bus call | `XSetICValues` with `XNSpotLocation` |
| Latency | D-Bus round-trip per keystroke | Local X11 calls |
| Supported IMEs | IBus, Fcitx4, Fcitx5 | Any XIM-compatible IM |

---

## Debugging Checklist

When observed behavior diverges from these reference sequences:

1. **Check IME backend detection**: Look for `IBus D-Bus IME connected` or `Fcitx D-Bus IME connected` at startup. If neither appears, the system fell back to XIM.
2. **Check `handled` return value**: If `handled=True` but no commit/preedit signal follows, the IME may have consumed the key without action (e.g., an internal state change).
3. **Check signal arrival**: If `ProcessKeyEvent` returns `handled=True` but the `CommitString`/`CommitText` trace line never appears, the D-Bus signal may not be reaching the application (connection issue, service crash).
4. **Check thread dispatching**: Commits and preedit changes are dispatched to the UI thread via `QueueAction`. If the trace shows `D-Bus IME Commit` but the TextBox does not update, check that `X11ImeTextBoxExtension.Instance` is valid and `OnCommittedText` is being called.
5. **Check IBus variant extraction**: For IBus, if `CommitText` trace shows an empty string, the variant struct layout may have changed. Verify that `text.GetItem(2)` still returns the string (index 2 in the IBus text variant).
6. **Check Fcitx cursor encoding**: If the preedit cursor is at the wrong position, verify the UTF-8 byte offset to character offset conversion in `OnUpdateFormattedPreedit`.
