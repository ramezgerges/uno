using System.Numerics;
using Uno.UI.Samples.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace UITests.Windows_UI_Xaml_Controls;
[Sample("Image", Description = "Rounded clip through the segmented (shadow) path")]
public sealed partial class RoundedWebGpu : Page
{
	public RoundedWebGpu()
	{
		this.InitializeComponent();
		Loaded += (s, e) => { Shadowed.Shadow = new ThemeShadow(); Shadowed.Translation = new Vector3(0, 0, 32); };
	}
}
