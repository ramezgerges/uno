﻿using System;
using Windows.Foundation;
using Windows.UI.Xaml.Documents;
using SkiaSharp;
using Windows.UI.Composition;
using System.Numerics;
using Windows.UI.Xaml.Media;
using Uno.UI;
using Windows.UI.Xaml.Documents.TextFormatting;
using Uno.UI.Xaml.Core;
using Uno.UI.Xaml.Media;

#nullable enable

namespace Windows.UI.Xaml.Controls
{
	partial class TextBlock : FrameworkElement, IBlock
	{
		private readonly TextVisual _textVisual;
		private Action? _selectionHighlightColorChanged;

		public TextBlock()
		{
			SetDefaultForeground(ForegroundProperty);
			_textVisual = new TextVisual(Visual.Compositor, this);

			Visual.Children.InsertAtBottom(_textVisual);

			_hyperlinks.CollectionChanged += HyperlinksOnCollectionChanged;
		}

#if DEBUG
		private protected override void OnLoaded()
		{
			base.OnLoaded();
			_textVisual.Comment = $"{Visual.Comment}#text";
		}
#endif

		protected override Size MeasureOverride(Size availableSize)
		{
			var padding = Padding;
			var availableSizeWithoutPadding = availableSize.Subtract(padding).AtLeastZero();
			var defaultLineHeight = GetComputedLineHeight();
			var desiredSize = Inlines.Measure(availableSizeWithoutPadding, defaultLineHeight);

			desiredSize = desiredSize.Add(padding);

			if (GetUseLayoutRounding())
			{
				// In order to prevent text clipping as a result of layout rounding at
				// scales other than 1.0x, the ceiling of the rescaled size is used.
				var plateauScale = RootScale.GetRasterizationScaleForElement(this);
				Size pageNodeSize = desiredSize;
				desiredSize.Width = ((int)Math.Ceiling(pageNodeSize.Width * plateauScale)) / plateauScale;
				desiredSize.Height = ((int)Math.Ceiling(pageNodeSize.Height * plateauScale)) / plateauScale;

				// LsTextLine is not aware of layoutround and uses baseline height to place the rendered text.
				// However, because the height of the *block is potentionally layoutround-ed up, we should adjust the
				// placement of text by the difference.  Horizontal adjustment is not of concern since
				// LsTextLine uses arranged size which is already layoutround-ed.
				//_layoutRoundingHeightAdjustment = desiredSize.Height - pageNodeSize.Height;
			}

			return desiredSize;
		}

		partial void OnIsTextSelectionEnabledChangedPartial() => RecalculateSubscribeToPointerEvents();

		private void ApplyFlowDirection(float width)
		{
			if (this.FlowDirection == FlowDirection.RightToLeft)
			{
				_textVisual.TransformMatrix = new Matrix4x4(new Matrix3x2(-1.0f, 0.0f, 0.0f, 1.0f, width, 0.0f));
			}
			else
			{
				_textVisual.TransformMatrix = Matrix4x4.Identity;
			}
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			var padding = Padding;
			var availableSizeWithoutPadding = finalSize.Subtract(padding);
			var arrangedSizeWithoutPadding = Inlines.Arrange(availableSizeWithoutPadding);
			_textVisual.Size = new Vector2((float)arrangedSizeWithoutPadding.Width, (float)arrangedSizeWithoutPadding.Height);
			_textVisual.Offset = new Vector3((float)padding.Left, (float)padding.Top, 0);
			ApplyFlowDirection((float)finalSize.Width);
			return base.ArrangeOverride(finalSize);
		}

		/// <summary>
		/// Gets the line height of the TextBlock either 
		/// based on the LineHeight property or the default 
		/// font line height.
		/// </summary>
		/// <returns>Computed line height</returns>
		internal float GetComputedLineHeight()
		{
			var lineHeight = LineHeight;
			if (!lineHeight.IsNaN() && lineHeight > 0)
			{
				return (float)lineHeight;
			}
			else
			{
				var font = FontDetailsCache.GetFont(FontFamily?.Source, (float)FontSize, FontWeight, FontStyle);
				return font.LineHeight;
			}
		}

		internal override void OnPropertyChanged2(DependencyPropertyChangedEventArgs args)
		{
			base.OnPropertyChanged2(args);
			if (args.Property == FlowDirectionProperty)
			{
				ApplyFlowDirection((float)this.RenderSize.Width);
			}
		}

		private Hyperlink? FindHyperlinkAt(Point point)
		{
			var padding = Padding;
			var span = Inlines.GetRenderSegmentSpanAt(point - new Point(padding.Left, padding.Top), false)?.span;

			if (span == null)
			{
				return null;
			}

			var inline = span.Segment.Inline;

			while ((inline = inline.GetParent() as Inline) != null)
			{
				if (inline is Hyperlink hyperlink)
				{
					return hyperlink;
				}
			}

			return null;
		}

		partial void OnInlinesChangedPartial()
		{
			Inlines.InvalidateMeasure();
		}

		// Invalidate Inlines measure when any IBlock properties used during measuring change:

		partial void OnMaxLinesChangedPartial()
		{
			Inlines.InvalidateMeasure();
		}

		partial void OnTextWrappingChangedPartial()
		{
			Inlines.InvalidateMeasure();
		}

		partial void OnLineHeightChangedPartial()
		{
			Inlines.InvalidateMeasure();
		}

		partial void OnLineStackingStrategyChangedPartial()
		{
			Inlines.InvalidateMeasure();
		}

		partial void OnSelectionHighlightColorChangedPartial(SolidColorBrush brush)
		{
			Inlines.InvalidateMeasure();
		}

		public void Invalidate(bool updateText) => InvalidateInlines(updateText);

		private void SetupInlines()
		{
			_inlines.FireDrawingEventsOnEveryRedraw = true;
			_inlines.RenderSelection = true;
			_inlines.SelectionFound += t =>
			{
				var canvas = t.canvas;
				var rect = t.rect;
				canvas.DrawRect(new SKRect((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom), new SKPaint
				{
					Color = SelectionHighlightColor.Color.ToSKColor(), Style = SKPaintStyle.Fill
				});
			};
		}

		// The following should be moved to TextBlock.cs when we implement SelectionHighlightColor for the other platforms
		public SolidColorBrush SelectionHighlightColor
		{
			get => (SolidColorBrush)GetValue(SelectionHighlightColorProperty);
			set => SetValue(SelectionHighlightColorProperty, value);
		}

		public static DependencyProperty SelectionHighlightColorProperty { get; } =
			DependencyProperty.Register(
				nameof(SelectionHighlightColor),
				typeof(SolidColorBrush),
				typeof(TextBlock),
				new FrameworkPropertyMetadata(
					DefaultBrushes.SelectionHighlightColor,
					propertyChangedCallback: (s, e) => ((TextBlock)s)?.OnSelectionHighlightColorChanged((SolidColorBrush)e.OldValue, (SolidColorBrush)e.NewValue)));

		private void OnSelectionHighlightColorChanged(SolidColorBrush? oldBrush, SolidColorBrush? newBrush)
		{
			oldBrush ??= DefaultBrushes.SelectionHighlightColor;
			newBrush ??= DefaultBrushes.SelectionHighlightColor;
			Brush.SetupBrushChanged(oldBrush, newBrush, ref _selectionHighlightColorChanged, () => OnSelectionHighlightColorChangedPartial(newBrush));
		}

		partial void OnSelectionHighlightColorChangedPartial(SolidColorBrush brush);
	}
}
