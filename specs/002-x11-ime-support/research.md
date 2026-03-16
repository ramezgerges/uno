# Research: X11 IME Support

## Decision 1: XIM Protocol for IME Integration

**Decision**: Use XIM (X Input Method) protocol via Xlib functions (XOpenIM, XCreateIC, XmbLookupString, XFilterEvent).

**Rationale**: XIM is the standard X11 input method protocol. All major Linux IME frameworks (IBus, Fcitx, SCIM) implement XIM compatibility. The existing codebase already uses Xlib directly via P/Invoke, so XIM fits naturally into the existing architecture.

**Alternatives considered**:
- **DBus direct integration with IBus/Fcitx**: Too framework-specific, would require separate implementations per IME framework.
- **GTK IM context**: Would add a GTK dependency, inappropriate for this project.
- **Wayland text-input protocol**: X11-only scope; Wayland is a separate target.

## Decision 2: Preedit Rendering via Callbacks (On-The-Spot)

**Decision**: Use XIM preedit callbacks (on-the-spot style) where the application renders composition text inline in the TextBox, matching the Win32 behavior.

**Rationale**: The managed TextBox layer already handles composition text rendering (underlines, text insertion via `ProcessTextInput()`). The Win32 implementation suppresses the default IME composition window and renders inline. The X11 implementation should do the same using XIM preedit callbacks, which tell the application to render preedit text itself.

**Alternatives considered**:
- **Over-the-spot**: IME draws its own floating window near the cursor. Simpler but inconsistent with Win32 behavior and doesn't integrate with TextBox rendering.
- **Root window**: IME draws at a fixed position. Poor UX.
- **Off-the-spot**: Application provides a dedicated area. Doesn't match WinUI behavior.

## Decision 3: XIC Per Window, Singleton Extension

**Decision**: Follow the Win32 pattern — singleton `X11ImeTextBoxExtension` registered via `ApiExtensibility`, with XIC (X Input Context) created per window.

**Rationale**: The Win32 implementation uses a singleton pattern with `_activeImeTextBox` tracking. Each X11 window needs its own XIC for proper focus handling, but the extension interface itself is a singleton that routes events to the active TextBox.

**Alternatives considered**:
- **XIC per TextBox**: Overly complex, as XIM focus management is per-window. The managed layer already handles multi-TextBox routing.

## Decision 4: XFilterEvent Integration Point

**Decision**: Call `XFilterEvent()` at the top of the event loop before dispatching events. If it returns true, the event was consumed by the IME and should be skipped.

**Rationale**: `XFilterEvent()` is the standard mechanism for IME event filtering in X11. It must be called for every event before processing. The current event loop in `X11XamlRootHost.x11events.cs` fetches events via `XNextEvent` and dispatches by type — `XFilterEvent` should be called immediately after `XNextEvent`.

## Decision 5: Remove @im=none and Enable IME

**Decision**: Change `XSetLocaleModifiers("@im=none")` to `XSetLocaleModifiers("")` in `X11ApplicationHost.cs` to enable the system's default input method.

**Rationale**: The current code explicitly disables IME. The empty string tells Xlib to use the default input method from the `XMODIFIERS` environment variable, which is how IBus, Fcitx, and other IME frameworks integrate.

## Decision 6: XmbLookupString for Text Input

**Decision**: Replace `XLookupString` with `XmbLookupString` (or `Xutf8LookupString`) in `X11KeyboardInputSource.ProcessKeyboardEvent` when an XIC is available.

**Rationale**: `XLookupString` cannot handle multi-byte input or IME composition. `XmbLookupString` works with the XIC to return either a committed string or a keysym, and reports the lookup status (XLookupChars, XLookupKeySym, XLookupBoth, XBufferOverflow). When the status is `XLookupChars` or `XLookupBoth`, the committed text should be used. When the IME is composing, `XFilterEvent` will consume the key events and the lookup will return nothing.

## Decision 7: Candidate Window Positioning

**Decision**: Use `XSetICValues` with `XNSpotLocation` to set the preedit position, which tells the IME where to display the candidate window.

**Rationale**: For on-the-spot preedit, the application must tell the IME where the text cursor is so the candidate window appears at the right location. This is analogous to `ImmSetCompositionWindow` / `ImmSetCandidateWindow` on Win32.

## Key Technical Findings

### Current State of X11 Keyboard Input
- File: `src/Uno.UI.Runtime.Skia.X11/Devices/Input/X11KeyboardInputSource.cs`
- Uses `XLookupString()` only — no composition support
- Has a TODO comment: "Composing inputs"
- Key events come from the Root X11 window (KeyPress/KeyRelease mask is on RootEventsMask)

### Current IME State
- File: `src/Uno.UI.Runtime.Skia.X11/Hosting/X11ApplicationHost.cs`
- IME is **explicitly disabled**: `XSetLocaleModifiers("@im=none")`
- Comment acknowledges this disables IME

### Event Loop Architecture
- File: `src/Uno.UI.Runtime.Skia.X11/Hosting/X11XamlRootHost.x11events.cs`
- Two threads: one for Root window, one for Top window
- Events fetched with `XNextEvent`, dispatched by type in a switch
- KeyPress/KeyRelease handled on root window thread
- Uses managed locking (`X11Helper.XLock`) around X11 calls

### Missing P/Invoke Bindings Needed
- `XOpenIM` / `XCloseIM`
- `XCreateIC` / `XDestroyIC`
- `XSetICFocus` / `XUnsetICFocus`
- `XmbLookupString` or `Xutf8LookupString`
- `XFilterEvent`
- `XSetICValues` (for spot location)
- `XGetICValues` (optional, for querying)
