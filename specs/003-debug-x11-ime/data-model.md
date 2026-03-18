# Data Model: D-Bus IME Support for X11

## Entities

### IX11InputMethod (Interface)

The core abstraction for all IME backends (IBus, Fcitx, XIM).

| Field/Method | Type | Description |
|-------------|------|-------------|
| `IsEnabled` | `bool` | Whether the input method is active and connected |
| `HandleKeyEventAsync(keyVal, keyCode, state, isRelease)` | `ValueTask<bool>` | Forward key event to IME; returns true if handled |
| `SetCursorLocation(x, y, w, h)` | `void` | Update IME candidate window position |
| `SetFocus(active)` | `void` | Notify IME of focus change |
| `Reset()` | `void` | Reset composition state |
| `Dispose()` | `void` | Cleanup resources |
| `Commit` event | `Action<string>` | Fired when IME commits text |
| `ForwardKey` event | `Action<uint, uint, uint>` | Fired when IME forwards a key back (keyval, keycode, state) |
| `PreeditChanged` event | `Action<string?, int>` | Fired when preedit text changes (text, cursorPos) |

### DBusInputMethodBase (Abstract Class)

Shared logic for D-Bus-based IME implementations.

| Field | Type | Description |
|-------|------|-------------|
| `_connection` | `Connection` | D-Bus session bus connection |
| `_serviceName` | `string` | D-Bus service name being monitored |
| `_isConnected` | `bool` | Whether the IME service is reachable |

**State Transitions**:
```
Disconnected → Connecting → Connected → Active (focused)
                   ↑            ↓
                   └── Reconnecting ←── Service crashed
```

### IBusInputMethod

| Field | Type | Description |
|-------|------|-------------|
| `_contextPath` | `string` | D-Bus object path for input context |
| `_capabilities` | `uint` | IBus capability flags |

### FcitxInputMethod

| Field | Type | Description |
|-------|------|-------------|
| `_contextPath` | `string` | D-Bus object path for input context |
| `_icWrapper` | `FcitxICWrapper` | Abstraction over fcitx4/fcitx5 differences |
| `_capabilities` | `ulong` | Fcitx capability flags (64-bit for fcitx5) |

### FcitxICWrapper

Unified wrapper for fcitx4 and fcitx5 D-Bus input context proxies.

| Field | Type | Description |
|-------|------|-------------|
| `_modernProxy` | `Fcitx5 IC proxy?` | fcitx5 input context proxy |
| `_legacyProxy` | `Fcitx4 IC proxy?` | fcitx4 input context proxy |

### X11InputMethodDetector (Static)

| Method | Return | Description |
|--------|--------|-------------|
| `DetectAndCreate()` | `IX11InputMethod?` | Check env vars, create appropriate backend |

**Detection rules**:
| Env Var | Value | Result |
|---------|-------|--------|
| `UNO_IM_MODULE` | `"none"` | `null` (XIM fallback) |
| `UNO_IM_MODULE` | `"ibus"` | `IBusInputMethod` |
| `UNO_IM_MODULE` | `"fcitx"/"fcitx5"` | `FcitxInputMethod` |
| `GTK_IM_MODULE` | (same values) | (same results) |
| `QT_IM_MODULE` | (same values) | (same results) |
| `XMODIFIERS` | `"@im=ibus"` | `IBusInputMethod` |
| `XMODIFIERS` | `"@im=fcitx"` | `FcitxInputMethod` |
| (none match) | — | `null` (XIM fallback) |

## Relationships

```
X11KeyboardInputSource
    ├── uses → IX11InputMethod (D-Bus: IBus or Fcitx)
    │              ├── Commit event → X11ImeTextBoxExtension.OnCommittedText()
    │              ├── PreeditChanged event → X11ImeTextBoxExtension.OnPreeditChanged()
    │              └── ForwardKey event → ProcessKeyboardEvent() (re-entry)
    └── uses → XIM (fallback, via Xutf8LookupString)
                   └── existing X11ImeTextBoxExtension path

X11ImeTextBoxExtension (modified)
    ├── IImeTextBoxExtension interface (unchanged)
    ├── delegates to IX11InputMethod for cursor location
    └── receives commit/preedit events from IX11InputMethod
```

## D-Bus XML Interface Definitions (new files)

| File | Interface | Purpose |
|------|-----------|---------|
| `org.freedesktop.IBus.Portal.xml` | `org.freedesktop.IBus.Portal` | IBus portal (CreateInputContext) |
| `org.freedesktop.IBus.InputContext.xml` | `org.freedesktop.IBus.InputContext` | IBus IC methods + signals |
| `org.fcitx.Fcitx.InputMethod1.xml` | `org.fcitx.Fcitx.InputMethod1` | Fcitx5 (CreateInputContext) |
| `org.fcitx.Fcitx.InputContext1.xml` | `org.fcitx.Fcitx.InputContext1` | Fcitx5 IC methods + signals |
| `org.fcitx.Fcitx.InputMethod.xml` | `org.fcitx.Fcitx.InputMethod` | Fcitx4 (CreateICv3) |
| `org.fcitx.Fcitx.InputContext.xml` | `org.fcitx.Fcitx.InputContext` | Fcitx4 IC methods + signals |
