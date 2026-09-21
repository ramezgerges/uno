#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace Uno.UI.Runtime.Skia;

/// <summary>
/// Describes a frame rendered by a headless window. Pixels are always BGRA8888 with premultiplied
/// alpha, laid out top-down.
/// </summary>
public readonly struct HeadlessFrameInfo
{
	internal HeadlessFrameInfo(int width, int height, int stride)
	{
		Width = width;
		Height = height;
		Stride = stride;
		Generation = 0;
	}

	private HeadlessFrameInfo(int width, int height, int stride, long generation)
	{
		Width = width;
		Height = height;
		Stride = stride;
		Generation = generation;
	}

	internal HeadlessFrameInfo WithGeneration(long generation)
		=> new(Width, Height, Stride, generation);

	/// <summary>Frame width, in raw pixels.</summary>
	public int Width { get; }

	/// <summary>Frame height, in raw pixels.</summary>
	public int Height { get; }

	/// <summary>Number of bytes per row.</summary>
	public int Stride { get; }

	/// <summary>Minimum size, in bytes, of a buffer able to hold this frame.</summary>
	public int ByteCount => Stride * Height;

	/// <summary>
	/// The <see cref="HeadlessFrameSource.Generation"/> this frame reflects. Feed it back to
	/// <see cref="HeadlessFrameSource.WaitForRenderRequest"/> to wait for the next change.
	/// </summary>
	public long Generation { get; }
}

/// <summary>A rectangle of changed pixels within a frame, in raw pixels.</summary>
public readonly record struct HeadlessRect(int X, int Y, int Width, int Height);

/// <summary>The outcome of a <see cref="HeadlessFrameSource.TryRender"/> call.</summary>
public enum HeadlessRenderResult
{
	/// <summary>The frame was drawn into the supplied buffer.</summary>
	Rendered,

	/// <summary>The window has nothing to draw yet; try again after the next render request.</summary>
	NotReady,

	/// <summary>
	/// The buffer is too small. The reported frame describes the size needed, so the caller can grow
	/// its buffer and call again.
	/// </summary>
	BufferTooSmall,
}

/// <summary>
/// Renders a headless window on demand. Pass an instance to <see cref="HeadlessHostBuilder.WithFrames"/>
/// (or <see cref="HeadlessWindowOptions.Frames"/>).
/// </summary>
/// <remarks>
/// The contract runs both ways, the way a platform host works against a compositor. Uno signals that it
/// wants to be drawn (an invalidation, equivalent to asking for a vsync) by raising
/// <see cref="RenderRequested"/> and bumping <see cref="Generation"/>. The caller decides when a frame
/// actually happens, by calling <see cref="TryRender"/> — on its own thread, at its own cadence.
/// Coalescing several requests into one render is expected, and ignoring them entirely just means the
/// next render picks the content up later.
/// <para>
/// <see cref="WaitForRenderRequest"/> is the blocking form of the signal, for callers whose cadence is
/// "whenever there is something new" rather than a clock.
/// </para>
/// <para>
/// Uno redraws only the damaged region of a frame, so a buffer accumulates content across calls. Passing
/// the same buffer each time keeps that incremental fast path; passing a different one forces a full
/// repaint, since the new buffer holds none of the previous content.
/// </para>
/// </remarks>
public sealed class HeadlessFrameSource
{
	private readonly object _gate = new();

	private Func<Memory<byte>, ICollection<HeadlessRect>?, (HeadlessRenderResult result, HeadlessFrameInfo frame)>? _render;
	private long _generation;

	/// <summary>
	/// Raised when the app has invalidated and wants to be drawn. Raised on the UI thread, so handlers
	/// should signal rather than render inline.
	/// </summary>
	public event EventHandler? RenderRequested;

	/// <summary>
	/// Incremented every time the app asks to be drawn. A caller that records the value it last rendered
	/// can tell whether there is anything new without racing the event.
	/// </summary>
	public long Generation
	{
		get
		{
			lock (_gate)
			{
				return _generation;
			}
		}
	}

	/// <summary>True once a window has picked this instance up, so rendering will produce frames.</summary>
	public bool IsConnected => _render is not null;

	internal void Connect(Func<Memory<byte>, ICollection<HeadlessRect>?, (HeadlessRenderResult result, HeadlessFrameInfo frame)> render)
		=> _render = render;

	internal void RaiseRenderRequested()
	{
		lock (_gate)
		{
			_generation++;
			Monitor.PulseAll(_gate);
		}

		RenderRequested?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Blocks until the app has asked to be drawn since <paramref name="seenGeneration"/>, which is
	/// updated to the generation that was reached. Returns false if the timeout elapsed first.
	/// </summary>
	public bool WaitForRenderRequest(ref long seenGeneration, TimeSpan timeout)
	{
		lock (_gate)
		{
			if (_generation == seenGeneration && !Monitor.Wait(_gate, timeout))
			{
				return false;
			}

			seenGeneration = _generation;
			return true;
		}
	}

	/// <summary>
	/// Draws the window's current content into <paramref name="buffer"/>. Safe to call from any thread,
	/// though calls are serialised against each other.
	/// </summary>
	public HeadlessRenderResult TryRender(Memory<byte> buffer, out HeadlessFrameInfo frame)
		=> TryRender(buffer, damage: null, out frame);

	/// <summary>
	/// Draws the window's current content into <paramref name="buffer"/>, reporting the regions that
	/// changed into <paramref name="damage"/>, which is cleared first. A caller that reuses one list
	/// across frames does not allocate. Passing the same buffer each time keeps the damage small;
	/// passing a different one reports the whole frame, since that buffer holds none of the last one.
	/// Safe to call from any thread, though calls are serialised against each other.
	/// </summary>
	public HeadlessRenderResult TryRender(Memory<byte> buffer, ICollection<HeadlessRect>? damage, out HeadlessFrameInfo frame)
	{
		if (_render is not { } render)
		{
			frame = default;
			return HeadlessRenderResult.NotReady;
		}

		damage?.Clear();

		long generation;

		lock (_gate)
		{
			generation = _generation;
		}

		var (result, rendered) = render(buffer, damage);

		// Stamped with the generation captured before drawing: anything raised during the render is not
		// guaranteed to be in this frame, so claiming it would lose an update.
		frame = rendered.WithGeneration(generation);
		return result;
	}
}
