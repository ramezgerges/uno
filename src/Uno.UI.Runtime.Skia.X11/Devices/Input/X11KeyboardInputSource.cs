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
				case XLib.XLookupChars:
				case XLib.XLookupBoth:
					// IME committed text
					var committed = System.Text.Encoding.UTF8.GetString(buffer, nbytes);
					if (!string.IsNullOrEmpty(committed))
					{
						var imeExtension = X11ImeTextBoxExtension.Instance;
						X11XamlRootHost.QueueAction(_host, () => imeExtension.OnCommittedText(committed));

						// For XLookupBoth, we also have a keysym — use it for the key event
						if (status == XLib.XLookupBoth)
						{
							symbols = committed;
						}
						else
						{
							// XLookupChars only — no keysym, just committed text.
							// Still raise key events so the input pipeline processes them.
							symbols = committed;
						}
					}
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
