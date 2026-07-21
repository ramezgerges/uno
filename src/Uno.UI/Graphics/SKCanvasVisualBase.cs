#nullable enable

using System;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace Uno.UI.Graphics;

internal abstract class SKCanvasVisualBase : ContainerVisual
{
	/// <param name="renderCallback">The first parameter of the action must be an SkiaSharp.SKCanvas instance.</param>
	protected SKCanvasVisualBase(Action<object, Size> renderCallback, Compositor compositor) : base(compositor)
	{
		RenderCallback = renderCallback;
	}

	protected Action<object, Size> RenderCallback { get; }

	/// <summary>
	/// Resolves the owning element's <see cref="XamlRoot"/> lazily. The WebGPU paint path needs it to obtain a
	/// native GL context (for hardware-accelerated Skia) — Composition visuals don't otherwise hold a XamlRoot.
	/// </summary>
	internal Func<XamlRoot?>? XamlRootProvider { get; set; }

	public abstract void Invalidate();
}
