#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;
using Uno.Foundation.Logging;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace Uno.UI.Runtime.Skia.Headless;

/// <summary>
/// Drives the Skia two-phase render cycle for a single headless window, so the app lifecycle,
/// composition animations and <c>RenderTargetBitmap</c> behave like a real target.
/// </summary>
/// <remarks>
/// Without a <see cref="HeadlessFrameSource"/> the window produces no pixel output: a dedicated thread
/// ticks the cycle against a null surface, keeping scheduling and animations alive while the paint walk
/// is skipped globally.
/// <para>
/// With one, rendering is pull-based and there is no thread of its own: an invalidation raises
/// <see cref="HeadlessFrameSource.RenderRequested"/>, and the frame is only drawn when the caller asks,
/// on the caller's thread. That mirrors a platform host drawing in response to a vsync callback.
/// </para>
/// </remarks>
internal sealed class HeadlessRenderer : IDisposable
{
	private readonly IXamlRootHost _host;
	private readonly HeadlessFrameSource? _frameSource;
	private readonly object _renderGate = new();

	private readonly AutoResetEvent? _tickEvent;
	private readonly Thread? _tickThread;
	private volatile bool _disposed;

	private SKSurface? _surface;
	private byte[] _internalBuffer = Array.Empty<byte>();
	private MemoryHandle _bufferHandle;
	private HeadlessFrameInfo _frameInfo;
	private Memory<byte> _lastCopyTarget;
	private readonly List<HeadlessRect> _damage = new();
	private readonly SKRegion _damageRegion = new();

	public HeadlessRenderer(IXamlRootHost host, HeadlessFrameSource? frameSource)
	{
		_host = host;
		_frameSource = frameSource;

		if (frameSource is not null)
		{
			frameSource.Connect(TryRender);
			return;
		}

		// No frame source: keep the legacy behaviour of ticking the cycle against a null surface.
		_tickEvent = new AutoResetEvent(false);
		_tickThread = new Thread(_ =>
		{
			while (!_disposed)
			{
				try
				{
					_tickEvent.WaitOne();
					if (_disposed)
					{
						break;
					}

					TickNullSurface();
				}
				catch (Exception ex)
				{
					this.LogError()?.Error("Error during headless rendering", ex);
				}
			}
		})
		{
			IsBackground = true,
			Name = "Headless rendering thread"
		};
		_tickThread.Start();
	}

	/// <summary>
	/// Signals that the window wants to be drawn. With a frame source this only raises
	/// <see cref="HeadlessFrameSource.RenderRequested"/>; the caller decides whether and when to render.
	/// </summary>
	public void Invalidate()
	{
		if (_disposed)
		{
			return;
		}

		if (_frameSource is { } frameSource)
		{
			frameSource.RaiseRenderRequested();
			return;
		}

		_tickEvent?.Set();
	}

	private void TickNullSurface()
	{
		if (_host.RootElement?.Visual.CompositionTarget is not CompositionTarget ct)
		{
			return;
		}

		ct.OnNativePlatformFrameRequested(_surface?.Canvas, size =>
		{
			_surface?.Dispose();
			_surface = SKSurface.CreateNull((int)size.Width, (int)size.Height);
			_frameInfo = new HeadlessFrameInfo((int)size.Width, (int)size.Height, (int)size.Width * 4);
			return _surface.Canvas;
		});

		_surface?.Flush();
	}

