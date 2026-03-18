// Contract: MacOSImeTextBoxExtension
// Implementation of IImeTextBoxExtension for Skia macOS platform
// Location: src/Uno.UI.Runtime.Skia.MacOS/UI/Xaml/Controls/TextBox/MacOSImeTextBoxExtension.cs

using System;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.UI.Runtime.Skia.MacOS;

/// <summary>
/// macOS Skia implementation of <see cref="IImeTextBoxExtension"/>.
/// Bridges macOS NSTextInputClient composition callbacks (setMarkedText/insertText/unmarkText)
/// to the managed TextBox composition event lifecycle (Started → Updated → Completed → Ended).
/// </summary>
internal sealed class MacOSImeTextBoxExtension : IImeTextBoxExtension
{
	internal static MacOSImeTextBoxExtension Instance { get; } = new();

	public bool IsComposing { get; }

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	public void StartImeSession(TextBox textBox)
	{
		// 1. Store reference to active TextBox
		// 2. Notify native layer to enable IME routing (set _imeActive flag)
		// 3. Send initial caret position for candidate window placement
	}

	public void EndImeSession()
	{
		// 1. If composing, fire CompositionEnded
		// 2. Notify native layer to disable IME routing
		// 3. Clear active TextBox reference
	}

	// Called from native via P/Invoke when NSTextInputClient.setMarkedText is invoked
	internal void OnSetMarkedText(string text, int selectedStart, int selectedLength)
	{
		// State machine: detect Idle→Composing vs Composing→Composing transitions
		// Fire CompositionStarted + CompositionUpdated or just CompositionUpdated
	}

	// Called from native via P/Invoke when NSTextInputClient.insertText is invoked
	internal void OnInsertText(string text)
	{
		// If composing: fire CompositionCompleted + CompositionEnded
		// If not composing: direct text insertion (no composition events)
	}

	// Called from native via P/Invoke when NSTextInputClient.unmarkText is invoked
	internal void OnUnmarkText()
	{
		// Fire CompositionEnded (cancel without commit)
	}
}
