using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Uno.UI.Samples.Controls;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace UITests.Shared.Windows_UI_Xaml_Media.Transform
{
	[Sample("Transform", "Animations", IgnoreInSnapshotTests = true)]
	public sealed partial class Animated_View_With_Transformed_Ancestor : UserControl
	{
		public Animated_View_With_Transformed_Ancestor()
		{
			this.InitializeComponent();

			tb.Inlines.Add(new Run() { Text = "some text lol lol" });
			tb.Inlines.Add(new LineBreak());
			tb.Inlines.Add(new Run() { Text = "some text lol lol" });
			tb.Inlines.Add(new Run() { Text = "some text lol lol" });
			tb.Inlines.Add(new LineBreak());
			tb.Inlines.Add(new Run() { Text = "some text \n\rlol lol" });

			Hyperlink hyperlink = new Hyperlink();
			Run run = new Run();
			run.Text = "Go to Bing";
			hyperlink.NavigateUri = new Uri("http://www.bing.com");
			hyperlink.Inlines.Add(run);
			tb.Inlines.Add(hyperlink);

			tb.Inlines.Add(new LineBreak());
			tb.Inlines.Add(new Run() { Text = "This is larger text", FontSize = 20 });
			tb.Inlines.Add(new Run() { Text = "This is even larger text", FontSize = 25 });
		}

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPageIndex = MyPipsPager.SelectedPageIndex;
            if (selectedPageIndex < MyPipsPager.NumberOfPages - 1)
            {
                MyPipsPager.SelectedPageIndex = selectedPageIndex + 1;
            }
        }
        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPageIndex = MyPipsPager.SelectedPageIndex;
            if (selectedPageIndex > 0)
            {
                MyPipsPager.SelectedPageIndex = selectedPageIndex - 1;
            }
        }

		private void make5(object sender, RoutedEventArgs e)
        {
                MyPipsPager.MaxVisiblePips = 5;
        }

		private void make7(object sender, RoutedEventArgs e)
		{
			MyPipsPager.MaxVisiblePips = 7;
		}
	}
}
