using System;
using Windows.Foundation;
using Uno.Disposables;
using Uno.Foundation.Extensibility;
using Uno.UI.Xaml.Controls.Extensions;

namespace Microsoft.UI.Xaml.Controls;

public partial class TextBox
{
	private static IImeTextBoxExtension _imeExtension;
	private static TextBox _activeImeTextBox;
	private bool _isComposing;
	private int _compositionStartIndex;
	private int _compositionLength;

	public event TypedEventHandler<TextBox, TextCompositionStartedEventArgs> TextCompositionStarted;
	public event TypedEventHandler<TextBox, TextCompositionChangedEventArgs> TextCompositionChanged;
	public event TypedEventHandler<TextBox, TextCompositionEndedEventArgs> TextCompositionEnded;

	internal bool IsComposing => _isComposing;
	internal int CompositionStartIndex => _compositionStartIndex;
	internal int CompositionLength => _compositionLength;

	private void InitializeIme()
	{
		if (_imeExtension is null)
		{
			_ = ApiExtensibility.CreateInstance(null, out _imeExtension);
			if (_imeExtension is not null)
			{
				_imeExtension.CompositionStarted += static (_, _) => _activeImeTextBox?.OnImeCompositionStarted();
				_imeExtension.CompositionUpdated += static (_, e) => _activeImeTextBox?.OnImeCompositionUpdated(e.Text);
				_imeExtension.CompositionCompleted += static (_, e) => _activeImeTextBox?.OnImeCompositionCompleted(e.Text);
				_imeExtension.CompositionEnded += static (_, _) => _activeImeTextBox?.OnImeCompositionEnded();
			}
		}
	}

	private void StartImeSession()
	{
		_activeImeTextBox = this;
		_imeExtension?.StartImeSession(this);
	}

	private void EndImeSession()
	{
		_imeExtension?.EndImeSession();
		_activeImeTextBox = null;
	}

	private void OnImeCompositionStarted()
	{
		if (IsReadOnly)
		{
			return;
		}

		_isComposing = true;
		_compositionStartIndex = SelectionStart;
		_compositionLength = 0;

		TextCompositionStarted?.Invoke(this, new TextCompositionStartedEventArgs(_compositionStartIndex, 0));
	}

	private void OnImeCompositionUpdated(string compositionText)
	{
		if (!_isComposing || IsReadOnly)
		{
			return;
		}

		ReplaceCompositionText(compositionText);
		_compositionLength = compositionText.Length;

		TextCompositionChanged?.Invoke(this, new TextCompositionChangedEventArgs(_compositionStartIndex, _compositionLength));
		InvalidateTextBoxRender();
	}

	private void OnImeCompositionCompleted(string committedText)
	{
		if (!_isComposing || IsReadOnly)
		{
			return;
		}

		TrySetCurrentlyTyping(true);
		ReplaceCompositionText(committedText);

		var startIndex = _compositionStartIndex;
		var committedLength = committedText.Length;
		_isComposing = false;
		_compositionLength = 0;
		_compositionStartIndex = 0;

		TextCompositionEnded?.Invoke(this, new TextCompositionEndedEventArgs(startIndex, committedLength));
		InvalidateTextBoxRender();
	}

	private void OnImeCompositionEnded()
	{
		if (!_isComposing)
		{
			return;
		}

		// Composition ended without explicit commit — keep text as-is (matches WinUI behavior).
		// The composition text was already inserted via ProcessTextInput during OnImeCompositionUpdated.
		_isComposing = false;
		_compositionLength = 0;
		_compositionStartIndex = 0;

		InvalidateTextBoxRender();
	}

	private void ReplaceCompositionText(string newText)
	{
		var text = Text;
		var replaced = text[.._compositionStartIndex] + newText + text[(_compositionStartIndex + _compositionLength)..];

		_suppressCurrentlyTyping = true;
		_clearHistoryOnTextChanged = false;
		try
		{
			_pendingSelection = (_compositionStartIndex + newText.Length, 0);
			ProcessTextInput(replaced);
		}
		finally
		{
			_clearHistoryOnTextChanged = true;
			_suppressCurrentlyTyping = false;
		}
	}

	private void InvalidateTextBoxRender()
	{
		if (TextBoxView?.DisplayBlock.Visual is { } visual)
		{
			Visual.Compositor.InvalidateRender(visual);
		}
	}

	/// <summary>
	/// Installs a fake IME extension for testing. The extension's events are
	/// forwarded to the active TextBox. Returns a disposable that restores the original.
	/// </summary>
	internal static IDisposable SetImeExtensionForTesting(IImeTextBoxExtension extension)
	{
		var original = _imeExtension;
		_imeExtension = extension;

		EventHandler onStarted = (_, _) => _activeImeTextBox?.OnImeCompositionStarted();
		EventHandler<ImeCompositionEventArgs> onUpdated = (_, e) => _activeImeTextBox?.OnImeCompositionUpdated(e.Text);
		EventHandler<ImeCompositionEventArgs> onCompleted = (_, e) => _activeImeTextBox?.OnImeCompositionCompleted(e.Text);
		EventHandler onEnded = (_, _) => _activeImeTextBox?.OnImeCompositionEnded();

		extension.CompositionStarted += onStarted;
		extension.CompositionUpdated += onUpdated;
		extension.CompositionCompleted += onCompleted;
		extension.CompositionEnded += onEnded;

		return Disposable.Create(() =>
		{
			extension.CompositionStarted -= onStarted;
			extension.CompositionUpdated -= onUpdated;
			extension.CompositionCompleted -= onCompleted;
			extension.CompositionEnded -= onEnded;
			_imeExtension = original;
		});
	}
}
