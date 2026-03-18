# Research: Android IME Composition Events

## Decision 1: Where to detect composition state transitions

**Decision**: Listen for `composingRegionChanged` in the existing `ObservableEditingState.DidChangeEditingState` callback, which already fires on `TextInputConnection`'s `DidChangeEditingState` listener.

**Rationale**: `ObservableEditingState` already tracks `ComposingStart`/`ComposingEnd` via `BaseInputConnection.GetComposingSpanStart/End()` and fires `DidChangeEditingState(textChanged, selectionChanged, composingRegionChanged)` after every batch edit. This is the authoritative source for composition region changes — no need to duplicate tracking.

**Alternatives considered**:
- Override `SetComposingText`/`CommitText`/`FinishComposingText` individually in `TextInputConnection`: More granular but duplicates logic already in `ObservableEditingState`. Would require distinguishing commit vs. update, which the composing span already encodes (composing region present = composing, absent = committed).
- Add a separate listener on `ObservableEditingState`: Unnecessary complexity since `TextInputConnection` already has a `DidChangeEditingState` listener wired up.

## Decision 2: How to bridge composition state to IImeTextBoxExtension events

**Decision**: Create `AndroidImeTextBoxExtension` implementing `IImeTextBoxExtension`. The extension monitors composing region transitions:
- No composing region → composing region present = fire `CompositionStarted`
- Composing region present → composing region changed = fire `CompositionUpdated` with composing text
- Composing region present → composing region removed (and text changed) = fire `CompositionCompleted` + `CompositionEnded`
- Composing region present → composing region removed (text unchanged) = fire `CompositionEnded` (cancel)

**Rationale**: This maps Android's span-based composing model directly to the Start→Update→Complete→End lifecycle used by Win32 and X11. The composing span is the single source of truth.

**Alternatives considered**:
- Modify `TextInputConnection` to fire events directly: Violates separation of concerns. `TextInputConnection` is a Flutter port focused on Android InputConnection protocol; IME extension is an Uno Platform abstraction.
- Use the `TextEditingDelta` list: Over-engineered for this use case. Deltas track every text mutation; we only need composing region transitions.

## Decision 3: How TextInputConnection notifies the IME extension

**Decision**: Add a callback/delegate property on `TextInputConnection` (e.g., `CompositionStateChanged`) that `AndroidImeTextBoxExtension` subscribes to. The callback receives composing region start/end and the composing text string, fired from the existing `DidChangeEditingState` listener when `composingRegionChanged` is true.

**Rationale**: Minimal change to `TextInputConnection`. The existing `DidChangeEditingState` callback already detects composing region changes. Adding one more notification from there keeps the change localized.

**Alternatives considered**:
- Have `AndroidImeTextBoxExtension` register its own listener on `ObservableEditingState`: Would require exposing `ObservableEditingState` publicly from `TextInputConnection`, increasing coupling.
- Poll `ComposingStart`/`ComposingEnd` on each key event: Fragile, misses batch edits, unnecessary with callback available.

## Decision 4: Registration and lifecycle

**Decision**: Register `AndroidImeTextBoxExtension` as a singleton via `ApiExtensibility.Register(typeof(IImeTextBoxExtension), ...)` in `AndroidHost.cs`. The extension's `StartImeSession`/`EndImeSession` connects/disconnects from the `TextInputPlugin`'s active `TextInputConnection`.

**Rationale**: Matches the pattern used by Win32 (`Win32Host.cs` registers `Win32ImeTextBoxExtension`) and X11 (`X11ApplicationHost.cs` registers `X11ImeTextBoxExtension`). Both are singletons.

**Alternatives considered**:
- Per-TextBox extension instances: Unnecessary; `IImeTextBoxExtension` is designed as a singleton. `TextBox.skia.cs` creates it once via `ApiExtensibility.CreateInstance` and routes events via `_activeImeTextBox`.

## Decision 5: Testing environment

**Decision**: Use Android emulator with Gboard Pinyin IME and `SamplesApp.Skia.netcoremobile` built with `-p:UnoTargetFrameworkOverride=net10.0-android`.

**Rationale**: The SamplesApp already includes TextBox samples. Gboard is the default Android keyboard and supports Pinyin input. The existing debug sample page (`TextBox_X11_IME_Debug`) can be reused since it uses `#if HAS_UNO` and `OperatingSystem.IsLinux()` guards — extending it for Android is straightforward.

**Alternatives considered**:
- Automated CI testing with adb shell input: Cannot simulate IME composition (adb `input text` bypasses InputConnection entirely). Manual testing with a real CJK keyboard is required for composition verification.
- Physical device: Works but emulator is more accessible for development.
