﻿#nullable enable

using Uno.UI.Xaml.Controls;
using Uno.UI.Xaml.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUICoreServices = global::Uno.UI.Xaml.Core.CoreServices;

namespace Uno.UI.Xaml.Islands;

internal partial class XamlIsland : Panel
{
	private readonly ContentManager _contentManager;

	public XamlIsland()
	{
		_contentManager = new(this, false);

		//Uno specific - flag as VisualTreeRoot for interop with existing logic
		IsVisualTreeRoot = true;

		InitializeRoot(WinUICoreServices.Instance);
	}

	internal ContentManager ContentManager => _contentManager;

	public UIElement? Content
	{
		get => _contentManager.Content;
		set
		{
			_contentManager.Content = value;

			SetPublicRootVisual(value, null, null);
		}
	}

	internal Window? OwnerWindow { get; set; }

	internal bool IsSiteVisible { get; set; }
}
