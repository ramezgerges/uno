# Quickstart: iOS Skia IME Composition Support

**Feature Branch**: `006-ios-ime`
**Date**: 2026-03-18

## Integration Scenarios

### Scenario 1: Basic CJK Composition (Pinyin)

1. Launch Uno Skia iOS app with a TextBox
2. Focus the TextBox
3. Switch to Pinyin keyboard
4. Type "nihao" — composition text "nihao" appears with underline in TextBox
5. Select "你好" from candidates — composition text replaced with committed text
6. Verify caret is positioned after "你好"

**Expected event sequence**:
```
StartImeSession(textBox)
CompositionStarted         → TextBox._isComposing = true
CompositionUpdated("n")    → TextBox displays "n" with underline
CompositionUpdated("ni")   → TextBox displays "ni" with underline
CompositionUpdated("nih")  → ...
CompositionUpdated("niha") → ...
CompositionUpdated("nihao")→ TextBox displays "nihao" with underline
CompositionCompleted("你好") → TextBox replaces composition with "你好"
CompositionEnded           → TextBox._isComposing = false
```

### Scenario 2: Composition Cancellation

1. Focus TextBox, switch to Pinyin
2. Type "ni" — composition text appears
3. Press backspace twice to clear composition
4. Verify TextBox returns to state before composition started

**Expected event sequence**:
```
CompositionStarted
CompositionUpdated("ni")
CompositionUpdated("n")     → backspace reduces composition
CompositionEnded             → backspace clears last char, composition cancelled
```

### Scenario 3: Non-Composition Keys During IME

1. Focus TextBox, type "Hello" normally
2. Switch to Pinyin keyboard (no active composition)
3. Press left arrow — caret moves left
4. Press backspace — deletes character before caret
5. Type "ni" to start composition, then press Escape — composition cancelled
6. Press Tab — focus moves to next control

### Scenario 4: Focus Loss During Composition

1. Focus TextBox, start Pinyin composition "ni"
2. Tap another TextBox — first TextBox loses focus
3. Verify composition in first TextBox is committed or cancelled
4. Verify second TextBox receives focus and can accept input

### Scenario 5: PasswordBox IME Suppression

1. Focus a PasswordBox
2. Switch to Pinyin keyboard
3. Type keys — should input directly as password characters, no composition

## Build & Test

### Build (Skia Desktop — development environment)

The iOS Skia runtime cannot be built in this environment (no macOS/Xcode). Use Skia desktop to validate shared composition logic and compile-check the AppleUIKit project:

```bash
cd src
# Ensure crosstargeting_override.props has net10.0
dotnet build Uno.UI-Skia-only.slnf --no-restore
```

### Runtime Tests (Skia Desktop — headless)

Composition event sequence tests run on Skia desktop since the shared `TextBox.skia.cs` composition handlers are platform-agnostic:

```bash
dotnet build src/SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj -c Release -f net10.0
cd src/SamplesApp/SamplesApp.Skia.Generic/bin/Release/net10.0
dotnet SamplesApp.Skia.Generic.dll --runtime-tests=test-results.xml
```

### End-to-End Validation (requires macOS + iOS Simulator — manual)

Full IME validation with CJK keyboards requires deploying to an iOS Simulator or device on a macOS machine:

```bash
# On macOS with Xcode installed:
dotnet build src/SamplesApp/SamplesApp.Mobile/SamplesApp.Mobile.csproj -f net10.0-ios
# Deploy to iOS Simulator and test with Pinyin/Kana keyboard
```

## Key Files to Modify

| File | Change |
|------|--------|
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxView.cs` | Override SetMarkedText, InsertText, UnmarkText |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxView.cs` | Override SetMarkedText, InsertText, UnmarkText |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/AppleUIKitImeTextBoxExtension.cs` | New: IImeTextBoxExtension implementation |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/InvisibleTextBoxViewExtension.cs` | Suppress ProcessNativeTextInput during composition |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/SinglelineInvisibleTextBoxDelegate.cs` | Composition-aware ShouldChangeCharacters |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/UI/Xaml/Controls/TextBox/MultilineInvisibleTextBoxDelegate.cs` | Composition-aware ShouldChangeText |
| `src/Uno.UI.Runtime.Skia.AppleUIKit/Hosting/ExtensionsRegistrar.cs` | Register IImeTextBoxExtension |
