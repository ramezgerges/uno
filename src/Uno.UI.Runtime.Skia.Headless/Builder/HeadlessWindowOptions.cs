#nullable enable

namespace Uno.UI.Runtime.Skia;

/// <summary>
/// Per-window configuration returned by <see cref="HeadlessHostBuilder.ConfigureWindow"/>. Carries the
/// rasterization scale (which has no public WinUI equivalent). The window size is not set here — it
/// defaults to <see cref="HeadlessHostBuilder.WithSize"/> and can be changed at runtime via the standard
/// <c>AppWindow.Resize</c>.
/// </summary>
public sealed class HeadlessWindowOptions
{
	/// <summary>
	/// The rasterization scale (a.k.a. <c>RawPixelsPerViewPixel</c>). Logical bounds are
	/// <c>size / scale</c>. Defaults to <c>1.0</c>.
	/// </summary>
	public float Scale { get; init; } = 1f;

	/// <summary>
	/// Renders this window on demand. When null the window renders to a null surface and produces no
	/// pixels.
	/// </summary>
	public HeadlessFrameSource? Frames { get; init; }

	/// <summary>
	/// Receives external pointer and keyboard input for this window. When null the window has no
	/// input sources and nothing can be injected into it.
	/// </summary>
	public HeadlessInput? Input { get; init; }
}
