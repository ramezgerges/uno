using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private DataPackage? _currentContent;
	private string? _currentText;

	public event EventHandler<object>? ContentChanged;

	public void StartContentChanged() { }
	public void StopContentChanged() { }

	public void Clear()
	{
		_currentContent = null;
		_currentText = null;
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		// Try wl-paste first for cross-app clipboard
		try
		{
			var process = new Process();
			process.StartInfo.FileName = "wl-paste";
			process.StartInfo.Arguments = "--no-newline";
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.Start();
			var text = process.StandardOutput.ReadToEnd();
			process.WaitForExit(1000);

			if (process.ExitCode == 0 && !string.IsNullOrEmpty(text))
			{
				var package = new DataPackage();
				package.SetText(text);
				return package.GetView();
			}
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"wl-paste not available, falling back to in-process clipboard: {ex.Message}");
			}
		}

		// Fallback to in-process clipboard
		return _currentContent?.GetView();
	}

	public void SetContent(DataPackage? content)
	{
		_currentContent = content;

		// Store text synchronously for wl-copy and capture it before GetView() is needed
		if (content != null)
		{
			try
			{
				var view = content.GetView();
				if (view.Contains(StandardDataFormats.Text))
				{
					// Use synchronous wait since we need the text immediately for wl-copy
					_currentText = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
				}
				else
				{
					_currentText = null;
				}
			}
			catch (Exception ex)
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"Failed to extract text from DataPackage: {ex.Message}");
				}
				_currentText = null;
			}

			// Try to set cross-app clipboard via wl-copy
			if (_currentText != null)
			{
				try
				{
					var process = new Process();
					process.StartInfo.FileName = "wl-copy";
					process.StartInfo.RedirectStandardInput = true;
					process.StartInfo.RedirectStandardError = true;
					process.StartInfo.UseShellExecute = false;
					process.StartInfo.CreateNoWindow = true;
					process.Start();
					process.StandardInput.Write(_currentText);
					process.StandardInput.Close();
					process.WaitForExit(1000);
				}
				catch (Exception ex)
				{
					if (this.Log().IsEnabled(LogLevel.Debug))
					{
						this.Log().Debug($"wl-copy not available, clipboard is in-process only: {ex.Message}");
					}
				}
			}
		}
		else
		{
			_currentText = null;
		}

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}
}
