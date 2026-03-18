# Implementation Plan: D-Bus IME Support for X11

**Branch**: `003-debug-x11-ime` | **Date**: 2026-03-17 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/003-debug-x11-ime/spec.md`

## Summary

Add IBus and fcitx5 D-Bus clients as primary IME backends for Uno Platform's X11/Skia backend. The existing XIM implementation is cleaned up and kept as fallback for systems without D-Bus IME, matching Avalonia's architecture. D-Bus delivers commit text as explicit string signals, which is reliable and testable in headless environments.

## Technical Context

**Language/Version**: C# / .NET 10.0 (multi-target with net9.0)
**Primary Dependencies**: Tmds.DBus.Protocol v0.90.2, Tmds.DBus.Generator v0.90.2 (both already referenced)
**Storage**: N/A
**Testing**: Headless SamplesApp on Xvfb + fcitx5 with xdotool; runtime tests via Skia desktop
**Target Platform**: Linux X11 (Skia backend)
**Project Type**: Library (Uno.UI.Runtime.Skia.X11)
**Performance Goals**: Zero overhead when no IME active; < 5ms key event processing latency via D-Bus
**Constraints**: Must gracefully handle missing D-Bus session bus (falls back to XIM)
**Scale/Scope**: ~8 new files, ~3 modified files, ~6 new D-Bus XML interface definitions

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Notes |
|-----------|--------|-------|
| I. WinUI API Fidelity | PASS | No public API changes; IME is internal platform plumbing |
| II. Cross-Platform Parity | PASS | X11-specific code isolated in platform-suffixed project; no impact on other platforms |
| III. Test-First Quality Gates | PASS | Headless testing with Xvfb + fcitx5 + SamplesApp validates D-Bus commit path |
| IV. Performance Discipline | PASS | D-Bus IME only activated when env vars indicate; zero overhead otherwise |
| V. Generated Code Boundaries | PASS | D-Bus proxies generated from XML definitions via Tmds.DBus.Generator |
| VI. Backward Compatibility | PASS | XIM kept as fallback; D-Bus is additive |
| VII. WinUI Implementation Alignment | N/A | IME on Linux has no WinUI equivalent; this is platform-specific |

## Project Structure

### Documentation (this feature)

```text
specs/003-debug-x11-ime/
├── plan.md              # This file
├── research.md          # Phase 0 output - protocol details, decisions
├── data-model.md        # Phase 1 output - entity relationships
├── quickstart.md        # Phase 1 output - setup and testing guide
├── contracts/           # Phase 1 output - interface definitions
│   ├── IX11InputMethod.cs
│   └── X11InputMethodDetector.cs
└── tasks.md             # Phase 2 output (created by /speckit.tasks)
```

### Source Code (repository root)

```text
src/Uno.UI.Runtime.Skia.X11/
├── IME/                                        # NEW directory
│   ├── IX11InputMethod.cs                      # Core interface
│   ├── X11InputMethodDetector.cs               # Detection + factory
│   ├── DBusInputMethodBase.cs                  # Shared D-Bus logic
│   ├── IBusInputMethod.cs                      # IBus client
│   ├── FcitxInputMethod.cs                     # Fcitx client
│   └── FcitxICWrapper.cs                       # Fcitx4/5 abstraction
├── dbus-interfaces/                            # EXISTING directory
│   ├── org.freedesktop.IBus.Portal.xml         # NEW
│   ├── org.freedesktop.IBus.InputContext.xml   # NEW
│   ├── org.fcitx.Fcitx.InputMethod1.xml       # NEW
│   ├── org.fcitx.Fcitx.InputContext1.xml       # NEW
│   ├── org.fcitx.Fcitx.InputMethod.xml         # NEW (fcitx4 legacy)
│   └── org.fcitx.Fcitx.InputContext.xml        # NEW (fcitx4 legacy)
├── UI/Xaml/Controls/TextBox/
│   └── X11ImeTextBoxExtension.cs               # MODIFIED - delegates to IX11InputMethod, XIM as fallback
├── Devices/Input/
│   └── X11KeyboardInputSource.cs               # MODIFIED - route through IX11InputMethod first, XIM fallback
├── Hosting/
│   └── X11XamlRootHost.x11events.cs            # MODIFIED - conditional XFilterEvent for XIM-only mode
└── Uno.UI.Runtime.Skia.X11.csproj              # MODIFIED (add XML files)
```

**Structure Decision**: All new D-Bus IME code goes in `src/Uno.UI.Runtime.Skia.X11/IME/` subdirectory. Existing XIM code in `X11ImeTextBoxExtension` and `X11KeyboardInputSource` is refactored behind the `IX11InputMethod` interface and cleaned up (remove experimental debug logging/workarounds), but preserved as the fallback path.

## IME Backend Priority

Matches Avalonia's detection and fallback chain:

```
1. Check env vars: UNO_IM_MODULE → GTK_IM_MODULE → QT_IM_MODULE → XMODIFIERS
2. If "ibus"  → IBusInputMethod (D-Bus)
3. If "fcitx" → FcitxInputMethod (D-Bus)
4. If "none"  → XIM fallback
5. If D-Bus connection fails → XIM fallback
6. If no env var matches → XIM fallback
```

## Headless Test Environment

Validated during debugging — the D-Bus IME path works in headless Xvfb (unlike XIM):

```bash
# Environment setup
Xvfb :42 -screen 0 1280x720x24 -listen tcp -nolisten unix &
export DISPLAY=localhost:42
eval "$(dbus-launch --sh-syntax)"
export GTK_IM_MODULE=fcitx
export XMODIFIERS="@im=fcitx"
fcitx5 -D --disable wayland,waylandim &
fluxbox &  # window manager for proper focus

