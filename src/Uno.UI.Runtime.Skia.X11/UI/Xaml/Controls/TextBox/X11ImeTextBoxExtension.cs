#nullable enable

using System;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Controls;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.WinUI.Runtime.Skia.X11;

/// <summary>
/// X11 XIM-based implementation of <see cref="IImeTextBoxExtension"/>.
/// Uses XOpenIM/XCreateIC to create input contexts per window and
/// routes composition events from the keyboard input source.
/// </summary>
internal sealed class X11ImeTextBoxExtension : IImeTextBoxExtension
{
	internal static X11ImeTextBoxExtension Instance { get; } = new();

	private static IntPtr _xim;
	private static readonly ConcurrentDictionary<IntPtr, IntPtr> _windowToXic = new();

	private IntPtr _currentDisplay;
	private IntPtr _currentWindow;
	private IntPtr _currentXic;
	private bool _isComposing;

	private X11ImeTextBoxExtension()
	{
	}

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
#pragma warning disable CS0067 // CompositionUpdated is not raised with XIMPreeditNothing style (IME renders its own preedit window)
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

		if (textBox.XamlRoot is not { } xamlRoot)
		{
			return;
		}

		if (XamlRootMap.GetHostForRoot(xamlRoot) is not X11XamlRootHost host)
		{
			return;
		}

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
				_currentXic = XLib.XCreateIC(_xim,
					__arglist(
						XLib.XNInputStyle, (IntPtr)(XLib.XIMPreeditNothing | XLib.XIMStatusNothing),
						XLib.XNClientWindow, _currentWindow,
						XLib.XNFocusWindow, _currentWindow,
						IntPtr.Zero));

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
	/// Updates the candidate window position by setting the XIC spot location.
	/// </summary>
	internal void UpdateSpotLocation(short x, short y)
	{
		if (_currentXic == IntPtr.Zero)
		{
			return;
		}

		var point = new XPoint { X = x, Y = y };
		using var _ = X11Helper.XLock(_currentDisplay);
		XLib.XSetICValues(_currentXic,
			__arglist(XLib.XNSpotLocation, point, IntPtr.Zero));
	}
}
