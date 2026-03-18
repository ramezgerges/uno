// Contract: IX11InputMethod — Core abstraction for D-Bus IME backends
// File: src/Uno.UI.Runtime.Skia.X11/IME/IX11InputMethod.cs

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// Abstraction for X11 input method backends (IBus, Fcitx, XIM).
/// Implementations communicate with the IME service via D-Bus or XIM protocol.
/// </summary>
internal interface IX11InputMethod : IDisposable
{
	/// <summary>Whether the input method service is connected and usable.</summary>
	bool IsEnabled { get; }

	/// <summary>
	/// Forward a key event to the IME for processing.
	/// </summary>
	/// <param name="keyVal">X11 KeySym value.</param>
	/// <param name="keyCode">X11 hardware keycode.</param>
	/// <param name="state">X11 modifier mask.</param>
	/// <param name="isRelease">True for KeyRelease, false for KeyPress.</param>
	/// <returns>True if the IME handled the event (should not be dispatched as KeyDown/Up).</returns>
	ValueTask<bool> HandleKeyEventAsync(uint keyVal, uint keyCode, uint state, bool isRelease);

	/// <summary>
	/// Update the IME candidate window position (absolute screen coordinates).
	/// </summary>
	void SetCursorLocation(int x, int y, int w, int h);

	/// <summary>Notify the IME of window focus change.</summary>
	void SetFocus(bool active);

	/// <summary>Reset the current composition state.</summary>
	void Reset();

	/// <summary>Fired when the IME commits finalized text.</summary>
	event Action<string> Commit;

	/// <summary>
	/// Fired when the IME forwards a key event back to the application
	/// (not consumed by IME).
	/// </summary>
	event Action<uint, uint, uint> ForwardKey;

	/// <summary>
	/// Fired when preedit (composition) text changes.
	/// First parameter is the preedit string (null to hide).
	/// Second parameter is the cursor position (character offset).
	/// </summary>
	event Action<string?, int> PreeditChanged;
}
