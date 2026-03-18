using System;
using System.Text;
using Uno.UI.Samples.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Uno.UI.Samples.Content.UITests.TextBoxControl
{
	[Sample("TextBox", Name = "TextBox_X11_IME_Debug", Description = "X11 IME Debug — shows D-Bus IME event flow")]
	public sealed partial class TextBox_X11_IME_Debug : UserControl
	{
		private readonly StringBuilder _eventLog = new();
		private int _eventCount;

		public TextBox_X11_IME_Debug()
		{
			this.InitializeComponent();
			this.Loaded += OnLoaded;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			DetectBackend();

			InputTextBox.GotFocus += (_, _) => AddLog("TextBox GotFocus");
			InputTextBox.LostFocus += (_, _) => AddLog("TextBox LostFocus");
			InputTextBox.TextChanged += OnTextChanged;

#if HAS_UNO
			// Try to hook into X11 IME events if on Linux/X11
			TryHookImeEvents();
#else
			BackendText.Text = "N/A (not X11)";
#endif
		}

		private void DetectBackend()
		{
			if (!OperatingSystem.IsLinux())
			{
				BackendText.Text = "N/A (not Linux)";
				return;
			}

			var gtkImModule = Environment.GetEnvironmentVariable("GTK_IM_MODULE");
			var unoImModule = Environment.GetEnvironmentVariable("UNO_IM_MODULE");
			var xmodifiers = Environment.GetEnvironmentVariable("XMODIFIERS");

			var detected = unoImModule ?? gtkImModule ?? "(from XMODIFIERS)";
			BackendText.Text = $"{detected}";
			AddLog($"Env: UNO_IM_MODULE={unoImModule}, GTK_IM_MODULE={gtkImModule}, XMODIFIERS={xmodifiers}");
		}

#if HAS_UNO
		private void TryHookImeEvents()
		{
			try
			{
				// Use reflection to access X11-specific IME types without compile-time dependency
				var imeExtType = Type.GetType("Uno.WinUI.Runtime.Skia.X11.X11ImeTextBoxExtension, Uno.UI.Runtime.Skia.X11");
				if (imeExtType is null)
				{
					AddLog("X11ImeTextBoxExtension not found (not X11 runtime)");
					return;
				}

				var instanceProp = imeExtType.GetProperty("Instance",
					System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
				if (instanceProp is null)
				{
					AddLog("Instance property not found");
					return;
				}

				var instance = instanceProp.GetValue(null);
				if (instance is null)
				{
					AddLog("Instance is null");
					return;
				}

				// Subscribe to CompositionStarted
				var startedEvent = imeExtType.GetEvent("CompositionStarted");
				startedEvent?.AddEventHandler(instance, new EventHandler((_, _) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						StateText.Text = "Composing";
						AddLog("CompositionStarted");
					});
				}));

				// Subscribe to CompositionCompleted
				var completedEvent = imeExtType.GetEvent("CompositionCompleted");
				if (completedEvent is not null)
				{
					// ImeCompositionEventArgs handler
					var handler = new EventHandler<Uno.UI.Xaml.Controls.Extensions.ImeCompositionEventArgs>((_, args) =>
					{
						DispatcherQueue.TryEnqueue(() =>
						{
							LastCommitText.Text = args.Text ?? "(empty)";
							AddLog($"CommitText: '{args.Text}'");
						});
					});
					completedEvent.AddEventHandler(instance, handler);
				}

				// Subscribe to CompositionUpdated
				var updatedEvent = imeExtType.GetEvent("CompositionUpdated");
				if (updatedEvent is not null)
				{
					var handler = new EventHandler<Uno.UI.Xaml.Controls.Extensions.ImeCompositionEventArgs>((_, args) =>
					{
						DispatcherQueue.TryEnqueue(() =>
						{
							LastPreeditText.Text = args.Text ?? "(cleared)";
							AddLog($"Preedit: '{args.Text}'");
						});
					});
					updatedEvent.AddEventHandler(instance, handler);
				}

				// Subscribe to CompositionEnded
				var endedEvent = imeExtType.GetEvent("CompositionEnded");
				endedEvent?.AddEventHandler(instance, new EventHandler((_, _) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						StateText.Text = "Idle";
						LastPreeditText.Text = "(cleared)";
						AddLog("CompositionEnded");
					});
				}));

				AddLog("IME event hooks registered");
			}
			catch (Exception ex)
			{
				AddLog($"Hook error: {ex.Message}");
			}
		}
#endif

		private void OnTextChanged(object sender, TextChangedEventArgs e)
		{
			LastKeyText.Text = InputTextBox.Text.Length > 0
				? InputTextBox.Text[^1].ToString()
				: "(empty)";
		}

		private void AddLog(string message)
		{
			_eventCount++;
			_eventLog.Insert(0, $"[{_eventCount:D3}] {message}\n");

			// Keep log reasonable
			if (_eventLog.Length > 2000)
			{
				_eventLog.Length = 2000;
			}

			EventLogText.Text = _eventLog.ToString();
		}
	}
}
