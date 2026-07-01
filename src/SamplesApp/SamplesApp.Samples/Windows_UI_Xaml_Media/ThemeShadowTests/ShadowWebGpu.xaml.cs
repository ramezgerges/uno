using System.Numerics;
using Uno.UI.Samples.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace UITests.Windows_UI_Xaml_Media.ThemeShadowTests;

[Sample("Microsoft.UI.Xaml.Media", Description = "WebGPU drop shadow: opaque vs semi-transparent caster")]
public sealed partial class ShadowWebGpu : Page
{
	public ShadowWebGpu()
	{
		this.InitializeComponent();
		Loaded += (s, e) =>
		{
			OpaqueCard.Shadow = new ThemeShadow();
			OpaqueCard.Translation = new Vector3(0, 0, 48);
			TransCard.Shadow = new ThemeShadow();
			TransCard.Translation = new Vector3(0, 0, 48);
		};
	}
}
