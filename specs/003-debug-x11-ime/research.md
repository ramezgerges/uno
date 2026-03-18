# Research: D-Bus IME Support for X11

## Decision 1: IME Protocol

**Decision**: Implement IBus and fcitx5 D-Bus clients as primary IME backends, keeping XIM as fallback (matching Avalonia's architecture).

**Rationale**:
- XIM (X Input Method) is a 1990s protocol with known fragility — commit text delivery via `Xutf8LookupString` fails in some environments (confirmed with native C test program on Xvfb + fcitx5/IBus).
- All modern Linux toolkits have migrated to D-Bus IME: GTK, Qt, Chromium, Avalonia, SDL.
- IBus is the default IME framework on Ubuntu/Fedora/GNOME (majority of Linux desktop users).
- fcitx5 is dominant among CJK-focused users (Arch, Manjaro, some Debian derivatives).
- D-Bus delivers commit text as explicit string payloads — reliable and testable in headless environments.
- XIM is kept as fallback for edge cases (no D-Bus session bus, unknown IME framework), matching Avalonia's approach.

**Alternatives considered**:
- XIM only (current state): Broken commit path in some environments; confirmed by pure C test.
- No XIM fallback: Would leave users without D-Bus unable to use IME at all.
- GTK IM module (Flutter's approach): Requires GTK dependency; Uno doesn't use GTK.
- Wayland text-input protocol: Only for Wayland, not X11.

## Decision 2: D-Bus Library

**Decision**: Use `Tmds.DBus.Protocol` + `Tmds.DBus.Generator` (already in Uno's X11 project).

**Rationale**:
- Uno's X11 project (`Uno.UI.Runtime.Skia.X11.csproj`) already references `Tmds.DBus.Protocol` v0.90.2 and `Tmds.DBus.Generator` v0.90.2 for file chooser/settings portals.
- Avalonia uses the same library family (`Tmds.DBus.Protocol` v0.90.3 + `Tmds.DBus.SourceGenerator` v0.0.22).
- No new NuGet dependencies needed — just add D-Bus XML interface definitions.

**Alternatives considered**:
- Raw D-Bus sockets: Too low-level, error-prone.
- Dbus-sharp: Abandoned, not .NET 8+ compatible.

## Decision 3: Reference Implementation

**Decision**: Port from Avalonia's `Avalonia.FreeDesktop/DBusIme/` implementation, adapted to Uno's architecture.

**Rationale**:
- Avalonia is the closest comparable .NET cross-platform UI framework without GTK dependency.
- Battle-tested with IBus and fcitx5 on real Linux desktops.
- Same D-Bus library, same platform (X11), similar architecture (Skia rendering).
- Flutter uses GTK IM modules (not applicable to Uno).

## Decision 4: Detection Priority

**Decision**: Check environment variables in order: `UNO_IM_MODULE` → `GTK_IM_MODULE` → `QT_IM_MODULE` → `XMODIFIERS`. If no D-Bus IME detected, IME is disabled (keyboard still works for direct input).

**Rationale**: Follows Avalonia's approach (which mirrors GTK/Qt convention). Adding `UNO_IM_MODULE` allows Uno-specific override without affecting other apps.

**Detection values**: `"ibus"` → IBus, `"fcitx"` or `"fcitx5"` → Fcitx, `"none"` → disable IME. If no env var matches, IME is disabled but keyboard works normally.

## Decision 5: Architecture

**Decision**: Create an `IX11InputMethod` interface with three implementations: `IBusInputMethod`, `FcitxInputMethod`, and `XimInputMethod` (wrapping existing XIM code as fallback).

**Rationale**: Clean separation allows independent development/testing of each backend while sharing the interface consumed by `X11KeyboardInputSource` and `X11ImeTextBoxExtension`. XIM fallback ensures IME works (at least partially) on systems without D-Bus.

## Protocol Details (from Avalonia analysis)

### IBus D-Bus Protocol
- **Bus**: Session bus, service `org.freedesktop.portal.IBus`
- **Create context**: `org.freedesktop.IBus.Portal.CreateInputContext(name) → object_path`
- **Process key**: `InputContext.ProcessKeyEvent(keyval: uint, keycode: uint, state: uint) → bool`
- **Commit signal**: `CommitText(variant)` — text extracted from struct at index 2
- **Preedit signals**: `UpdatePreeditText(variant, cursor_pos, visible)`, `ShowPreeditText()`, `HidePreeditText()`
- **Cursor**: `SetCursorLocation(x: int, y: int, w: int, h: int)` in absolute screen pixels
- **Focus**: `FocusIn()` / `FocusOut()`
- **Capabilities**: `SetCapabilities(caps: uint)` — `CapFocus = 1 << 3`, `CapPreeditText = 1 << 0`
- **Modifier masks**: `ShiftMask = 1<<0`, `ControlMask = 1<<2`, `Mod1Mask = 1<<3`, `ReleaseMask = 1<<30`

### Fcitx5 D-Bus Protocol
- **Bus**: Session bus, service `org.freedesktop.portal.Fcitx`
- **Create context**: `org.fcitx.Fcitx.InputMethod1.CreateInputContext(args: array[(string,string)]) → (path, keydata)`
- **Process key**: `InputContext1.ProcessKeyEvent(keyval: uint, keycode: uint, state: uint, isRelease: bool, time: uint) → bool`
- **Commit signal**: `CommitString(str: string)` — direct string
- **Preedit signal**: `UpdateFormattedPreedit(parts: array[(string,int)], cursorpos: int)` — cursor is **UTF-8 byte offset**
- **Cursor**: `SetCursorRect(x: int, y: int, w: int, h: int)`
- **Focus**: `FocusIn()` / `FocusOut()`
- **Capabilities**: `SetCapability(caps: uint64)` — note: 64-bit vs fcitx4's 32-bit

### Fcitx4 D-Bus Protocol (legacy)
- **Bus**: Session bus, service `org.fcitx.Fcitx`
- **Create context**: `org.fcitx.Fcitx.InputMethod.CreateICv3(name: string, pid: int) → (icid: int, ...)`
- **Process key**: `InputContext.ProcessKeyEvent(keyval, keycode, state, type: int, time: uint) → int`
- Same signals as fcitx5, differences: `type` is int (0=press, 1=release) instead of bool; `SetCapacity` uses uint

## Key Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| No session bus (minimal Linux) | XIM fallback; keyboard still works for direct input |
| D-Bus signals on background thread | Use existing `QueueAction` pattern to dispatch to UI thread |
| IBus service restarts | Monitor `NameOwnerChanged` signal for reconnection (Avalonia pattern) |
| fcitx4 vs fcitx5 differences | Abstraction wrapper (Avalonia's `FcitxICWrapper` pattern) |
| Preedit cursor encoding (fcitx UTF-8 bytes vs chars) | Convert with `Encoding.UTF8.GetCharCount()` |

## Existing Infrastructure in Uno's X11 Project

- `Tmds.DBus.Protocol` v0.90.2 already referenced
- `Tmds.DBus.Generator` v0.90.2 already referenced
- D-Bus XML interface pattern established (`dbus-interfaces/` directory with `AdditionalFiles`)
- `X11XamlRootHost.QueueAction()` for dispatching to UI thread
- `X11ImeTextBoxExtension` with `IImeTextBoxExtension` interface for composition events
- `X11KeyboardInputSource.ProcessKeyboardEvent()` as the key event entry point

## Previous Research Findings (from debugging sessions)

### Headless Testing
- Xvfb + fcitx5 environment confirmed XIM commit bug at native C level
- English typing works via XIM (XLookupChars path)
- Chinese commit text lost: `Xutf8LookupString` returns `XLookupNone` for keycode=0 commit events
- Firefox works in same environment because it uses fcitx5's **D-Bus frontend**, not XIM
- Automated headless testing of D-Bus IME commit SHOULD work (unlike XIM)

### XIM Current State
- XIMPreeditNothing | XIMStatusNothing style (IBus-compatible)
- XIMPreeditCallbacks reverted — IBus accepts but never invokes callbacks
- XLookupChars fix for English typing with IME engines active
