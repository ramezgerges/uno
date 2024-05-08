---
uid: Uno.Controls.SKCanvasElement
---

## Introduction

When creating an Uno Platform application, developers might want to create elaborate 2D graphics using a library such as [Skia](https://skia.org) or [Cairo](https://www.cairographics.org), rather than using, for example, a simple [Canvas](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.canvas). To support this use case, SkiaSharp comes with an [SKXamlCanvas](https://learn.microsoft.com/dotnet/api/skiasharp.views.windows.skxamlcanvas) element that allows for drawing in an area using SkiaSharp.

On Uno Platform Skia Desktop targets, we can utilize the pre-existing internal Skia canvas used to render the application window instead of creating additional Skia surfaces. It is then possible to copy the resulting renderings to the application (e.g. using a [BitmapImage](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.imaging.bitmapimage)). This way, a lot of Skia functionally can be acquired "for free". For example, no additional additional packages are needed, and setup for hardware acceleration is not needed if the Uno application is already using OpenGL to render.

This functionality is exposed in two parts. `SkiaVisual` is a [Composition API Visual](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.visual) that gets an [SKCanvas](https://learn.microsoft.com/dotnet/api/skiasharp.skcanvas) to draw on and is almost completely unmanaged. For more streamlined usage, an `SKCanvasElement` is provided that internally wraps a `SkiaVisual` and can be used like any `FrameworkElement`, with support for sizing, clipping, RTL, etc. You should use `SKCanvasElement` for most scenarios. Only use a raw `SkiaVisual` if your use case is not covered by `SKCanvasElement`.

> [!IMPORTANT]
> This functionality is only available on Skia Desktop (`net8.0-desktop`) targets.

## SkiaVisual

A `SkiaVisual` is an abstract `Visual` that provides Uno Platform applications the ability to utilize SkiaSharp to draw directly on the Skia canvas that is used internally by Uno to draw the window. To use `SkiaVisual`, create a subclass of `SkiaVisual` and override the `RenderOverride` method.

```csharp
protected abstract void RenderOverride(SKCanvas canvas);
```

You can then add the `SkiaVisual` as a child visual of an element using [ElementCompositionPreview.SetElementChildVisual](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.hosting.elementcompositionpreview.setelementchildvisual#microsoft-ui-xaml-hosting-elementcompositionpreview-setelementchildvisual(microsoft-ui-xaml-uielement-microsoft-ui-composition-visual)).

Note that you will need to add your own logic to handle sizing and clipping.

When adding your drawing logic in `RenderOverride` on the provided canvas, you can assume that the origin is already translated so that `0,0` is the origin of the visual, not the entire window.

Additionally, `SkiaVisual` has an `Invalidate` method that can be used at any time to tell the Uno Platform runtime to redraw the visual, calling `RenderOverride` in the process.

## SKCanvasElement

`SKCanvasElement` is a ready-made `FrameworkElement` that creates an internal `SkiaVisual` and maintains its state as one would expect. To use `SKCanvasElement`, create a subclass of `SKCanvasElement` and override the `RenderOverride` method, which takes the canvas that will be drawn on and the clipping area inside the canvas. Drawing outside this area will be clipped.

```csharp
protected abstract void RenderOverride(SKCanvas canvas, Size area);
```

By default, `SKCanvasElement` takes all the available space given to it in the `Measure` cycle. If you want to customize how much space the element takes, you can override its `MeasureOverride` method.

Note that since `SKCanvasElement` takes as much space as it can, it's not allowed to place an `SKCanvasElement` inside a `StackPanel`, a `Grid` with `Auto` sizing, or any other element that provides its child(ren) with infinite space. To work around this, you can explicitly set the `Width` and/or `Height` of the `SKCanvasElement`.

## Full example

To see this in action, here's a complete sample that uses `SKCanvasElement` to draw 1 of 3 different drawings based on the value of a [Slider](https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.slider). Note how you have to be careful with surrounding all the Skia-related logic in platform-specific guards. This is the case for both the [XAML](platform-specific-xaml) and the [code-behind](platform-specific-csharp).

XAML:

```xaml
<!-- SKCanvasElementExample.xaml -->
<UserControl x:Class="BlankApp.SKCanvasElementExample"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="using:BlankApp"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:skia="http://uno.ui/skia"
             xmlns:not_skia="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             mc:Ignorable="d"
             d:DesignHeight="300"
             d:DesignWidth="400">

	<skia:Grid>
		<Grid.RowDefinitions>
			<RowDefinition Height="Auto" />
			<RowDefinition Height="Auto" />
			<RowDefinition Height="*" />
		</Grid.RowDefinitions>
		<Slider Grid.Row="1" x:Name="slider" Header="Sample" Minimum="0" Maximum="{x:Bind MaxSampleIndex}" />
		<local:SKCanvasElementImpl Grid.Row="2" Sample="{x:Bind slider.Value, Mode=OneWay}" />
	</skia:Grid>
	<not_skia:TextBlock Text="This sample is only supported on skia." />
</UserControl>
```

Code-behind:

```csharp
// SKCanvasElementExample.xaml.cs
public partial class SKCanvasElementExample : UserControl
{
    public int MaxSampleIndex => SKCanvasElementImpl.SampleCount - 1;

    public SKCanvasElement_Simple()
    {
        this.InitializeComponent();
    }
}
```

```csharp
// SKCanvasElementImpl.skia.cs <-- NOTICE the `.skia`
public class SKCanvasElementImpl : SKCanvasElement
{
	public static int SampleCount => 3;

	public static DependencyProperty SampleProperty { get; } = DependencyProperty.Register(
		nameof(Sample),
		typeof(int),
		typeof(SKCanvasElementImpl),
		new PropertyMetadata(0, (o, args) => ((SKCanvasElementImpl)o).SampleChanged((int)args.NewValue)));

	public int Sample
	{
		get => (int)GetValue(SampleProperty);
		set => SetValue(SampleProperty, value);
	}

	private void SampleChanged(int newIndex)
	{
		Sample = Math.Min(Math.Max(0, newIndex), SampleCount - 1);
	}

	protected override void RenderOverride(SKCanvas canvas, Size area)
	{
		var minDim = Math.Min(area.Width, area.Height);
		// rescale to fit the given area, assuming each drawing takes is 260x260
		canvas.Scale((float)(minDim / 260), (float)(minDim / 260));

		switch (Sample)
		{
			case 0:
				SkiaDrawing0(canvas);
				break;
			case 1:
				SkiaDrawing1(canvas);
				break;
			case 2:
				SkiaDrawing2(canvas);
				break;
		}
	}

	// https://fiddle.skia.org/c/@shapes
	private void SkiaDrawing0(SKCanvas canvas)
	{
		canvas.DrawColor(SKColors.White);

		var paint = new SKPaint();
		paint.Style = SKPaintStyle.Fill;
		paint.IsAntialias = true;
		paint.StrokeWidth = 4;
		paint.Color = new SKColor(0xff4285F4);

		var rect = SKRect.Create(10, 10, 100, 160);
		canvas.DrawRect(rect, paint);

		var oval = new SKPath();
		oval.AddRoundRect(rect, 20, 20);
		oval.Offset(new SKPoint(40, 80));
		paint.Color = new SKColor(0xffDB4437);
		canvas.DrawPath(oval, paint);

		paint.Color = new SKColor(0xff0F9D58);
		canvas.DrawCircle(180, 50, 25, paint);

		rect.Offset(80, 50);
		paint.Color = new SKColor(0xffF4B400);
		paint.Style = SKPaintStyle.Stroke;
		canvas.DrawRoundRect(rect, 10, 10, paint);
	}

	// https://fiddle.skia.org/c/@bezier_curves
	private void SkiaDrawing1(SKCanvas canvas)
	{
		canvas.DrawColor(SKColors.White);

		var paint = new SKPaint();
		paint.Style = SKPaintStyle.Stroke;
		paint.StrokeWidth = 8;
		paint.Color = new SKColor(0xff4285F4);
		paint.IsAntialias = true;
		paint.StrokeCap = SKStrokeCap.Round;

		var path = new SKPath();
		path.MoveTo(10, 10);
		path.QuadTo(256, 64, 128, 128);
		path.QuadTo(10, 192, 250, 250);
		canvas.DrawPath(path, paint);
	}

	// https://fiddle.skia.org/c/@shader
	private void SkiaDrawing2(SKCanvas canvas)
	{
		var paint = new SKPaint();
		using var pathEffect = SKPathEffect.CreateDiscrete(10.0f, 4.0f);
		paint.PathEffect = pathEffect;
		SKPoint[] points =
		{
			new SKPoint(0.0f, 0.0f),
			new SKPoint(256.0f, 256.0f)
		};
		SKColor[] colors =
		{
			new SKColor(66, 133, 244),
			new SKColor(15, 157, 88)
		};
		paint.Shader = SKShader.CreateLinearGradient(points[0], points[1], colors, SKShaderTileMode.Clamp);
		paint.IsAntialias = true;
		canvas.Clear(SKColors.White);
		var path = Star();
		canvas.DrawPath(path, paint);

		SKPath Star()
		{
			const float R = 60.0f, C = 128.0f;
			var path = new SKPath();
			path.MoveTo(C + R, C);
			for (var i = 1; i < 15; ++i)
			{
				var a = 0.44879895f * i;
				var r = R + R * (i % 2);
				path.LineTo((float)(C + r * Math.Cos(a)), (float)(C + r * Math.Sin(a)));
			}
			return path;
		}
	}
}
```
