using Uno.UI.Samples.Controls;
using Microsoft.UI.Xaml.Controls;

namespace UITests.Windows_UI_Xaml_Media.AcrylicBrushTests;

[Sample("Brushes", Description = "Acrylic brush filling an arbitrary (ellipse) shape — WebGPU path-masked backdrop")]
public sealed partial class AcrylicPathWebGpu : Page
{
	public AcrylicPathWebGpu()
	{
		this.InitializeComponent();
	}
}
