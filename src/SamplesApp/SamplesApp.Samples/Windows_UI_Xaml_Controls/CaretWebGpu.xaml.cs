using Uno.UI.Samples.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UITests.Shared.Windows_UI_Xaml_Controls.TextBoxTests
{
	[Sample("TextBox", Name = "CaretWebGpu", IsManualTest = true, IgnoreInSnapshotTests = true)]
	public sealed partial class CaretWebGpu : UserControl
	{
		public CaretWebGpu()
		{
			this.InitializeComponent();
			this.Loaded += (s, e) => { box.Focus(FocusState.Programmatic); box.Select(5, 0); };
		}
	}
}
