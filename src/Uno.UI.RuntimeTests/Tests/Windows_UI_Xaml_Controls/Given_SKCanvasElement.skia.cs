﻿using System.Drawing;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Private.Infrastructure;
using SkiaSharp;
using Uno.UI.RuntimeTests.Helpers;
using Size = Windows.Foundation.Size;
namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
public class Given_SKCanvasElement
{
	[TestMethod]
	public async Task When_Clipped_Inside_ScrollViewer()
	{
		var SUT = new BlueFillSKCanvasElement
		{
			Height = 400,
			Width = 400
		};

		var border = new Border
		{
			BorderBrush = Colors.Green,
			Height = 400,
			Child = new ScrollViewer
			{
				VerticalAlignment = VerticalAlignment.Top,
				Height = 100,
				Background = Colors.Red,
				Content = SUT
			}
		};

		await UITestHelper.Load(border);

		var bitmap = await UITestHelper.ScreenShot(border);

		ImageAssert.HasColorInRectangle(bitmap, new Rectangle(0, 0, 400, 300), Colors.Blue);
		ImageAssert.DoesNotHaveColorInRectangle(bitmap, new Rectangle(0, 101, 400, 299), Colors.Blue);
	}

	private class BlueFillSKCanvasElement : SKCanvasElement
	{
		protected override void RenderOverride(SKCanvas canvas, Size area)
		{
			canvas.DrawRect(new SKRect(0, 0, (float)area.Width, (float)area.Height), new SKPaint { Color = SKColors.Blue });
		}
	}
}
