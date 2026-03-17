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

	internal unsafe void ProcessKeyboardEvent(XKeyEvent keyEvent, bool pressed, bool imeFiltered = false)
	{
		var xic = X11ImeTextBoxExtension.GetXicForWindow(keyEvent.window);

		string? symbols = null;
		nint keySym = 0;

		if (xic != IntPtr.Zero && pressed)
		{
			// Use Xutf8LookupString for IME-aware text lookup.
			// This is called for both filtered and non-filtered events:
			// XFilterEvent dispatches to the IME, Xutf8LookupString reads back the result.
			var buffer = stackalloc byte[64];
			int nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, buffer, 64, out keySym, out var status);

			if (status == XLib.XBufferOverflow)
			{
				var largeBuffer = stackalloc byte[nbytes + 1];
				nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, largeBuffer, nbytes + 1, out keySym, out status);
				buffer = largeBuffer;
			}

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessKeyboardEvent pressed={pressed} filtered={imeFiltered}: keycode={keyEvent.keycode} keySym={keySym} status={status} nbytes={nbytes}");
			}

			switch (status)
			{
				case XLib.XLookupBoth:
					// Keysym + text. For filtered events this means the IME forwarded
					// a regular key (e.g., IBus passing through ASCII in English mode).
					// Don't set symbols here — the non-filtered forwarded event will
					// handle normal character insertion via the KeyDown path.
					if (imeFiltered)
					{
						// Skip entirely — the non-filtered forwarded event handles KeyDown.
						return;
					}
					symbols = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
					if (string.IsNullOrEmpty(symbols))
					{
						symbols = null;
					}
					break;

				case XLib.XLookupChars:
					// Text only (no keysym) — IME committed text.
					var committed = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
					if (!string.IsNullOrEmpty(committed))
					{
						var imeExtension = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(committed));
					}
					// Don't set symbols — text is handled by composition events, not KeyDown.
					return;

				case XLib.XLookupKeySym:
					// Key only, no text. If filtered, the IME consumed the key for
					// composition — don't raise a KeyDown that would confuse the TextBox.
					if (imeFiltered)
					{
						var ime = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => ime.OnComposing());
						return;
					}
					break;

				case XLib.XLookupNone:
					// No result — IME fully consumed the event. If filtered, signal composing.
					if (imeFiltered)
					{
						var ime = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => ime.OnComposing());
					}
					return;
			}
		}
		else if (imeFiltered)
		{
			// Filtered event but no XIC — nothing to do
			return;
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
