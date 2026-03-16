# Research: IME Support for Uno Platform

**Branch**: `001-ime-support` | **Date**: 2026-03-16

## Decision 1: Platform Abstraction Pattern

**Decision**: Use `ApiExtensibility` with a new `IImeTextBoxExtension` interface registered per-platform.

**Rationale**: This matches the existing pattern used by `ITextBoxNotificationsProviderSingleton`, `IClipboardExtension`, `IApplicationViewExtension`, and dozens of other platform services. The `ApiExtensibility.Register()` call in `Win32Host`'s static constructor ensures the Win32 implementation is loaded when running on Win32 Skia.

**Alternatives considered**:
- Direct platform-specific partial classes on TextBox: Rejected because TextBox already uses `ApiExtensibility` for its notifications provider, and the interface approach provides cleaner separation between managed composition state and platform-specific IME handling.
- MCP/plugin pattern: Overkill for an internal platform service.

## Decision 2: Win32 IME Message Handling Strategy

**Decision**: Intercept `WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`, and `WM_IME_ENDCOMPOSITION` messages in `Win32WindowWrapper.WndProcInner()`, and suppress `WM_CHAR` during active composition to prevent duplicate text insertion.

**Rationale**: Currently, Uno's Win32 backend passes all IME messages to `DefWindowProc`, which causes the OS to handle composition internally. The `OnKey()` method peeks for `WM_CHAR` to extract typed characters, but during IME composition, `WM_CHAR` messages are generated for committed text (not composition text). To properly support composition:
1. `WM_IME_STARTCOMPOSITION` → signals start of composition
2. `WM_IME_COMPOSITION` with `GCS_COMPSTR` → provides composition string updates
3. `WM_IME_COMPOSITION` with `GCS_RESULTSTR` → provides committed text
4. `WM_IME_ENDCOMPOSITION` → signals end of composition
5. During composition, `WM_CHAR` for committed text must be suppressed to avoid double-insertion (the extension handles committed text directly)

**Alternatives considered**:
- Text Services Framework (TSF): More modern but significantly more complex to implement. IMM32 is sufficient for TextBox composition support and is the approach used by most Win32 applications.
- Continue relying on `DefWindowProc` + `WM_CHAR`: Cannot support composition string display or TextComposition events.

## Decision 3: Composition String Rendering

**Decision**: Use the existing TextDecoration (underline) rendering in `UnicodeText.skia.cs` to draw composition underlines, managed through a composition range tracked on TextBoxView.

**Rationale**: The Skia text rendering pipeline already supports underline decorations via `spell-check underline rendering` in `UnicodeText.skia.cs`. Selection highlighting is also already implemented via `TextHighlighter`. The composition underline can be rendered similarly by tracking the composition range (start index, length) and drawing an underline decoration during the render pass.

**Alternatives considered**:
- Custom composition overlay: Unnecessary complexity since underline rendering already exists.
- Platform-native composition window: Win32's default composition window appears in a separate popup, which doesn't match WinUI's inline composition behavior.

## Decision 4: Composition State Management Location

**Decision**: Track composition state (isComposing, compositionStart, compositionLength, compositionText) in the managed TextBox layer (TextBox.skia.cs), with the platform extension providing composition events from the native IME.

**Rationale**: The TextBox is responsible for text content, selection, and rendering. Composition state affects all of these. The platform extension's role is limited to: (1) intercepting native IME messages, (2) extracting composition/committed text, (3) forwarding events to the managed layer via the interface. The managed layer handles: (1) updating text content, (2) tracking composition range, (3) firing WinUI TextComposition events, (4) triggering composition underline rendering.

**Alternatives considered**:
- Full state management in platform extension: Would duplicate TextBox logic and complicate cross-platform consistency.

## Decision 5: IME Candidate Window Positioning

**Decision**: Extend the existing `Win32ImeCaretManager` to continue managing candidate window positioning via IMM32 `ImmSetCompositionWindow`/`ImmSetCandidateWindow` calls.

**Rationale**: This infrastructure already works. The `Win32TextBoxNotificationsProviderSingleton` already updates the caret position on focus and selection changes, which positions the candidate window correctly. No changes needed for basic positioning.

## Key Technical Findings

### Current Win32 Text Input Flow
```
WM_KEYDOWN → OnKey() peeks WM_CHAR → KeyEventArgs.UnicodeKey
→ InputManager.KeyboardManager → KeyRoutedEventArgs
→ TextBox.OnKeyDownSkia() → ProcessTextInput()
```

### IME Composition Flow (to implement)
```
WM_IME_STARTCOMPOSITION → Extension.OnCompositionStarted()
→ TextBox tracks composition state, fires TextCompositionStarted

WM_IME_COMPOSITION(GCS_COMPSTR) → Extension.OnCompositionUpdated(text)
→ TextBox updates inline composition text + underline, fires TextCompositionChanged

WM_IME_COMPOSITION(GCS_RESULTSTR) → Extension.OnCompositionCompleted(text)
→ TextBox commits text via ProcessTextInput(), fires TextCompositionEnded

WM_IME_ENDCOMPOSITION → Extension.OnCompositionEnded()
→ TextBox clears composition state
```

### Existing Infrastructure to Leverage
- `Win32ImeCaretManager`: Already positions IME candidate windows via IMM32
- `Win32TextBoxNotificationsProviderSingleton`: Already bridges TextBox focus/selection to Win32
- `UnicodeText.skia.cs spell-check underline rendering`: Already renders underline text decorations
- `TextHighlighter` / selection rendering: Already handles range-based visual overlays
- `ApiExtensibility`: Registration pattern in `Win32Host` static constructor
