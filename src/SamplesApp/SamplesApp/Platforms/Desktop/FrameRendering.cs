#nullable enable

using System;
using System.Collections.Generic;
using Uno.UI.Runtime.Skia;

namespace SamplesApp;

internal static class FrameRendering
{
	/// <summary>
	/// Renders the window into a caller-owned buffer, growing it when the frame needs more room. When
	/// <paramref name="waitForNew"/> is set, waits for Uno to ask to be drawn since
	/// <paramref name="seenGeneration"/> before rendering.
	/// </summary>
	public static bool TryRenderInto(
		this HeadlessFrameSource frames,
		ref byte[] buffer,
		ref long seenGeneration,
		bool waitForNew,
		ICollection<HeadlessRect>? damage,
		out HeadlessFrameInfo frame)
	{
		if (waitForNew && !frames.WaitForRenderRequest(ref seenGeneration, TimeSpan.FromSeconds(20)))
		{
			frame = default;
			return false;
		}

		// A frame may need a bigger buffer than the caller has; grow once and retry.
		for (var attempt = 0; attempt < 2; attempt++)
		{
			switch (frames.TryRender(buffer, damage, out frame))
			{
				case HeadlessRenderResult.Rendered:
					seenGeneration = frame.Generation;
					return true;

				case HeadlessRenderResult.BufferTooSmall:
					buffer = new byte[frame.ByteCount];
					continue;

				default:
					return false;
			}
		}

		frame = default;
		return false;
	}
}
