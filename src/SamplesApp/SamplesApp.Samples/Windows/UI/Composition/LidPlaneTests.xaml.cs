using System.Numerics;
using Uno.UI.Samples.Controls;

using Windows.UI;

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;

namespace UITests.Windows_UI_Composition
{
	[Sample("Microsoft.UI.Composition",
		Name = "LidPlane",
		Description = "Projects live content onto a plane hinged at its bottom edge, blurring and dimming towards the far edge. Driven by a 0..1 progress slider.",
		IsManualTest = true,
		IgnoreInSnapshotTests = true)]
	public sealed partial class LidPlaneTests : UserControl
	{
		private ContainerVisual _root;
		private ContainerVisual _plane;
		private SpriteVisual _dimSprite;
#if HAS_UNO
		private GaussianBlurEffect _blur;
#endif

		public LidPlaneTests()
		{
			this.InitializeComponent();
			this.Loaded += OnLoaded;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
#if HAS_UNO
			if (_root is not null)
			{
				return;
			}

			var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
			var size = new Vector2((float)contentHost.Width, (float)contentHost.Height);

			var surface = compositor.CreateVisualSurface();
			surface.SourceVisual = ElementCompositionPreview.GetElementVisual(contentHost);
			surface.SourceSize = size;

			var surfaceBrush = compositor.CreateSurfaceBrush(surface);

			// A blurred copy is alpha-masked by a vertical ramp and composited over the sharp
			// original, so the blur grows towards the far edge instead of being uniform.
			var source = new CompositionEffectSourceParameter("source");
			_blur = new GaussianBlurEffect
			{
				Name = "blur",
				BlurAmount = 0f,
				BorderMode = EffectBorderMode.Hard,
				Source = source
			};

			var graph = new CompositeEffect
			{
				Mode = CanvasComposite.SourceOver,
				Sources =
				{
					source,
					new AlphaMaskEffect
					{
						Source = _blur,
						AlphaMask = new CompositionEffectSourceParameter("mask")
					}
				}
			};

			var effectBrush = compositor.CreateEffectFactory(graph).CreateBrush();
			effectBrush.SetSourceParameter("source", surfaceBrush);
			effectBrush.SetSourceParameter("mask", CreateVerticalRamp(compositor, Colors.White));

			// The hinge: rotation about the bottom edge.
			_plane = compositor.CreateContainerVisual();
			_plane.Size = size;
			_plane.RotationAxis = Vector3.UnitX;
			_plane.CenterPoint = new Vector3(size.X / 2f, size.Y, 0f);

			var contentSprite = compositor.CreateSpriteVisual();
			contentSprite.Size = size;
			contentSprite.Brush = effectBrush;
			_plane.Children.InsertAtTop(contentSprite);

			// Dimming rides on its own visual so it projects with the plane rather than
			// being baked into the captured surface.
			_dimSprite = compositor.CreateSpriteVisual();
			_dimSprite.Size = size;
			_dimSprite.Brush = CreateVerticalRamp(compositor, Colors.Black);
			_dimSprite.Opacity = 0f;
			_plane.Children.InsertAtTop(_dimSprite);

			_root = compositor.CreateContainerVisual();
			_root.Size = size;
			_root.Children.InsertAtTop(_plane);

			ElementCompositionPreview.SetElementChildVisual(effectHost, _root);

			UpdateEffect();
#endif
		}

		/// <summary>
		/// Transparent at the hinge, fully opaque at the far edge.
		/// </summary>
		private static CompositionLinearGradientBrush CreateVerticalRamp(Compositor compositor, Color color)
		{
			var brush = compositor.CreateLinearGradientBrush();
			brush.MappingMode = CompositionMappingMode.Relative;
			brush.StartPoint = new Vector2(0f, 1f);
			brush.EndPoint = new Vector2(0f, 0f);
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0, color.R, color.G, color.B)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(255, color.R, color.G, color.B)));
			return brush;
		}

		private void UpdateEffect()
		{
			if (_root is null)
			{
				return;
			}

			var progress = (float)progressSlider.Value;
			var size = _root.Size;

			// Perspective is centred on the viewer's line of sight (the middle of the
			// content); the hinge is a separate concern, carried by _plane.CenterPoint.
			var perspective = Matrix4x4.Identity;
			perspective.M34 = -1f / (float)distanceSlider.Value;

			_root.TransformMatrix =
				Matrix4x4.CreateTranslation(-size.X / 2f, -size.Y / 2f, 0f) *
				perspective *
				Matrix4x4.CreateTranslation(size.X / 2f, size.Y / 2f, 0f);

			// At rest the wrapped content is shown directly so it stays interactive; the
			// projection only takes over once the plane starts to tilt.
			var isFlat = progress <= 0f;
			_root.IsVisible = !isFlat;
			sourceHost.Opacity = isFlat || showSource.IsChecked == true ? 1 : 0;

			_plane.RotationAngleInDegrees = progress * (float)angleSlider.Value;
			_dimSprite.Opacity = progress * (float)dimSlider.Value;
#if HAS_UNO
			_blur.BlurAmount = progress * (float)blurSlider.Value;
#endif
		}

		private void OnParameterChanged(object sender, RangeBaseValueChangedEventArgs e)
			=> UpdateEffect();

		private void OnShowSourceChanged(object sender, RoutedEventArgs e)
			=> UpdateEffect();
	}
}
