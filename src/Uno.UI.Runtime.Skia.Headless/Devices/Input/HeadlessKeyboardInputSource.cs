#nullable enable

using Windows.Foundation;
using Windows.UI.Core;

namespace Uno.UI.Runtime.Skia.Headless;

/// <summary>
/// Raises keyboard events for a headless window. Pressed-key and modifier tracking lives in
/// <see cref="HeadlessInput"/>, which drives this.
/// </summary>
internal sealed class HeadlessKeyboardInputSource : IUnoKeyboardInputSource
{
	public event TypedEventHandler<object, KeyEventArgs>? KeyDown;
	public event TypedEventHandler<object, KeyEventArgs>? KeyUp;
	public event TypedEventHandler<object, CharacterReceivedEventArgs>? CharacterReceived;

	internal void RaiseKeyDown(KeyEventArgs args) => KeyDown?.Invoke(this, args);

	internal void RaiseKeyUp(KeyEventArgs args) => KeyUp?.Invoke(this, args);

	internal void RaiseCharacterReceived(CharacterReceivedEventArgs args) => CharacterReceived?.Invoke(this, args);
}
