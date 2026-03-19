#nullable enable

using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Ime;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml.Controls.Extensions;

namespace Uno.UI.Runtime.Skia.Win32;

/// <summary>
/// Win32 IMM32-based implementation of <see cref="IImeTextBoxExtension"/>.
/// Handles WM_IME_STARTCOMPOSITION, WM_IME_COMPOSITION, and WM_IME_ENDCOMPOSITION
/// messages to provide IME support for TextBox on Win32 Skia.
/// </summary>
internal sealed class Win32ImeTextBoxExtension : IImeTextBoxExtension
{
	internal static Win32ImeTextBoxExtension Instance { get; } = new();

	private HWND _hwnd;
	private bool _isComposing;

	private Win32ImeTextBoxExtension()
	{
	}

	public bool IsComposing => _isComposing;

	public event EventHandler? CompositionStarted;
	public event EventHandler<ImeCompositionEventArgs>? CompositionUpdated;
	public event EventHandler<ImeCompositionEventArgs>? CompositionCompleted;
	public event EventHandler? CompositionEnded;

	public void StartImeSession(TextBox textBox)
	{
		_hwnd = HWND.Null;

		if (textBox.XamlRoot is not { } xamlRoot)
		{
			return;
		}

		if (XamlRootMap.GetHostForRoot(xamlRoot) is not Win32WindowWrapper wrapper)
		{
			return;
		}

		if (wrapper.NativeWindow is not Win32NativeWindow nativeWindow)
		{
			return;
		}

		_hwnd = (HWND)nativeWindow.Hwnd;
	}

	public void EndImeSession()
	{
		if (_isComposing && !_hwnd.IsNull)
		{
			// Tell the IME to commit the active composition and close its windows
			var himc = PInvoke.ImmGetContext(_hwnd);
			if (!himc.IsNull)
			{
				PInvoke.ImmNotifyIME(himc, NOTIFY_IME_ACTION.NI_COMPOSITIONSTR, NOTIFY_IME_INDEX.CPS_COMPLETE, 0);
				PInvoke.ImmReleaseContext(_hwnd, himc);
			}

			_isComposing = false;
			CompositionEnded?.Invoke(this, EventArgs.Empty);
		}

		_hwnd = HWND.Null;
	}

	/// <summary>
	/// Called from WndProc when WM_IME_STARTCOMPOSITION is received.
	/// </summary>
	internal void OnWmImeStartComposition()
	{
		if (_hwnd.IsNull)
		{
			return;
		}

		_isComposing = true;
		CompositionStarted?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Called from WndProc when WM_IME_COMPOSITION is received.
	/// </summary>
	internal unsafe void OnWmImeComposition(LPARAM lParam)
	{
		if (_hwnd.IsNull)
		{
			return;
		}

		var himc = PInvoke.ImmGetContext(_hwnd);
		if (himc.IsNull)
		{
			return;
		}

		try
		{
			var flags = (IME_COMPOSITION_STRING)(uint)lParam.Value;

			// GCS_RESULTSTR: The user has committed text from the IME
			if (flags.HasFlag(IME_COMPOSITION_STRING.GCS_RESULTSTR))
			{
				var text = GetCompositionString(himc, IME_COMPOSITION_STRING.GCS_RESULTSTR);
				if (text is not null)
				{
					CompositionCompleted?.Invoke(this, new ImeCompositionEventArgs(text));
				}
			}

			// GCS_COMPSTR: The composition string has changed
			if (flags.HasFlag(IME_COMPOSITION_STRING.GCS_COMPSTR))
			{
				var text = GetCompositionString(himc, IME_COMPOSITION_STRING.GCS_COMPSTR);
				if (text is not null)
				{
					CompositionUpdated?.Invoke(this, new ImeCompositionEventArgs(text));
				}
			}
		}
		finally
		{
			PInvoke.ImmReleaseContext(_hwnd, himc);
		}
	}

	/// <summary>
	/// Called from WndProc when WM_IME_ENDCOMPOSITION is received.
	/// </summary>
	internal void OnWmImeEndComposition()
	{
		if (!_isComposing)
		{
			return;
		}

		_isComposing = false;
		CompositionEnded?.Invoke(this, EventArgs.Empty);
	}

	private static unsafe string? GetCompositionString(HIMC himc, IME_COMPOSITION_STRING dwIndex)
	{
		// First call to get the byte length
		var byteLen = PInvoke.ImmGetCompositionString(himc, dwIndex, null, 0);
		if (byteLen <= 0)
		{
			return dwIndex == IME_COMPOSITION_STRING.GCS_COMPSTR ? string.Empty : null;
		}

		var buffer = stackalloc byte[byteLen];
		var result = PInvoke.ImmGetCompositionString(himc, dwIndex, buffer, (uint)byteLen);
		if (result <= 0)
		{
			return null;
		}

		return new string((char*)buffer, 0, result / sizeof(char));
	}
}
