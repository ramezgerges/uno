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
/// Uses XOpenIM/XCreateIC with XIMPreeditNothing to create input contexts
/// per window, and routes composition events from the keyboard input source.
/// </summary>
internal sealed class X11ImeTextBoxExtension : IImeTextBoxExtension
{
	internal static X11ImeTextBoxExtension Instance { get; } = new();

	private static IntPtr _xim;
	private static readonly ConcurrentDictionary<IntPtr, IntPtr> _windowToXic = new();

	// The host for dispatching UI-thread actions.
	private X11XamlRootHost? _currentHost;

	// Pending spot location to be applied from the event thread.
	// XSetICValues must not be called from the UI thread while the event thread uses the XIC.
	private volatile bool _spotLocationPending;
	private short _pendingSpotX;
	private short _pendingSpotY;

	private IntPtr _currentDisplay;
	private IntPtr _currentWindow;
	private IntPtr _currentXic;
	private bool _isComposing;

	private X11ImeTextBoxExtension()
	{
	}

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
#pragma warning disable CS0067 // Interface-required event; will be used when inline preedit preview is implemented.
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
#pragma warning restore CS0067
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
				// Use XIMPreeditNothing so the IME renders its own preedit popup.
				// XIMPreeditCallbacks is not reliably supported by IBus — it accepts
				// the style but never invokes the callbacks, causing XFilterEvent to
				// swallow all key events (including backspace in English mode).
				_currentXic = XLib.XCreateIC(_xim,
					XLib.XNInputStyle, (IntPtr)(XLib.XIMPreeditNothing | XLib.XIMStatusNothing),
					XLib.XNClientWindow, _currentWindow,
					XLib.XNFocusWindow, _currentWindow,
					IntPtr.Zero);

				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"XCreateIC with XIMPreeditNothing: {(_currentXic != IntPtr.Zero ? "succeeded" : "failed")}");
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
	/// Stores the desired spot location. The actual XSetICValues call is deferred
	/// to the event thread via <see cref="FlushPendingSpotLocation"/> to avoid
	/// concurrent XIC access from the UI thread and the X11 event thread.
	/// </summary>
	internal void UpdateSpotLocation(short x, short y)
	{
		_pendingSpotX = x;
		_pendingSpotY = y;
		_spotLocationPending = true;
	}

	/// <summary>
	/// Applies any pending spot location update. Must be called from the X11 event thread
	/// (e.g., during <see cref="X11KeyboardInputSource.ProcessKeyboardEvent"/>).
	/// </summary>
	internal void FlushPendingSpotLocation()
	{
		if (!_spotLocationPending || _currentXic == IntPtr.Zero)
		{
			return;
		}

		_spotLocationPending = false;

		// Allocate XPoint on unmanaged heap — XVaCreateNestedList is varargs
		// and ref parameters don't work reliably with varargs P/Invoke.
		var pointPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XPoint>());
		try
		{
			Marshal.StructureToPtr(new XPoint { X = _pendingSpotX, Y = _pendingSpotY }, pointPtr, false);

			using var lockDisposable = X11Helper.XLock(_currentDisplay);

			var preeditAttr = XLib.XVaCreateNestedList(0,
				XLib.XNSpotLocation, pointPtr,
				IntPtr.Zero);

			if (preeditAttr != IntPtr.Zero)
			{
				XLib.XSetICValues(_currentXic,
					XLib.XNPreeditAttributes, preeditAttr,
					IntPtr.Zero);
				_ = XLib.XFree(preeditAttr);
			}
		}
		finally
		{
			Marshal.FreeHGlobal(pointPtr);
		}
	}
}
