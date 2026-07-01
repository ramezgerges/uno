using Microsoft.UI.Xaml.Controls;
using Uno.UI.Samples.Controls;
namespace UITests.Windows_UI_Xaml_Controls
{
	[Sample("Perf", Name = "PerfText")]
	public sealed partial class PerfText : Page
	{
		public PerfText()
		{
			this.InitializeComponent();
			for (int i = 0; i < 120; i++)
				root.Children.Add(new TextBlock { Text = $"The quick brown fox {i} jumps over the lazy dog 0123456789", FontSize = 16 });
		}
	}
}
