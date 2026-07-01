using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Graphics.Canvas.Effects;
using Uno.UI.Samples.Controls;

namespace UITests.Windows_UI_Xaml_Controls
{
	[Sample("Image", Name = "EffectWebGpu", Description = "WebGPU CompositionEffectBrush: color-matrix effects over solid-color sources")]
	public sealed partial class EffectWebGpu : Page
	{
		public EffectWebGpu()
		{
			this.InitializeComponent();
			Loaded += OnLoaded;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			var c = Microsoft.UI.Xaml.Window.Current.Compositor;
			grayB.Background = Make(c, new GrayscaleEffect { Source = new CompositionEffectSourceParameter("s") }, c.CreateColorBrush(Microsoft.UI.Colors.Red));
			invertB.Background = Make(c, new InvertEffect { Source = new CompositionEffectSourceParameter("s") }, c.CreateColorBrush(Microsoft.UI.Colors.Red));
			tintB.Background = Make(c, new TintEffect { Source = new CompositionEffectSourceParameter("s"), Color = Color.FromArgb(255, 80, 160, 255) }, c.CreateColorBrush(Microsoft.UI.Colors.White));
			satB.Background = Make(c, new SaturationEffect { Source = new CompositionEffectSourceParameter("s"), Saturation = 0f }, c.CreateColorBrush(Microsoft.UI.Colors.Red));
		}

		private static Brush Make(Compositor c, Windows.Graphics.Effects.IGraphicsEffect effect, CompositionBrush src)
		{
			var eb = c.CreateEffectFactory(effect).CreateBrush();
			eb.SetSourceParameter("s", src);
			return new EBrush(eb);
		}

		private sealed class EBrush : XamlCompositionBrushBase
		{
			private readonly CompositionBrush _b;
			public EBrush(CompositionBrush b) => _b = b;
			protected override void OnConnected() => CompositionBrush = _b;
		}
	}
}
