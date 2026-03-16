# Quickstart: IME Support Implementation

**Branch**: `001-ime-support` | **Date**: 2026-03-16

## Prerequisites

- Windows machine with a CJK IME installed (Japanese IME recommended for testing)
- `src/crosstargeting_override.props` set to `net10.0` (Skia target)
- Familiarity with Win32 IMM32 API concepts

## Build & Test

```bash
cd src

# Restore and build (Skia desktop)
dotnet restore Uno.UI-Skia-only.slnf
dotnet build Uno.UI-Skia-only.slnf --no-restore

# Run unit tests
dotnet test Uno.UI/Uno.UI.Tests.csproj --no-build

# Run SamplesApp for manual IME testing
dotnet run --project SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj

# Run runtime tests headlessly
dotnet build SamplesApp/SamplesApp.Skia.Generic/SamplesApp.Skia.Generic.csproj -c Release -f net10.0
cd SamplesApp/SamplesApp.Skia.Generic/bin/Release/net10.0
dotnet SamplesApp.Skia.Generic.dll --runtime-tests=test-results.xml
```

## Key Files to Modify

### New files
| File | Purpose |
|------|---------|
| `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/IImeTextBoxExtension.skia.cs` | Platform abstraction interface |
| `src/Uno.UI/UI/Xaml/Controls/TextBox/Extensions/ImeCompositionEventArgs.skia.cs` | Event args for interface |
| `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Controls/TextBox/Win32ImeTextBoxExtension.cs` | Win32 IMM32 implementation |

### Files to modify
| File | Change |
|------|--------|
| `src/Uno.UI.Runtime.Skia.Win32/Hosting/Win32Host.cs` | Register `IImeTextBoxExtension` |
| `src/Uno.UI.Runtime.Skia.Win32/UI/Xaml/Window/Win32WindowWrapper.cs` | Handle WM_IME_* messages |
| `src/Uno.UI.Runtime.Skia.Win32/Devices/Input/Win32WindowWrapper.Keyboard.cs` | Suppress WM_CHAR during composition |
| `src/Uno.UI/UI/Xaml/Controls/TextBox/TextBox.skia.cs` | Composition state management, event firing |
| `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionStartedEventArgs.cs` | Implement (copy from Generated, add backing fields) |
| `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionChangedEventArgs.cs` | Implement (copy from Generated, add backing fields) |
| `src/Uno.UI/UI/Xaml/Controls/TextBox/TextCompositionEndedEventArgs.cs` | Implement (copy from Generated, add backing fields) |
| `src/Uno.UI/UI/Xaml/Documents/UnicodeText.skia.cs` | Composition underline rendering |

## Manual Testing Steps

1. Build and run SamplesApp on Windows
2. Navigate to a TextBox sample
3. Switch to Japanese IME (Win+Space or language bar)
4. Type "nihongo" — should see hiragana composition inline with underline
5. Press Space to see candidate list — should appear near caret
6. Press Enter to commit — composition text replaced with selected kanji
7. Press Escape during composition — text should revert
8. Verify TextCompositionStarted/Changed/Ended events fire (add event handlers in test sample)
