# Quickstart: Android IME Composition Events

## What This Feature Does

Bridges Android's `BaseInputConnection` composition events to Uno Platform's `IImeTextBoxExtension` interface on Skia Android, enabling `TextCompositionStarted`/`Changed`/`Ended` events for CJK text input. This brings Android to parity with Win32 and X11 IME support.

## Architecture Overview

```
Android IME (Gboard, etc.)
  │
  ▼ SetComposingText / CommitText / FinishComposingText
TextInputConnection (BaseInputConnection)
  │
  ▼ DidChangeEditingState(composingRegionChanged=true)
AndroidImeTextBoxExtension (NEW)
  │
  ▼ CompositionStarted / CompositionUpdated / CompositionCompleted / CompositionEnded
TextBox.skia.cs
  │
  ▼ TextCompositionStarted / TextCompositionChanged / TextCompositionEnded
Developer app code
```

## New Files

```
src/Uno.UI.Runtime.Skia.Android/
├── UI/Xaml/Controls/TextBox/
│   └── AndroidImeTextBoxExtension.cs    # IImeTextBoxExtension for Android
```

## Modified Files

| File | Change |
|------|--------|
| `AndroidHost.cs` | Add `ApiExtensibility.Register(typeof(IImeTextBoxExtension), ...)` |
| `TextInputConnection.cs` | Add composition state change callback to notify the IME extension |

## How to Test

### 1. Set up Android Emulator with Pinyin IME

```bash
# Create emulator (if not already present)
$ANDROID_HOME/cmdline-tools/latest/bin/avdmanager create avd \
  --name "ime_test" \
  --package "system-images;android-35;google_apis_playstore;x86_64" \
  --device "pixel_6"

# Start emulator
$ANDROID_HOME/emulator/emulator -avd ime_test -gpu swiftshader_indirect &

# Wait for boot
adb wait-for-device
adb shell 'while [[ -z $(getprop sys.boot_completed) ]]; do sleep 1; done'

# Enable Chinese Pinyin in Gboard
# Navigate: Settings → System → Languages & input → On-screen keyboard → Gboard → Languages
# Add "Chinese (Simplified) - Pinyin"
# (This step requires manual interaction on first setup)
```

### 2. Build and Deploy SamplesApp

```bash
cd src

# Ensure crosstargeting_override.props targets Android
cat > crosstargeting_override.props << 'EOF'
<Project>
  <PropertyGroup>
    <UnoTargetFrameworkOverride>net10.0-android</UnoTargetFrameworkOverride>
  </PropertyGroup>
</Project>
EOF

# Build
dotnet build SamplesApp/SamplesApp.Skia.netcoremobile/SamplesApp.Skia.netcoremobile.csproj \
  -p:UnoTargetFrameworkOverride=net10.0-android \
  -f net10.0-android

# Deploy to emulator
dotnet build SamplesApp/SamplesApp.Skia.netcoremobile/SamplesApp.Skia.netcoremobile.csproj \
  -p:UnoTargetFrameworkOverride=net10.0-android \
  -f net10.0-android \
  -t:Install

# Launch app
adb shell am start -n uno.platform.samplesapp.skia/crc6448f3b0362cbf4bc9.MainActivity
```

### 3. Test Scenarios

#### English input (regression check)
1. Navigate to a TextBox sample in SamplesApp
2. Tap the TextBox to focus
3. Type "hello" using the English keyboard
4. Verify text appears correctly — no composition events should fire

#### CJK composition (primary test)
1. Switch keyboard to Chinese Pinyin (long-press globe icon on Gboard)
2. Tap a TextBox to focus
3. Type "nihao" — observe composition preview (underlined text)
4. Select "你好" from candidates
5. Verify:
   - Composition text appeared during typing
   - Final committed text is "你好"
   - If using the debug sample page: TextCompositionStarted → TextCompositionChanged (multiple) → TextCompositionEnded events logged

#### Composition cancel
1. Switch to Pinyin, focus a TextBox
2. Type "ni" — observe composition
3. Press backspace until composition is cleared
4. Verify TextBox returns to original state

#### Focus switch during composition
1. Start a composition in one TextBox
2. Tap a different TextBox
3. Verify the composition is properly ended (no stale state)

### 4. Using the Debug Sample Page

The existing `TextBox_X11_IME_Debug` sample can be extended for Android or a new Android-specific debug page can be added. The sample shows:
- Current IME backend status
- Last composition events (start, update, complete, end)
- Event log with timestamps

## Key Design Decisions

1. **Bridging, not reimplementation**: The Android `TextInputConnection` already handles all IME protocol details (SetComposingText, CommitText, etc.). We only need to observe composing region transitions and fire the appropriate `IImeTextBoxExtension` events.
2. **Singleton pattern**: Matches Win32 and X11 implementations. `TextBox.skia.cs` creates and subscribes to the extension once via `ApiExtensibility`.
3. **ComposingStart/ComposingEnd as source of truth**: Android's composing span (tracked by `ObservableEditingState`) is the canonical indicator of composition state. Transition from no span → span = start, span change = update, span → no span = commit/cancel.
4. **No adb automation for composition**: `adb shell input text` bypasses `InputConnection` entirely, so IME composition cannot be automated via adb. Manual testing with a real CJK keyboard is required.
