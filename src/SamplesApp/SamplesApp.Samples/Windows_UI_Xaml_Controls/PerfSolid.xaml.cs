using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Uno.UI.Samples.Controls;
namespace UITests.Windows_UI_Xaml_Controls
{
	[Sample("Perf", Name = "PerfSolid")]
	public sealed partial class PerfSolid : Page
	{
		public PerfSolid()
		{
			this.InitializeComponent();
			int n = 18;
			for (int i = 0; i < n; i++) { root.RowDefinitions.Add(new RowDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition()); }
			for (int r = 0; r < n; r++) for (int col = 0; col < n; col++)
			{
				var b = new Border { Background = new SolidColorBrush(Color.FromArgb(255, (byte)(r * 14), (byte)(col * 14), 128)), Margin = new(1) };
				Grid.SetRow(b, r); Grid.SetColumn(b, col); root.Children.Add(b);
			}
		}
	}
}
