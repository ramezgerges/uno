#nullable enable

using System;
using Microsoft.UI.Xaml.Controls;
using Uno.Foundation.Logging;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.UI.Runtime.Skia.Android;

/// <summary>
/// Android Skia implementation of <see cref="IImeTextBoxExtension"/>.
/// Bridges Android <see cref="TextInputConnection"/> composition state
/// (SetComposingText/CommitText/FinishComposingText) to the managed
/// TextBox composition event lifecycle (Started → Updated → Completed → Ended).
/// </summary>
/// <remarks>
/// Timing: The composition callback fires from <see cref="ObservableEditingState.EndBatchEdit"/>
/// which happens BEFORE <see cref="TextInputConnection.EndBatchEdit"/> calls
/// <c>ActiveTextBox.ProcessTextInput()</c>. This means TextBox.Text still has the
/// composing text when our callback runs, so <c>ReplaceCompositionText</c> in
/// <c>TextBox.skia.cs</c> works correctly. The subsequent <c>ProcessTextInput</c>
/// from <c>EndBatchEdit</c> sets the same text and is effectively a no-op.
/// </remarks>
internal sealed class AndroidImeTextBoxExtension : IImeTextBoxExtension
{
	private bool _isComposing;
	private int _lastComposingStart = -1;
	private int _lastComposingEnd = -1;
	private int _lastFullTextLength;
	private TextInputConnection? _activeConnection;
	private TextInputPlugin? _activePlugin;
	private bool _sessionActive;

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	public void StartImeSession(TextBox textBox)
	{
		// Don't wire up composition events for PasswordBox — IME composition
		// reveals characters, which is not appropriate for password fields.
		if (textBox is PasswordBox)
		{
			return;
		}

		_sessionActive = true;

		// Get the active TextInputConnection from the TextInputPlugin.
		// Also subscribe to InputConnectionCreated so we can re-subscribe
		// when the system calls OnCreateInputConnection (which creates a new
		// TextInputConnection, invalidating the previous one).
		if (UnoSKCanvasView.Instance is { } canvasView)
		{
			_activePlugin = canvasView.TextInputPlugin;
			_activePlugin.InputConnectionCreated += OnInputConnectionCreated;
			SubscribeToConnection(_activePlugin.ActiveInputConnection);
		}

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"IME session started. Connection: {(_activeConnection is not null ? "active" : "none")}");
		}
	}

	public void EndImeSession()
	{
		_sessionActive = false;
		UnsubscribeFromConnection();

		if (_activePlugin is not null)
		{
			_activePlugin.InputConnectionCreated -= OnInputConnectionCreated;
			_activePlugin = null;
		}

		if (_isComposing)
		{
			_isComposing = false;
			_lastComposingStart = -1;
			_lastComposingEnd = -1;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
	}

	private void OnInputConnectionCreated(TextInputConnection newConnection)
	{
		if (!_sessionActive)
		{
			return;
		}

		// A new connection was created — re-subscribe.
		UnsubscribeFromConnection();
		SubscribeToConnection(newConnection);
	}

	private void SubscribeToConnection(TextInputConnection? connection)
	{
		_activeConnection = connection;
		if (_activeConnection is not null)
		{
			_activeConnection.CompositionStateChanged += OnCompositionStateChanged;
		}
	}

	private void UnsubscribeFromConnection()
	{
		if (_activeConnection is not null)
		{
			_activeConnection.CompositionStateChanged -= OnCompositionStateChanged;
			_activeConnection = null;
		}
	}

	private void OnCompositionStateChanged(int composingStart, int composingEnd, string? composingText, string fullText)
	{
		bool wasComposing = _isComposing;
		bool isNowComposing = composingStart >= 0 && composingEnd > composingStart;

		if (!wasComposing && isNowComposing)
		{
			// Transition: Idle → Composing
			_isComposing = true;
			_lastComposingStart = composingStart;
			_lastComposingEnd = composingEnd;
			_lastFullTextLength = fullText.Length;

			CompositionStarted?.Invoke(this, EventArgs.Empty);

			if (!string.IsNullOrEmpty(composingText))
			{
				CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(composingText));
			}

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"Composition started: [{composingStart}..{composingEnd}] '{composingText}'");
			}
		}
		else if (wasComposing && isNowComposing)
		{
			// Transition: Composing → Composing (preedit update)
			_lastComposingStart = composingStart;
			_lastComposingEnd = composingEnd;
			_lastFullTextLength = fullText.Length;

			if (!string.IsNullOrEmpty(composingText))
			{
				CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(composingText));
			}

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"Composition updated: [{composingStart}..{composingEnd}] '{composingText}'");
			}
		}
		else if (wasComposing && !isNowComposing)
		{
			// Transition: Composing → Idle (commit or cancel)
			_isComposing = false;

			// Compute the committed text. The old composing region was at
			// [_lastComposingStart.._lastComposingEnd). The text outside
			// that region is unchanged, so:
			//   nonComposingLength = _lastFullTextLength - oldComposingLength
			//   committedLength = fullText.Length - nonComposingLength
			var oldComposingLength = _lastComposingEnd - _lastComposingStart;
			var nonComposingLength = _lastFullTextLength - oldComposingLength;
			var committedLength = fullText.Length - nonComposingLength;

			if (committedLength > 0 && _lastComposingStart >= 0
				&& _lastComposingStart + committedLength <= fullText.Length)
			{
				var committedText = fullText.Substring(_lastComposingStart, committedLength);
				CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(committedText));

				if (this.Log().IsEnabled(LogLevel.Trace))
				{
					this.Log().Trace($"Composition committed: '{committedText}' at {_lastComposingStart}");
				}
			}
			else if (committedLength == 0)
			{
				// Composing region removed without replacement — cancel.
				if (this.Log().IsEnabled(LogLevel.Trace))
				{
					this.Log().Trace("Composition cancelled (no committed text)");
				}
			}

			_lastComposingStart = -1;
			_lastComposingEnd = -1;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}
	}
}
