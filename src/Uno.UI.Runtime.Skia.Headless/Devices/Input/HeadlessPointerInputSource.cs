#nullable enable

using System.Runtime.CompilerServices;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.UI.Core;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia.Headless;

/// <summary>
/// Raises pointer events for a headless window. It holds no state of its own beyond the cursor position:
/// button and modifier tracking lives in <see cref="HeadlessInput"/>, which drives this.
/// </summary>
internal sealed class HeadlessPointerInputSource : IUnoCorePointerInputSource
{
#pragma warning disable CS0067 // Capture and enter/exit have no meaning without a real pointing device.
	public event TypedEventHandler<object, PointerEventArgs>? PointerCaptureLost;
	public event TypedEventHandler<object, PointerEventArgs>? PointerEntered;
	public event TypedEventHandler<object, PointerEventArgs>? PointerExited;
	public event TypedEventHandler<object, PointerEventArgs>? PointerMoved;
	public event TypedEventHandler<object, PointerEventArgs>? PointerPressed;
	public event TypedEventHandler<object, PointerEventArgs>? PointerReleased;
	public event TypedEventHandler<object, PointerEventArgs>? PointerWheelChanged;
	public event TypedEventHandler<object, PointerEventArgs>? PointerCancelled; // Uno only
#pragma warning restore CS0067

	public bool HasCapture => false;

	public CoreCursor PointerCursor { get; set; } = new(CoreCursorType.Arrow, 0);

	/// <summary>Last injected position, in logical pixels.</summary>
	public Point PointerPosition { get; internal set; }

	public void SetPointerCapture(PointerIdentifier pointer) => LogNotSupported();

	public void SetPointerCapture() => LogNotSupported();

	public void ReleasePointerCapture(PointerIdentifier pointer) => LogNotSupported();

	public void ReleasePointerCapture() => LogNotSupported();

	internal void RaiseMoved(PointerEventArgs args) => PointerMoved?.Invoke(this, args);

	internal void RaisePressed(PointerEventArgs args) => PointerPressed?.Invoke(this, args);

	internal void RaiseReleased(PointerEventArgs args) => PointerReleased?.Invoke(this, args);

	internal void RaiseWheelChanged(PointerEventArgs args) => PointerWheelChanged?.Invoke(this, args);

	internal void RaiseEntered(PointerEventArgs args) => PointerEntered?.Invoke(this, args);

	internal void RaiseExited(PointerEventArgs args) => PointerExited?.Invoke(this, args);

	internal void RaiseCancelled(PointerEventArgs args) => PointerCancelled?.Invoke(this, args);

	private void LogNotSupported([CallerMemberName] string member = "")
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"{member} is not supported on the headless host.");
		}
	}
}
