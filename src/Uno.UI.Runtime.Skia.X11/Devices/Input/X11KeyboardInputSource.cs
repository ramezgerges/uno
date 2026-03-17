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

	internal unsafe void ProcessKeyboardEvent(XKeyEvent keyEvent, bool pressed)
	{
		var xic = X11ImeTextBoxExtension.GetXicForWindow(keyEvent.window);

		string? symbols = null;
		nint keySym = 0;

		if (xic != IntPtr.Zero && pressed)
		{
			// Use Xutf8LookupString for IME-aware text lookup.
			// This is only called for non-filtered events (filtered events are
			// handled in the event loop by signaling OnComposing).
			// When the IME commits, it synthesizes a non-filtered KeyPress that
			// Xutf8LookupString returns XLookupChars for.
			var buffer = stackalloc byte[64];
			int nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, buffer, 64, out keySym, out var status);

			if (status == XLib.XBufferOverflow)
			{
				var largeBuffer = stackalloc byte[nbytes + 1];
				nbytes = XLib.Xutf8LookupString(xic, ref keyEvent, largeBuffer, nbytes + 1, out keySym, out status);
				buffer = largeBuffer;
			}

			var lookupText = nbytes > 0 ? System.Text.Encoding.UTF8.GetString(buffer, nbytes) : null;

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessKeyboardEvent pressed={pressed}: keycode={keyEvent.keycode} keySym={keySym} status={status} nbytes={nbytes} text='{lookupText}'");
			}

			switch (status)
			{
				case XLib.XLookupBoth:
					// Keysym + text — regular key forwarded by IME (e.g., ASCII in English mode).
					if (!string.IsNullOrEmpty(lookupText))
					{
						symbols = lookupText;
					}
					break;

				case XLib.XLookupChars:
					// Text only (no keysym) — IME committed text.
					if (!string.IsNullOrEmpty(lookupText))
					{
						var imeExtension = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(lookupText));
					}
					return;

				case XLib.XLookupKeySym:
					// Keysym only. However, some IMEs (e.g., IBus) put committed text
					// in the buffer even with this status. If we have text, treat it
					// as a commit.
					if (!string.IsNullOrEmpty(lookupText))
					{
						var imeExtension = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(lookupText));
						return;
					}
					// No text — proceed to KeyDown without unicode character.
					break;

				case XLib.XLookupNone:
					// No result — nothing to dispatch.
					return;
			}
		}
		else
		{
			// No XIC or key release — use classic XLookupString
			var buffer = stackalloc byte[4];
			int nbytes = XLib.XLookupString(ref keyEvent, buffer, 4, out keySym, IntPtr.Zero);

			var text = System.Text.Encoding.UTF8.GetString(buffer, nbytes);

			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"ProcessKeyboardEvent pressed={pressed}: keycode={keyEvent.keycode} keySym={keySym} vk={X11KeyTransform.VirtualKeyFromKeySym(keySym)} text='{text}' nbytes={nbytes}");
			}
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

		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"Dispatching {(pressed ? "KeyDown" : "KeyUp")}: vk={X11KeyTransform.VirtualKeyFromKeySym(keySym)} unicodeKey={(symbols?.Length > 0 ? symbols[0].ToString() : "null")}");
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
