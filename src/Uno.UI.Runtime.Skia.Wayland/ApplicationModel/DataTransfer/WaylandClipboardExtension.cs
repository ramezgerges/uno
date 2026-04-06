using System;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Uno.ApplicationModel.DataTransfer;
using Uno.Foundation.Logging;

namespace Uno.WinUI.Runtime.Skia.Wayland;

/// <summary>
/// Clipboard using wl-copy/wl-paste as a bridge to the Wayland clipboard protocol.
/// These tools handle all the wl_data_device protocol complexity including thread safety
/// and protocol versioning. They open their own Wayland connections and don't interfere
/// with the application's event loop.
///
/// Install: sudo apt install wl-clipboard
/// </summary>
internal class WaylandClipboardExtension : IClipboardExtension
{
	private static WaylandClipboardExtension? _instance;
	internal static WaylandClipboardExtension Instance => _instance ??= new();

	private DataPackage? _fallbackContent;

	public event EventHandler<object>? ContentChanged;

	public void StartContentChanged() { }
	public void StopContentChanged() { }

	public void Clear()
	{
		_fallbackContent = null;
		try { RunProcess("wl-copy", "--clear"); } catch { }
		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Flush() { }

	public DataPackageView? GetContent()
	{
		// Try wl-paste for cross-app clipboard
		var text = RunProcessGetOutput("wl-paste", "--no-newline");
		if (text != null)
		{
			var package = new DataPackage();
			package.SetText(text);
			return package.GetView();
		}

		return _fallbackContent?.GetView();
	}

	public void SetContent(DataPackage? content)
	{
		_fallbackContent = content;

		if (content != null)
		{
			try
			{
				var view = content.GetView();
				if (view.Contains(StandardDataFormats.Text))
				{
					var text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
					if (text != null)
					{
						RunProcessWithInput("wl-copy", text);
					}
				}
			}
			catch (Exception ex)
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"wl-copy failed: {ex.Message}");
				}
			}
		}

		ContentChanged?.Invoke(this, EventArgs.Empty);
	}

	private static string? RunProcessGetOutput(string command, string args)
	{
		try
		{
			using var process = new Process();
			process.StartInfo.FileName = command;
			process.StartInfo.Arguments = args;
			process.StartInfo.RedirectStandardOutput = true;
			process.StartInfo.RedirectStandardError = true;
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.Start();
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(2000);
			if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
			{
				return output;
			}
		}
		catch { }
		return null;
	}

	private static void RunProcessWithInput(string command, string input)
	{
		using var process = new Process();
		process.StartInfo.FileName = command;
		process.StartInfo.RedirectStandardInput = true;
		process.StartInfo.RedirectStandardError = true;
		process.StartInfo.UseShellExecute = false;
		process.StartInfo.CreateNoWindow = true;
		process.Start();
		process.StandardInput.Write(input);
		process.StandardInput.Close();
		process.WaitForExit(2000);
	}

	private static void RunProcess(string command, string args)
	{
		using var process = new Process();
		process.StartInfo.FileName = command;
		process.StartInfo.Arguments = args;
		process.StartInfo.RedirectStandardError = true;
		process.StartInfo.UseShellExecute = false;
		process.StartInfo.CreateNoWindow = true;
		process.Start();
		process.WaitForExit(2000);
	}
}
