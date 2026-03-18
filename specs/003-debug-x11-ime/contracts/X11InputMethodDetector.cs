// Contract: X11InputMethodDetector — IME backend detection and creation
// File: src/Uno.UI.Runtime.Skia.X11/IME/X11InputMethodDetector.cs

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// Detects the active input method framework from environment variables
/// and creates the appropriate D-Bus IME client.
/// </summary>
/// <remarks>
/// Detection priority:
/// 1. UNO_IM_MODULE (Uno-specific override)
/// 2. GTK_IM_MODULE (standard Linux)
/// 3. QT_IM_MODULE (standard Linux)
/// 4. XMODIFIERS (X11 standard, parsed for @im=name)
///
/// Returns null if no D-Bus IME is detected → caller falls back to XIM.
/// </remarks>
internal static class X11InputMethodDetector
{
	/// <summary>
	/// Detect the active IME and create a D-Bus client for it.
	/// Returns null if no D-Bus IME is available (XIM should be used).
	/// </summary>
	public static IX11InputMethod? DetectAndCreate()
	{
		// Check env vars in priority order
		// Parse XMODIFIERS for @im=<name> pattern
		// Return IBusInputMethod, FcitxInputMethod, or null
		throw new NotImplementedException();
	}
}
