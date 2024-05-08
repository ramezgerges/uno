﻿#nullable enable

using System;
using Uno.UI.Xaml.Core;
using Uno.UI.Xaml.Islands;
using Windows.Foundation;
using Windows.Graphics.Display;
using Uno.UI.Extensions;
using Windows.UI.Composition;
using Uno.Disposables;
using Uno.Foundation.Extensibility;
using Uno.UI.Hosting;
using Uno.UI.Xaml.Controls;

namespace Microsoft.UI.Xaml;

/// <summary>
/// Represents a tree of XAML content and information about the context in which it is hosted.
/// </summary>
/// <remarks>
/// Effectively a public API wrapper around VisualTree.
/// </remarks>
public sealed partial class XamlRoot
{
	internal XamlRoot(VisualTree visualTree)
	{
		VisualTree = visualTree;
	}

	/// <summary>
	/// Occurs when a property of XamlRoot has changed.
	/// </summary>
	public event TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? Changed;

	/// <summary>
	/// Gets the root element of the XAML element tree.
	/// </summary>
	public UIElement? Content
	{
		get
		{
			var publicRoot = VisualTree.PublicRootVisual;
			if (publicRoot is WindowChrome chrome)
			{
				return chrome.Content as UIElement;
			}

			return publicRoot;
		}
	}

	internal Rect Bounds
	{
		get
		{
			var rootElement = VisualTree.RootElement;
			if (rootElement is RootVisual rootVisual)
			{
				if (Window.CurrentSafe is null)
				{
					throw new InvalidOperationException("Window.Current must be set.");
				}

				return Window.CurrentSafe.Bounds;
			}
			else if (rootElement is XamlIsland xamlIsland)
			{
				var width = !double.IsNaN(xamlIsland.Width) ? xamlIsland.Width : 0;
				var height = !double.IsNaN(xamlIsland.Height) ? xamlIsland.Height : 0;
				return new Rect(0, 0, width, height);
			}

			return default;
		}
	}

	internal Microsoft.UI.Composition.Compositor Compositor => Microsoft.UI.Composition.Compositor.GetSharedCompositor();

#if !HAS_UNO_WINUI // This is a UWP-only property
	/// <summary>
	/// Gets the context identifier for the view.
	/// </summary>
	public UIContext UIContext { get; } = new UIContext();
#endif

	internal Window? HostWindow => VisualTree.ContentRoot.GetOwnerWindow();

	internal static DisplayInformation GetDisplayInformation(XamlRoot? root)
		=> root?.HostWindow?.AppWindow.Id is { } id ? DisplayInformation.GetOrCreateForWindowId(id) : DisplayInformation.GetForCurrentViewSafe();

	internal static void SetForElement(DependencyObject element, XamlRoot? currentRoot, XamlRoot? newRoot)
	{
		if (currentRoot == newRoot)
		{
			return;
		}

		if (currentRoot is not null)
		{
			throw new InvalidOperationException("Cannot change XamlRoot for existing element");
		}

		if (newRoot is not null)
		{
			element.SetVisualTree(newRoot.VisualTree);
		}
	}

	internal IDisposable OpenPopup(Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
	{
		if (VisualTree.PopupRoot == null)
		{
			throw new InvalidOperationException("PopupRoot is not initialized yet.");
		}

		return VisualTree.PopupRoot.OpenPopup(popup);
	}

	public object? GetGL()
	{
		if (ApiExtensibility.CreateInstance<XamlRootMap<IXamlRootHost>>(this, out var map) && map.GetHostForRoot(this) is { } host)
		{
			return host.GetGL();
		}

		return null;
	}

	public IDisposable LockGL()
	{
		if (ApiExtensibility.CreateInstance<XamlRootMap<IXamlRootHost>>(this, out var map) && map.GetHostForRoot(this) is { } host)
		{
			return host.LockGL();
		}

		return Disposable.Empty;
	}
}