	private (HeadlessRenderResult result, HeadlessFrameInfo frame) TryRender(Memory<byte> buffer, ICollection<HeadlessRect>? damage)
	{
		lock (_renderGate)
		{
			if (_disposed || _host.RootElement?.Visual.CompositionTarget is not CompositionTarget ct)
			{
				return (HeadlessRenderResult.NotReady, default);
			}

			// Rendering always targets the same internal surface, so Uno's damage-clipped incremental
			// path stays valid no matter which buffer the caller hands in. The frame is copied out
			// afterwards, which is what makes buffer swapping free from the caller's point of view.
			ct.OnNativePlatformFrameRequested(_surface?.Canvas, size =>
			{
				BindInternalSurface(new HeadlessFrameInfo((int)size.Width, (int)size.Height, (int)size.Width * 4));
				return _surface!.Canvas;
			});

			if (_surface is null)
			{
				return (HeadlessRenderResult.NotReady, default);
			}

			_surface.Flush();

			if (buffer.Length < _frameInfo.ByteCount)
			{
				return (HeadlessRenderResult.BufferTooSmall, _frameInfo);
			}

			// A caller that swaps buffers gets the whole frame, since the buffer it handed in holds none
			// of the previous one. Reusing a buffer only costs the damaged rows.
			var reusedBuffer = buffer.Equals(_lastCopyTarget);
			CollectDamage(reusedBuffer ? ct.LastPresentedDamage : null);
			CopyOut(buffer);
			_lastCopyTarget = buffer;

			damage?.Clear();

			if (damage is not null)
			{
				foreach (var rect in _damage)
				{
					damage.Add(rect);
				}
			}

			return (HeadlessRenderResult.Rendered, _frameInfo);
		}
	}

	/// <summary>
	/// Turns the presented damage path into whole-pixel rectangles. A region decomposes the path into
	/// disjoint spans, so two separate changes do not drag along everything between them the way a single
	/// bounding box would. A null path means the whole frame was repainted.
	/// </summary>
	private void CollectDamage(SKPath? presentedDamage)
	{
		_damage.Clear();

		if (presentedDamage is null)
		{
			_damage.Add(new HeadlessRect(0, 0, _frameInfo.Width, _frameInfo.Height));
			return;
		}

		// Reused across frames: a fresh SKRegion is a native allocation on every single frame.
		_damageRegion.SetRect(new SKRectI(0, 0, _frameInfo.Width, _frameInfo.Height));

		if (!_damageRegion.SetPath(presentedDamage, _damageRegion))
		{
			return;
		}

		using var iterator = _damageRegion.CreateRectIterator();

		while (iterator.Next(out var rect))
		{
			if (rect.Width > 0 && rect.Height > 0)
			{
				_damage.Add(new HeadlessRect(rect.Left, rect.Top, rect.Width, rect.Height));
			}
		}
	}

	/// <summary>Copies the internal frame into the caller's buffer, limited to the damaged rows.</summary>
	private void CopyOut(Memory<byte> buffer)
	{
		var source = _internalBuffer.AsSpan(0, _frameInfo.ByteCount);
		var target = buffer.Span;

		foreach (var rect in _damage)
		{
			var byteOffset = rect.X * 4;
			var byteCount = rect.Width * 4;

			for (var y = rect.Y; y < rect.Y + rect.Height; y++)
			{
				var start = y * _frameInfo.Stride + byteOffset;
				source.Slice(start, byteCount).CopyTo(target.Slice(start, byteCount));
			}
		}
	}

	/// <summary>Points the render surface at the internal buffer, growing it when the frame size changes.</summary>
	private void BindInternalSurface(HeadlessFrameInfo info)
	{
		ReleaseSurface();

		if (_internalBuffer.Length < info.ByteCount)
		{
			_internalBuffer = new byte[info.ByteCount];
		}

		_bufferHandle = _internalBuffer.AsMemory().Pin();

		unsafe
		{
			_surface = SKSurface.Create(
				new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
				(IntPtr)_bufferHandle.Pointer,
				info.Stride);
		}

		_frameInfo = info;

		// The surface was recreated, so every caller buffer is now stale.
		_lastCopyTarget = default;
	}

	private void ReleaseSurface()
	{
		_surface?.Dispose();
		_surface = null;
		_bufferHandle.Dispose();
		_bufferHandle = default;
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;

		if (_tickThread is null)
		{
			lock (_renderGate)
			{
				ReleaseSurface();
			}

			return;
		}

		// Wake the tick thread so it can observe _disposed and exit before shared resources go away.
		_tickEvent!.Set();

		if (_tickThread.Join(TimeSpan.FromSeconds(1)))
		{
			ReleaseSurface();
			_tickEvent.Dispose();
		}
		else
		{
			// The thread may still be mid-render; leak its surface/event rather than risk a use-after-dispose race.
			this.LogWarn()?.Warn("The headless rendering thread did not stop within the timeout; its surface and event are left undisposed to avoid a race.");
		}
	}
}
