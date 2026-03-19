using System;

namespace Uno.UI.Xaml.Controls.Extensions;

/// <summary>
/// Event args for IME composition updates and completions.
/// </summary>
internal class ImeCompositionEventArgs : EventArgs
{
	/// <summary>
	/// The composition string (during composition) or committed text (on completion).
	/// </summary>
	public string Text { get; }

	/// <summary>
	/// Cursor position within the composition string, or -1 if not available.
	/// </summary>
	public int CursorPosition { get; }

	public ImeCompositionEventArgs(string text, int cursorPosition = -1)
	{
		Text = text;
		CursorPosition = cursorPosition;
	}
}
