# Data Model: Android IME Composition Events

## Composition State Machine

```
[Idle]
  │
  │ ComposingStart/ComposingEnd transition from (-1,-1) to valid range
  │
  ▼ CompositionStarted
[Composing]
  │
  ├── ComposingStart/ComposingEnd change while still valid
  │   ▼ CompositionUpdated (with composing text)
  │   └── stays in [Composing]
  │
  ├── ComposingStart/ComposingEnd transition to (-1,-1) AND text changed
  │   ▼ CompositionCompleted (with committed text)
  │   ▼ CompositionEnded
  │   └── returns to [Idle]
  │
  └── ComposingStart/ComposingEnd transition to (-1,-1) AND text unchanged
      ▼ CompositionEnded (cancel)
      └── returns to [Idle]
```

## Entity: AndroidImeTextBoxExtension

| Field | Type | Description |
|-------|------|-------------|
| _isComposing | bool | Whether a composition session is active |
| _lastComposingStart | int | Start of composing region at last notification |
| _lastComposingEnd | int | End of composing region at last notification |
| _compositionStartTextLength | int | TextBox.Text.Length when composition started (to detect commit) |

## Entity: Composition Notification (callback from TextInputConnection)

| Field | Type | Description |
|-------|------|-------------|
| composingStart | int | Start index of composing span (-1 if none) |
| composingEnd | int | End index of composing span (-1 if none) |
| composingText | string? | Text within the composing region (null if no region) |
| fullText | string | Full text of the editable |

## Relationships

```
AndroidHost
  └── registers → AndroidImeTextBoxExtension (singleton, IImeTextBoxExtension)

TextBox.skia.cs
  └── subscribes to → AndroidImeTextBoxExtension events
  └── calls → StartImeSession(textBox) / EndImeSession()

AndroidImeTextBoxExtension
  └── connects to → TextInputConnection (via TextInputPlugin on session start)
  └── monitors → ObservableEditingState composing region (via callback)
  └── fires → CompositionStarted / CompositionUpdated / CompositionCompleted / CompositionEnded

TextInputConnection
  └── receives → SetComposingText / CommitText / FinishComposingText from Android IME
  └── updates → ObservableEditingState (composing spans)
  └── notifies → DidChangeEditingState callback (composingRegionChanged)
  └── NEW: notifies → AndroidImeTextBoxExtension via composition callback
```