# Switch to Pinyin
fcitx5-remote -s pinyin

# Build and run SamplesApp
cd src/SamplesApp/SamplesApp.Skia.Generic
dotnet run > /tmp/app.log 2>&1 &

# Test with xdotool
xdotool mousemove 100 89 && xdotool click 1  # click TextBox
xdotool type --delay 150 "hello"               # English
xdotool key ctrl+space                         # toggle Pinyin
xdotool key n i space                          # type "ni" → commit 你
```

**Key fact**: Firefox successfully commits Chinese (你) in this exact environment using fcitx5's D-Bus frontend. This confirms D-Bus IME commit works in headless Xvfb — the issue was only with XIM's `Xutf8LookupString`.

## Implementation Phases

### Phase 1: D-Bus XML Interfaces + Generated Proxies

Add D-Bus XML interface definitions for IBus and Fcitx (both v4 and v5). Register them in `.csproj` for Tmds.DBus.Generator to produce C# proxy classes.

**Files**: 6 XML files + .csproj modification
**Validation**: Project compiles with generated proxy classes accessible

### Phase 2: IX11InputMethod Interface + Detector

Create the core `IX11InputMethod` interface and `X11InputMethodDetector` that reads environment variables and creates the appropriate backend. Wrap existing XIM code behind the same interface as `XimInputMethod`.

**Files**: `IX11InputMethod.cs`, `X11InputMethodDetector.cs`
**Validation**: Detector returns correct backend type based on env vars; XIM path still works

### Phase 3: IBus D-Bus Client

Implement `IBusInputMethod` — connect to IBus portal, create input context, forward key events, receive commit/preedit signals.

**Files**: `DBusInputMethodBase.cs`, `IBusInputMethod.cs`
**Validation**: Headless test — IBus commit signal delivers Chinese text to SamplesApp TextBox
**Reference**: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/IBus/IBusX11TextInputMethod.cs`

### Phase 4: Fcitx D-Bus Client

Implement `FcitxInputMethod` with `FcitxICWrapper` for fcitx4/fcitx5 compatibility.

**Files**: `FcitxInputMethod.cs`, `FcitxICWrapper.cs`
**Validation**: Headless test — fcitx5 commit signal delivers Chinese text to SamplesApp TextBox
**Reference**: `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/Fcitx/`

### Phase 5: Integration

- Modify `X11KeyboardInputSource` to route key events through `IX11InputMethod` when D-Bus is active, falling back to XIM
- Modify `X11ImeTextBoxExtension` to delegate cursor updates to `IX11InputMethod`
- Make `XFilterEvent` in event loop conditional on XIM-only mode
- Clean up experimental debug logging and event-pumping workarounds from XIM debugging

**Files**: `X11ImeTextBoxExtension.cs`, `X11KeyboardInputSource.cs`, `X11XamlRootHost.x11events.cs`
**Validation**: End-to-end headless test:
  1. English typing works (SamplesApp TextBox)
  2. Pinyin composition → Chinese commit works (fcitx5, D-Bus path)
  3. Toggle between English/Pinyin works
  4. `UNO_IM_MODULE=none` → falls back to XIM, English typing works

### Phase 6: Headless Test Script + Cleanup

Create automated test script for CI that sets up Xvfb + fcitx5 + SamplesApp and validates IME input. Update `run-ime-test.sh`.

**Validation**: Script runs end-to-end, captures screenshots, reports pass/fail

## Key References

| Resource | Location |
|----------|----------|
| Avalonia IBus impl | `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/IBus/IBusX11TextInputMethod.cs` |
| Avalonia Fcitx impl | `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/Fcitx/FcitxX11TextInputMethod.cs` |
| Avalonia detection | `/tmp/avalonia/src/Avalonia.FreeDesktop/DBusIme/X11DBusImeHelper.cs` |
| Avalonia IME orchestration | `/tmp/avalonia/src/Avalonia.X11/X11Window.Ime.cs` |
| Uno existing XIM | `src/Uno.UI.Runtime.Skia.X11/UI/Xaml/Controls/TextBox/X11ImeTextBoxExtension.cs` |
| Uno keyboard source | `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs` |
| Uno event loop | `src/Uno.UI.Runtime.Skia.X11/Hosting/X11XamlRootHost.x11events.cs` |
| Research findings | `specs/003-debug-x11-ime/research.md` |
