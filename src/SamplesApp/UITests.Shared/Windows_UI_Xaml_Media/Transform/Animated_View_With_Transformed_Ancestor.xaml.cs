using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Uno.UI.Samples.Controls;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace UITests.Shared.Windows_UI_Xaml_Media.Transform
{
	[Sample("Transform", "Animations", IgnoreInSnapshotTests = true)]
	public sealed partial class Animated_View_With_Transformed_Ancestor : UserControl
	{
		public Animated_View_With_Transformed_Ancestor()
		{
			this.InitializeComponent();
		}

		private void Button_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
		{
			ValueFlyout.Items.Clear();
			var cancelOption = new MenuFlyoutItem
			{
				// Add space for Uno rendering bug
				Text = $"(select this to cancel)" + " ",
				Icon = new SymbolIcon(Symbol.Cancel),
				Tag = "CANCEL"
			};
			//cancelOption.Click += FlyoutItem_Click;
			ValueFlyout.Items.Add(cancelOption);
			ValueFlyout.Items.Add(new MenuFlyoutSeparator());

			var newItem = new MenuFlyoutItem
			{
				Text = "New Item 1",
				Icon = new SymbolIcon(Symbol.Target)
			};
			ValueFlyout.Items.Add(newItem);

			newItem = new MenuFlyoutItem
			{
				Text = "New Item 2"
			};
			ValueFlyout.Items.Add(newItem);

			newItem = new MenuFlyoutItem
			{
				Text = "New Item 3"
			};
			ValueFlyout.Items.Add(newItem);

			ValueFlyout.ShowAt((FrameworkElement)sender, new FlyoutShowOptions
			{
				Placement = FlyoutPlacementMode.Auto,
				ShowMode = FlyoutShowMode.Standard
			});
		}
	}
}
