#nullable enable

using SkiaSharp;
using Uno.UI.Composition;

namespace Microsoft.UI.Composition
{
	public partial class RedirectVisual : ContainerVisual
	{
		internal override void Paint(in PaintingSession session)
		{
			base.Paint(in session);

			if (Source is not null && session.Canvas is { } canvas)
			{
				Source.RenderRootVisual(canvas, null);
			}
		}

		// WebGPU mirror: Skia paints children (base.Paint) first, then the redirected Source on top. The Source is
		// therefore emitted AFTER children, via PaintOverChildrenWebGpu, to preserve that z-order.
		internal override void PaintOverChildrenWebGpu(IWebGpuDrawList draw, SKRect clipInRoot, float opacity)
		{
			Source?.RenderWebGpu(draw, clipInRoot, opacity);
		}

		internal override bool CanPaint() => Source?.CanPaint() ?? false;
		internal override bool RequiresRepaintOnEveryFrame => true;
	}
}
