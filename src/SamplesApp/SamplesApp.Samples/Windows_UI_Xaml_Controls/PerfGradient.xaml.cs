using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using Windows.Foundation;
using Uno.UI.Samples.Controls;
namespace UITests.Windows_UI_Xaml_Controls
{
	[Sample("Perf", Name = "PerfGradient")]
	public sealed partial class PerfGradient : Page
	{
		public PerfGradient()
		{
			this.InitializeComponent();
			int n = 12;
			for (int i = 0; i < n; i++) { root.RowDefinitions.Add(new RowDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition()); }
			for (int r = 0; r < n; r++) for (int col = 0; col < n; col++)
			{
				var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
				g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, (byte)(r * 20), 80, 200), Offset = 0 });
				g.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, (byte)(col * 20), 60), Offset = 1 });
				var b = new Border { Background = g, Margin = new(2), CornerRadius = new(6) };
				Grid.SetRow(b, r); Grid.SetColumn(b, col); root.Children.Add(b);
			}
		}
	}
}
