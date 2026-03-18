# Quickstart: macOS IME Composition Testing

## Prerequisites

- **macOS machine required** for build and runtime testing (native Objective-C compilation and NSTextInputClient protocol require macOS)
- .NET 10.0 SDK installed
- Xcode command line tools installed (for building `libUnoNativeMac.dylib`)
- Uno Platform source code checked out on branch `005-macos-ime`

> **Note**: This feature cannot be built or tested in a Linux/WSL environment. The native Objective-C code (`UnoNativeMac`) and macOS-specific frameworks are only available on macOS. Development of C# managed code can be done on any platform, but compilation and testing require macOS.

## Build

```bash
cd src
cp crosstargeting_override.props.sample crosstargeting_override.props
# Edit to set: <UnoTargetFrameworkOverride>net10.0-macos14.0</UnoTargetFrameworkOverride>

dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj -f net10.0-macos14.0
```

## Run

```bash
cd src/SamplesApp/SamplesApp.Skia.Generic
dotnet run -f net10.0-macos14.0
```

## Test Scenarios

### Scenario 1: Basic CJK Composition (P1)

1. Launch the SamplesApp on macOS
2. Navigate to **TextBox** > **TextBox_IME_Debug** sample
3. Click the InputTextBox to focus it
4. Switch to a CJK input method:
   - System Settings > Keyboard > Input Sources > Add "Simplified Pinyin" (or "Japanese - Romaji")
   - Press Ctrl+Space (or Globe key) to switch to the CJK input method
5. Type "nihao" (Pinyin for 你好)
6. **Expected**:
   - Event log shows `TextCompositionStarted` when first key is typed
   - Event log shows `TextCompositionChanged` as preedit updates
   - Composition text appears in TextBox with underline decoration
   - State shows "Composing"
7. Press Space to accept the first candidate (你好)
8. **Expected**:
   - Event log shows `TextCompositionEnded`
   - TextBox shows committed text "你好"
   - State shows "Idle"
   - Last Commit shows "你好"

### Scenario 2: Cancel Composition

1. Start composing (type "nihao" with Pinyin active)
2. Press Escape
3. **Expected**:
   - Composition text is removed
   - `TextCompositionEnded` fires
   - TextBox returns to pre-composition state

### Scenario 3: Candidate Window Position (P2)

1. Focus the InputTextBox
2. Start typing in Pinyin
3. **Expected**: The macOS candidate window popup appears near the text caret, not at the bottom of the screen

### Scenario 4: Press-and-Hold Accent Input

1. Switch to English input method
2. Press and hold the 'a' key
3. **Expected**: macOS accent popup appears (à, á, â, etc.)
4. Select an accent character
5. **Expected**: The accented character is inserted into the TextBox

### Scenario 5: Cross-Platform Parity

1. Run the same TextBox_IME_Debug sample on X11 (Linux), Win32, and macOS
2. Perform the same Pinyin composition on each
3. **Expected**: Event sequences match across platforms

## Debugging

Enable trace logging to see composition events:
```
UNO_BOOTSTRAP_LOG_LEVEL=trace
```

Check native IME routing in Xcode console output (DEBUG builds print `NSEventTypeKeyDown` logs).
