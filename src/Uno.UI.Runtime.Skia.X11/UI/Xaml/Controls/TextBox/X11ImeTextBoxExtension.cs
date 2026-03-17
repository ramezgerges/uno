#nullable enable

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// X11 XIM-based implementation of <see cref="IImeTextBoxExtension"/>.
/// Uses XOpenIM/XCreateIC with XIMPreeditCallbacks to create input contexts
/// per window, provide inline preedit preview, and route composition events
/// from the keyboard input source.
/// </summary>
internal sealed class X11ImeTextBoxExtension : IImeTextBoxExtension
{
	internal static X11ImeTextBoxExtension Instance { get; } = new();

	private static IntPtr _xim;
	private static readonly ConcurrentDictionary<IntPtr, IntPtr> _windowToXic = new();

	// Preedit callback delegates — stored as fields to prevent GC.
	private static XIMProc? _preeditStartProc;
	private static XIMProc? _preeditDoneProc;
	private static XIMProc? _preeditDrawProc;
	private static XIMProc? _preeditCaretProc;

	// Native memory for XIMCallback structs (must outlive the XIC).
	private static IntPtr _preeditStartCbPtr;
	private static IntPtr _preeditDoneCbPtr;
	private static IntPtr _preeditDrawCbPtr;
	private static IntPtr _preeditCaretCbPtr;

	// The current preedit (composition) string being built up from draw callbacks.
	private string _preeditString = string.Empty;

	// The host for dispatching UI-thread actions.
	private X11XamlRootHost? _currentHost;

	private IntPtr _currentDisplay;
	private IntPtr _currentWindow;
	private IntPtr _currentXic;
	private bool _isComposing;

	private X11ImeTextBoxExtension()
	{
	}

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	/// <summary>
	/// Gets the XIC for the given window, or IntPtr.Zero if none exists.
	/// Called from <see cref="X11KeyboardInputSource"/> to use Xutf8LookupString.
	/// </summary>
	internal static IntPtr GetXicForWindow(IntPtr window)
	{
		_windowToXic.TryGetValue(window, out var xic);
		return xic;
	}

	public void StartImeSession(TextBox textBox)
	{
		_currentDisplay = IntPtr.Zero;
		_currentWindow = IntPtr.Zero;
		_currentXic = IntPtr.Zero;
		_currentHost = null;

		if (textBox.XamlRoot is not { } xamlRoot)
		{
			return;
		}

		if (XamlRootMap.GetHostForRoot(xamlRoot) is not X11XamlRootHost host)
		{
			return;
		}

		_currentHost = host;
		var rootWindow = host.RootX11Window;
		_currentDisplay = rootWindow.Display;
		_currentWindow = rootWindow.Window;

		using (X11Helper.XLock(_currentDisplay))
		{
			// Open XIM if not already open
			if (_xim == IntPtr.Zero)
			{
				_xim = XLib.XOpenIM(_currentDisplay, IntPtr.Zero, null!, null!);
				if (_xim == IntPtr.Zero)
				{
					if (this.Log().IsEnabled(LogLevel.Debug))
					{
						this.Log().Debug("XOpenIM returned null — no input method available. IME will be disabled.");
					}
					return;
				}
			}

			// Get or create XIC for this window
			if (!_windowToXic.TryGetValue(_currentWindow, out _currentXic))
			{
				_currentXic = CreateXicWithPreeditCallbacks();

				if (_currentXic == IntPtr.Zero)
				{
					// Fall back to XIMPreeditNothing if callbacks are not supported.
					if (this.Log().IsEnabled(LogLevel.Debug))
					{
						this.Log().Debug("XIMPreeditCallbacks not supported, falling back to XIMPreeditNothing.");
					}
					_currentXic = XLib.XCreateIC(_xim,
						XLib.XNInputStyle, (IntPtr)(XLib.XIMPreeditNothing | XLib.XIMStatusNothing),
						XLib.XNClientWindow, _currentWindow,
						XLib.XNFocusWindow, _currentWindow,
						IntPtr.Zero);
				}

				if (_currentXic == IntPtr.Zero)
				{
					if (this.Log().IsEnabled(LogLevel.Warning))
					{
						this.Log().Warn("XCreateIC failed — IME will be disabled for this window.");
					}
					return;
				}

				_windowToXic[_currentWindow] = _currentXic;
			}

			XLib.XSetICFocus(_currentXic);
		}
	}

