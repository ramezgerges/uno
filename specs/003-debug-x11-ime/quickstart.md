# Quickstart: D-Bus IME Support for X11

## What This Feature Does

Adds IBus and fcitx5 D-Bus clients as primary IME backends for Uno Platform's X11/Skia backend, keeping XIM as fallback. Enables reliable CJK (Chinese, Japanese, Korean) text input on Linux.

## Architecture Overview

```
Key Event → X11InputMethodDetector selects backend:
  ├── IBusInputMethod (D-Bus) ──→ org.freedesktop.portal.IBus
  ├── FcitxInputMethod (D-Bus) ─→ org.freedesktop.portal.Fcitx
  └── No D-Bus IME detected ──→ XIM fallback (existing code)

IME Response:
  ├── CommitText signal ──→ X11ImeTextBoxExtension.OnCommittedText()
  ├── PreeditChanged ────→ X11ImeTextBoxExtension.OnPreeditChanged()
  └── ForwardKey ────────→ X11KeyboardInputSource.ProcessKeyboardEvent()
```

XIM is kept as fallback for systems without D-Bus IME (matching Avalonia's architecture).

## New Files

```
src/Uno.UI.Runtime.Skia.X11/
├── IME/
│   ├── IX11InputMethod.cs              # Interface for all IME backends
│   ├── X11InputMethodDetector.cs       # Env var detection + factory
│   ├── IBusInputMethod.cs              # IBus D-Bus client
│   ├── FcitxInputMethod.cs             # Fcitx D-Bus client
│   └── FcitxICWrapper.cs               # Fcitx4/5 abstraction
├── dbus-interfaces/
│   ├── org.freedesktop.IBus.Portal.xml         # (new, includes InputContext + Service)
│   ├── org.fcitx.Fcitx.InputMethod1.xml       # (new)
│   ├── org.fcitx.Fcitx.InputContext1.xml       # (new)
│   ├── org.fcitx.Fcitx.InputMethod.xml         # (new, fcitx4 legacy)
│   └── org.fcitx.Fcitx.InputContext.xml        # (new, fcitx4 legacy)
```

## Modified Files

| File | Change |
|------|--------|
| `X11ImeTextBoxExtension.cs` | Rewritten — delegates to IX11InputMethod instead of XIM |
| `X11KeyboardInputSource.cs` | Route through IX11InputMethod first; XIM as fallback |
| `X11XamlRootHost.x11events.cs` | Conditional XFilterEvent (XIM-only mode) |
| `Uno.UI.Runtime.Skia.X11.csproj` | Add D-Bus XML interface AdditionalFiles |

## How to Test

### Headless (CI-compatible)
```bash
# Start virtual display + window manager
Xvfb :42 -screen 0 1280x720x24 -listen tcp -nolisten unix &
export DISPLAY=localhost:42
eval "$(dbus-launch --sh-syntax)"
fluxbox &

# Start fcitx5
export GTK_IM_MODULE=fcitx
export XMODIFIERS="@im=fcitx"
fcitx5 -D --disable wayland,waylandim &
fcitx5-remote -s pinyin

# Build and run SamplesApp
cd src/SamplesApp/SamplesApp.Skia.Generic
dotnet run &

# Test English input
xdotool mousemove 100 89 && xdotool click 1
xdotool type --delay 150 "hello"

# Test Pinyin → Chinese commit
xdotool key ctrl+space        # toggle to Pinyin
xdotool key n i space          # type "ni" → commit 你
```

### With IBus (Ubuntu/Fedora default)
```bash
export GTK_IM_MODULE=ibus
export XMODIFIERS=@im=ibus
ibus-daemon -drx
# Run SamplesApp, focus TextBox, switch to Pinyin (Ctrl+Space), type "nihao"
```

### With fcitx5
```bash
export GTK_IM_MODULE=fcitx
export XMODIFIERS=@im=fcitx
fcitx5 -d
# Run SamplesApp, focus TextBox, switch to Pinyin (Ctrl+Space), type "nihao"
```

### Disable IME
```bash
export UNO_IM_MODULE=none
# Run SamplesApp — no IME, direct keyboard only
```

## Key Design Decisions

1. **D-Bus as primary**: XIM commit was broken; D-Bus delivers text as explicit string signals
2. **XIM as fallback**: Kept for systems without D-Bus IME, matching Avalonia's architecture
3. **Tmds.DBus**: Already a dependency; no new packages needed
4. **Avalonia reference**: Port from battle-tested Avalonia implementation
5. **Async key handling**: D-Bus calls are async; key events queued for processing
6. **Headless testable**: D-Bus IME works in Xvfb (unlike XIM), enabling CI validation
