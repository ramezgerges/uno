// Contract: AndroidImeTextBoxExtension
// Implements IImeTextBoxExtension for Skia Android platform.
// Bridges Android BaseInputConnection composition events to Uno TextComposition events.

using System;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.UI.Runtime.Skia.Android;

internal sealed class AndroidImeTextBoxExtension : IImeTextBoxExtension
{
	private bool _isComposing;
	private int _lastComposingStart = -1;
	private int _lastComposingEnd = -1;

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	/// <summary>
	/// Called when a TextBox gains focus. Connects to the active TextInputConnection
	/// to receive composition state change notifications.
	/// </summary>
	public void StartImeSession(TextBox textBox)
	{
		// Connect to TextInputPlugin's active TextInputConnection
		// to receive composition state callbacks.
		// Implementation will wire up to the DidChangeEditingState callback.
	}

	/// <summary>
	/// Called when a TextBox loses focus. Disconnects composition monitoring.
	/// If a composition was in progress, fires CompositionEnded.
	/// </summary>
	public void EndImeSession()
	{
		if (_isComposing)
		{
			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
		// Disconnect from TextInputConnection callbacks.
	}

	/// <summary>
	/// Called by TextInputConnection when the composing region changes.
	/// Detects composition state transitions and fires appropriate events.
	/// </summary>
	internal void OnCompositionStateChanged(int composingStart, int composingEnd, string? composingText, string fullText)
	{
		bool wasComposing = _isComposing;
		bool isNowComposing = composingStart >= 0 && composingEnd > composingStart;

		if (!wasComposing && isNowComposing)
		{
			// Transition: Idle → Composing
			_isComposing = true;
			CompositionStarted?.Invoke(this, EventArgs.Empty);
			if (!string.IsNullOrEmpty(composingText))
			{
				CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(composingText));
			}
		}
		else if (wasComposing && isNowComposing)
		{
			// Transition: Composing → Composing (update)
			if (!string.IsNullOrEmpty(composingText))
			{
				CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(composingText));
			}
		}
		else if (wasComposing && !isNowComposing)
		{
			// Transition: Composing → Idle (commit or cancel)
			// If text changed, it's a commit. Extract the committed text.
			_isComposing = false;

			// The committed text is what replaced the composing region.
			// At this point the text is already updated in the editable.
			// Fire CompositionCompleted with the committed segment if text changed.
			// Then fire CompositionEnded.
			CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(/* committed text */));
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}

		_lastComposingStart = composingStart;
		_lastComposingEnd = composingEnd;
	}
}