	public void EndImeSession()
	{
		if (_isComposing)
		{
			_isComposing = false;
			_preeditString = string.Empty;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}

		if (_currentXic != IntPtr.Zero && _currentDisplay != IntPtr.Zero)
		{
			using (X11Helper.XLock(_currentDisplay))
			{
				XLib.XUnsetICFocus(_currentXic);
			}
		}

		_currentDisplay = IntPtr.Zero;
		_currentWindow = IntPtr.Zero;
		_currentXic = IntPtr.Zero;
		_currentHost = null;
	}

	/// <summary>
	/// Called from <see cref="X11KeyboardInputSource"/> when Xutf8LookupString
	/// returns committed text (XLookupChars or XLookupBoth).
	/// </summary>
	internal void OnCommittedText(string text)
	{
		if (!_isComposing)
		{
			// Direct commit without prior composition (e.g., single-key IME commit)
			CompositionStarted?.Invoke(this, EventArgs.Empty);
		}

		_preeditString = string.Empty;
		CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(text));
		_isComposing = false;
		CompositionEnded?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Called from the event loop when XFilterEvent consumes a KeyPress,
	/// indicating the IME is composing.
	/// </summary>
	internal void OnComposing()
	{
		if (!_isComposing)
		{
			_isComposing = true;
			CompositionStarted?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>
	/// Called from the preedit draw callback (on X11 event thread) with the updated preedit text.
	/// Dispatches CompositionUpdated on the UI thread.
	/// </summary>
	internal void OnPreeditChanged(string preeditText)
	{
		_preeditString = preeditText;

		if (_currentHost is { } host)
		{
			var text = preeditText;
			X11XamlRootHost.QueueAction(host, () =>
			{
				if (!_isComposing)
				{
					_isComposing = true;
					CompositionStarted?.Invoke(this, EventArgs.Empty);
				}
				CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(text));
			});
		}
	}

	/// <summary>
	/// Updates the candidate window position by setting the XIC spot location
	/// via XNPreeditAttributes nested list.
	/// </summary>
	internal void UpdateSpotLocation(short x, short y)
	{
		if (_currentXic == IntPtr.Zero)
		{
			return;
		}

		var point = new XPoint { X = x, Y = y };
		using var lockDisposable = X11Helper.XLock(_currentDisplay);

		var preeditAttr = XLib.XVaCreateNestedList(0,
			XLib.XNSpotLocation, ref point,
			IntPtr.Zero);

		if (preeditAttr != IntPtr.Zero)
		{
			XLib.XSetICValues(_currentXic,
				XLib.XNPreeditAttributes, preeditAttr,
				IntPtr.Zero);
			_ = XLib.XFree(preeditAttr);
		}
	}

	/// <summary>
	/// Creates an XIC with XIMPreeditCallbacks style, setting up the 4 preedit callbacks.
	/// Returns IntPtr.Zero if the IME doesn't support this style.
	/// </summary>
	private IntPtr CreateXicWithPreeditCallbacks()
	{
		EnsurePreeditCallbacksAllocated();

		var preeditAttr = XLib.XVaCreateNestedList(0,
			XNames.XNPreeditStartCallback, _preeditStartCbPtr,
			XNames.XNPreeditDoneCallback, _preeditDoneCbPtr,
			XNames.XNPreeditDrawCallback, _preeditDrawCbPtr,
			XNames.XNPreeditCaretCallback, _preeditCaretCbPtr,
			IntPtr.Zero);

		if (preeditAttr == IntPtr.Zero)
		{
			return IntPtr.Zero;
		}

		try
		{
			return XLib.XCreateIC(_xim,
				XLib.XNInputStyle, (IntPtr)(XLib.XIMPreeditCallbacks | XLib.XIMStatusNothing),
				XLib.XNClientWindow, _currentWindow,
				XLib.XNFocusWindow, _currentWindow,
				XLib.XNPreeditAttributes, preeditAttr,
				IntPtr.Zero);
		}
		finally
		{
			_ = XLib.XFree(preeditAttr);
		}
	}

	/// <summary>
	/// Allocates the native XIMCallback structs for the 4 preedit callbacks (once).
	/// Each native struct is 2 IntPtrs: client_data + function pointer.
	/// </summary>
	private static void EnsurePreeditCallbacksAllocated()
	{
		if (_preeditStartCbPtr != IntPtr.Zero)
		{
			return; // Already allocated.
		}

		_preeditStartProc = PreeditStartCallback;
		_preeditDoneProc = PreeditDoneCallback;
		_preeditDrawProc = PreeditDrawCallback;
		_preeditCaretProc = PreeditCaretCallback;

		_preeditStartCbPtr = AllocNativeXIMCallback(_preeditStartProc);
		_preeditDoneCbPtr = AllocNativeXIMCallback(_preeditDoneProc);
		_preeditDrawCbPtr = AllocNativeXIMCallback(_preeditDrawProc);
		_preeditCaretCbPtr = AllocNativeXIMCallback(_preeditCaretProc);
	}

	/// <summary>
	/// Allocates an unmanaged XIMCallback struct {client_data, callback} and returns a pointer to it.
	/// </summary>
	private static IntPtr AllocNativeXIMCallback(XIMProc proc)
	{
		var functionPtr = Marshal.GetFunctionPointerForDelegate(proc);
		var ptr = Marshal.AllocHGlobal(IntPtr.Size * 2);
		Marshal.WriteIntPtr(ptr, 0, IntPtr.Zero); // client_data
		Marshal.WriteIntPtr(ptr, IntPtr.Size, functionPtr); // callback
		return ptr;
	}

	// --- Preedit callbacks (called from X11 event thread during XFilterEvent) ---

	private static int PreeditStartCallback(IntPtr xim, IntPtr clientData, IntPtr callData)
	{
		if (Instance.Log().IsEnabled(LogLevel.Trace))
		{
			Instance.Log().Trace("PreeditStartCallback");
		}
		Instance._preeditString = string.Empty;
		// Return -1 to indicate no length limit on the preedit string.
		return -1;
	}

	private static int PreeditDoneCallback(IntPtr xim, IntPtr clientData, IntPtr callData)
	{
		if (Instance.Log().IsEnabled(LogLevel.Trace))
		{
			Instance.Log().Trace("PreeditDoneCallback");
		}
		Instance._preeditString = string.Empty;
		return 0;
	}

	private static unsafe int PreeditDrawCallback(IntPtr xim, IntPtr clientData, IntPtr callData)
	{
		if (callData == IntPtr.Zero)
		{
			return 0;
		}

		var drawStruct = Marshal.PtrToStructure<XIMPreeditDrawCallbackStruct>(callData);

		string newPreedit;
		if (drawStruct.Text != IntPtr.Zero)
		{
			var ximText = Marshal.PtrToStructure<XIMText>(drawStruct.Text);
			if (ximText.String != IntPtr.Zero && ximText.Length > 0)
			{
				if (ximText.EncodingIsWChar != 0)
				{
					// wchar_t* — on Linux wchar_t is 4 bytes (UTF-32)
					newPreedit = BuildPreeditFromDraw(
						Instance._preeditString,
						drawStruct.ChangeFirst,
						drawStruct.ChangeLength,
						Marshal.PtrToStringUni(ximText.String) ?? string.Empty);
				}
				else
				{
					// char* — multibyte (UTF-8 on modern systems)
					newPreedit = BuildPreeditFromDraw(
						Instance._preeditString,
						drawStruct.ChangeFirst,
						drawStruct.ChangeLength,
						Marshal.PtrToStringUTF8(ximText.String) ?? string.Empty);
				}
			}
			else
			{
				// Empty text = deletion at the specified range
				newPreedit = BuildPreeditFromDraw(
					Instance._preeditString,
					drawStruct.ChangeFirst,
					drawStruct.ChangeLength,
					string.Empty);
			}
		}
		else
		{
			// Null text = delete ChangeLength chars at ChangeFirst
			newPreedit = BuildPreeditFromDraw(
				Instance._preeditString,
				drawStruct.ChangeFirst,
				drawStruct.ChangeLength,
				string.Empty);
		}

		if (Instance.Log().IsEnabled(LogLevel.Trace))
		{
			Instance.Log().Trace($"PreeditDrawCallback: preedit='{newPreedit}' changeFirst={drawStruct.ChangeFirst} changeLen={drawStruct.ChangeLength}");
		}

		Instance.OnPreeditChanged(newPreedit);
		return 0;
	}

	private static int PreeditCaretCallback(IntPtr xim, IntPtr clientData, IntPtr callData)
	{
		// We don't need to handle caret movement within the preedit string.
		return 0;
	}

	/// <summary>
	/// Applies the XIM preedit draw operation to the current preedit string.
	/// The draw callback specifies: replace <paramref name="changeLength"/> chars
	/// starting at <paramref name="changeFirst"/> with <paramref name="newText"/>.
	/// </summary>
	private static string BuildPreeditFromDraw(string current, int changeFirst, int changeLength, string newText)
	{
		if (changeFirst < 0)
		{
			changeFirst = 0;
		}

		if (changeFirst > current.Length)
		{
			changeFirst = current.Length;
		}

		if (changeFirst + changeLength > current.Length)
		{
			changeLength = current.Length - changeFirst;
		}

		return current[..changeFirst] + newText + current[(changeFirst + changeLength)..];
	}
}
