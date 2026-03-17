using System;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Windows.Foundation;
using Windows.UI.Core;

namespace Uno.WinUI.Runtime.Skia.X11;

internal class X11KeyboardInputSource : IUnoKeyboardInputSource
{
	public event TypedEventHandler<object, KeyEventArgs>? KeyDown;
	public event TypedEventHandler<object, KeyEventArgs>? KeyUp;

	private X11XamlRootHost _host;

	public X11KeyboardInputSource(IXamlRootHost host)
	{
		if (host is not X11XamlRootHost)
		{
			throw new ArgumentException($"{nameof(host)} must be an X11 host instance");
		}

		_host = (X11XamlRootHost)host;
		_host.SetKeyboardSource(this);
	}

	/// <summary>
	/// Called from the event loop when XFilterEvent returns true for a KeyPress.
	/// Checks if the IME committed text synchronously during filtering.
	/// </summary>
	internal unsafe void ProcessFilteredKeyEvent(XKeyEvent keyEvent)
	{
		var xic = X11ImeTextBoxExtension.GetXicForWindow(keyEvent.window);
		if (xic == IntPtr.Zero)
		{
			return;
		}

		var buffer = stackalloc byte[64];
		int nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, buffer, 64, out _, out var status);

		if ((status == XLib.XLookupChars || status == XLib.XLookupBoth) && nbytes > 0)
		{
			var committed = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
			if (!string.IsNullOrEmpty(committed))
			{
				if (this.Log().IsEnabled(LogLevel.Trace))
				{
					this.Log().Trace($"ProcessFilteredKeyEvent: IME committed '{committed}' during XFilterEvent");
				}

				var imeExtension = X11ImeTextBoxExtension.Instance;
				X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(committed));
				return;
			}
		}

		// No committed text — IME is composing
		var ime = X11ImeTextBoxExtension.Instance;
		X11XamlRootHost.QueueAction(_host, () => ime.OnComposing());
	}

	internal unsafe void ProcessKeyboardEvent(XKeyEvent keyEvent, bool pressed)
	{
		var xic = X11ImeTextBoxExtension.GetXicForWindow(keyEvent.window);

		string? symbols = null;
		nint keySym = 0;

		if (xic != IntPtr.Zero && pressed)
		{
			// Use Xutf8LookupString for IME-aware text lookup
			var buffer = stackalloc byte[64];
			int nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, buffer, 64, out keySym, out var status);

			if (status == XLib.XBufferOverflow)
			{
				// Buffer too small — allocate larger and retry
				var largeBuffer = stackalloc byte[nbytes + 1];
				nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, largeBuffer, nbytes + 1, out keySym, out status);
				buffer = largeBuffer;
			}

			switch (status)
			{
				case XLib.XLookupBoth:
					// Regular key press with text — handle as normal KeyDown with unicodeKey.
					// Do NOT route through IME composition path to avoid double insertion.
					symbols = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
					if (string.IsNullOrEmpty(symbols))
					{
						symbols = null;
					}
					break;

				case XLib.XLookupChars:
					// IME committed text (no keysym) — route through composition events.
					var committed = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
					if (!string.IsNullOrEmpty(committed))
					{
						var imeExtension = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(committed));
					}
					// Don't set symbols — text is handled by composition events, not KeyDown.
					break;

				case XLib.XLookupKeySym:
					// Key only, no text — process as normal key event
					break;

				case XLib.XLookupNone:
					// IME consumed the event entirely — skip
					return;
			}

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessKeyboardEvent pressed={pressed}: {keyEvent.keycode} -> {X11KeyTransform.VirtualKeyFromKeySym(keySym)} status={status} utf8:{symbols}");
			}
		}
		else
		{
			// No XIC or key release — use classic XLookupString
			var buffer = stackalloc byte[4];
			int nbytes = XLib.XLookupString(ref keyEvent, buffer, 4, out keySym, IntPtr.Zero);

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessKeyboardEvent pressed={pressed}: {keyEvent.keycode} -> {X11KeyTransform.VirtualKeyFromKeySym(keySym)}");
			}

			var text = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
			if (!string.IsNullOrEmpty(text) && (text == "\r" || !char.IsControl(text[0])))
			{
				symbols = text;
			}
		}

		// Filter out control characters from symbols (except CR)
		if (symbols is not null && symbols != "\r" && symbols.Length > 0 && char.IsControl(symbols[0]))
		{
			symbols = null;
		}

		var args = new KeyEventArgs(
			"keyboard",
			X11KeyTransform.VirtualKeyFromKeySym(keySym),
			X11XamlRootHost.XModifierMaskToVirtualKeyModifiers(keyEvent.state),
			new CorePhysicalKeyStatus
			{
				ScanCode = (uint)keyEvent.keycode,
				RepeatCount = 1,
			},
			unicodeKey: symbols?.Length > 0 ? symbols[0] : null);

		X11XamlRootHost.QueueAction(_host, () =>
		{
			if (pressed)
			{
				KeyDown?.Invoke(this, args);
			}
			else
			{
				KeyUp?.Invoke(this, args);
			}
		});
	}
}
