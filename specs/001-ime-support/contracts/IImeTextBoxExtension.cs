// Contract: IImeTextBoxExtension
// This is the interface for platform-specific IME integration with TextBox.
// Registered via ApiExtensibility per platform. The managed TextBox layer
// subscribes to composition events and manages text/rendering state.

namespace Uno.UI.Xaml.Controls.Extensions;

/// <summary>
/// Platform-specific extension for IME (Input Method Editor) support in TextBox.
/// Each platform provides an implementation registered via ApiExtensibility.
/// The extension intercepts native IME events and forwards composition state
/// to the managed TextBox layer.
/// </summary>
internal interface IImeTextBoxExtension
{
	/// <summary>
	/// Called when the TextBox gains focus. The platform should prepare
	/// IME context for the given TextBox.
	/// </summary>
	void StartImeSession(TextBox textBox);

	/// <summary>
	/// Called when the TextBox loses focus. The platform should clean up
	/// IME context. Any active composition should be committed or cancelled.
	/// </summary>
	void EndImeSession();

	/// <summary>
	/// Called when the caret position changes in the TextBox.
	/// The platform should update the IME candidate window position.
	/// </summary>
	void UpdateCaretPosition(int x, int y);

	/// <summary>
	/// Raised when the user begins an IME composition (e.g., starts typing
	/// a CJK character sequence).
	/// </summary>
	event EventHandler? CompositionStarted;

	/// <summary>
	/// Raised when the IME composition string changes (e.g., user types
	/// additional characters to narrow candidates).
	/// Args: the current composition string.
	/// </summary>
	event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;

	/// <summary>
	/// Raised when the user commits text from the IME (selects a candidate).
	/// Args: the committed text string.
	/// </summary>
	event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;

	/// <summary>
	/// Raised when the IME composition session ends (after commit or cancel).
	/// </summary>
	event EventHandler? CompositionEnded;
}

/// <summary>
/// Event args for IME composition updates and completions.
/// </summary>
internal class ImeCompositionEventArgs : EventArgs
{
	/// <summary>
	/// The composition string (during composition) or committed text (on completion).
	/// </summary>
	public string Text { get; }

	public ImeCompositionEventArgs(string text)
	{
		Text = text;
	}
}
