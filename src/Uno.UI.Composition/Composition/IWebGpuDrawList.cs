#nullable enable

using System.Numerics;
using Color = global::Windows.UI.Color;

namespace Microsoft.UI.Composition;

/// <summary>
/// EXPERIMENTAL. Abstract sink for the WebGPU render path. The Visual tree traversal
/// (<see cref="Visual.RenderRootVisualWebGpu"/>) emits backend-agnostic 2D draw commands
/// here; a WebGPU backend (in a separate assembly) consumes them. This keeps Silk.NET /
/// GPU dependencies out of Uno.UI.Composition.
/// </summary>
internal interface IWebGpuDrawList
{
	/// <summary>
	/// A solid-color rectangle spanning local (0,0)-(size), placed by <paramref name="totalMatrix"/>
	/// (root→device), with cumulative <paramref name="opacity"/>, scissored to the device-space
	/// rectangle <paramref name="clipRect"/> (left, top, right, bottom in pixels).
	/// </summary>
	void AddRect(Matrix4x4 totalMatrix, Vector2 size, Color color, float opacity, Vector4 clipRect);

	/// <summary>
	/// A linear-gradient rectangle spanning local (0,0)-(size). <paramref name="startLocal"/>/
	/// <paramref name="endLocal"/> are the gradient endpoints in local pixels; <paramref name="offsets"/>
	/// (0..1) and <paramref name="colors"/> are the stops. Same placement/opacity/clip contract as AddRect.
	/// </summary>
	void AddLinearGradient(Matrix4x4 totalMatrix, Vector2 size, Vector2 startLocal, Vector2 endLocal,
		float[] offsets, Color[] colors, float opacity, Vector4 clipRect, Vector4 radii = default, int tileMode = 0);

	/// <summary>
	/// A radial-gradient rectangle spanning local (0,0)-(size). <paramref name="centerLocal"/> and
	/// <paramref name="radiusLocal"/> are the gradient ellipse's center and (x,y) radii in local pixels;
	/// <paramref name="originLocal"/> is the focal origin (t=0 there, t=1 at the ellipse edge). Same
	/// stops/placement/opacity/clip/rounded-mask contract as <see cref="AddLinearGradient"/>.
	/// </summary>
	void AddRadialGradient(Matrix4x4 totalMatrix, Vector2 size, Vector2 centerLocal, Vector2 radiusLocal, Vector2 originLocal,
		float[] offsets, Color[] colors, float opacity, Vector4 clipRect, Vector4 radii = default, int tileMode = 0);

	/// <summary>
	/// A solid rounded rectangle: local rect (<paramref name="localOffset"/>, <paramref name="size"/>),
	/// per-corner radii <paramref name="radii"/> as (TopLeft, TopRight, BottomRight, BottomLeft), placed by
	/// <paramref name="totalMatrix"/>. Same color/opacity/clip contract as AddRect.
	/// </summary>
	void AddRoundedRect(Matrix4x4 totalMatrix, Vector2 localOffset, Vector2 size, Vector4 radii, Color color, float opacity, Vector4 clipRect);

	/// <summary>
	/// A solid-filled arbitrary path: <paramref name="contours"/> are closed polylines in local
	/// coordinates (Béziers already flattened by the caller via Skia geometry — no Skia drawing),
	/// filled even-odd by WebGPU stencil-then-cover. Placed by <paramref name="totalMatrix"/>.
	/// </summary>
	/// <paramref name="localOffset"/> is added to each contour point in local space before <paramref name="totalMatrix"/>
	/// (e.g. a glyph's pen position). Lets all of a visual's glyphs share one matrix so the arena path can bake the
	/// offset into local vertices; equivalent to <c>Translate(localOffset) * totalMatrix</c> otherwise.
	void AddPath(Matrix4x4 totalMatrix, Vector2[][] contours, Color color, float opacity, Vector4 clipRect, Vector2 localOffset = default);

	/// <summary>
	/// An acrylic backdrop over the local rect (<paramref name="localOffset"/>, <paramref name="size"/>),
	/// placed by <paramref name="totalMatrix"/>. The content already rendered behind this region is
	/// gaussian-blurred (<paramref name="blurSigma"/>), luminosity-blended with <paramref name="luminosityColor"/>
	/// and color-blended with <paramref name="tintColor"/> (WinUI acrylic recipe), modulated by
	/// <paramref name="opacity"/>. When <paramref name="isOpaque"/> the backdrop is skipped and the tint is
	/// drawn solid. <paramref name="radii"/> rounds the corners (TopLeft, TopRight, BottomRight, BottomLeft).
	/// </summary>
	void AddAcrylic(Matrix4x4 totalMatrix, Vector2 localOffset, Vector2 size, Vector4 radii,
		Color tintColor, Color luminosityColor, float blurSigma, float noiseOpacity, bool isOpaque, float opacity, Vector4 clipRect);

	/// <summary>
	/// Acrylic backdrop masked by an arbitrary path (<paramref name="contours"/>, same flattened form as
	/// <see cref="AddPath"/>): the content behind is gaussian-blurred and composited only inside the path
	/// (via stencil-then-cover), then the tint fills the path on top. Same recipe as <see cref="AddAcrylic"/>.
	/// The path-coverage + blur primitive this uses is also what an arbitrary-shape drop shadow would need.
	/// </summary>
	void AddAcrylicPath(Matrix4x4 totalMatrix, Vector2[][] contours,
		Color tintColor, Color luminosityColor, float blurSigma, float noiseOpacity, bool isOpaque, float opacity, Vector4 clipRect);

	/// <summary>
	/// Begins a drop-shadow group: the commands emitted until the matching <see cref="EndShadow"/> form the
	/// shadow-casting subtree. At render time that subtree is drawn to an offscreen texture and its actual
	/// alpha (real spatially-varying coverage — images, gradients, AA, per-visual opacity, any number of
	/// visuals) is gaussian-blurred (<paramref name="blurSigma"/>), offset by (<paramref name="dx"/>,
	/// <paramref name="dy"/>) device px, and composited as <paramref name="shadowColor"/>×coverage behind
	/// that same content. Returns a handle to pass to <see cref="EndShadow"/>.
	/// </summary>
	int BeginShadow(Color shadowColor, float dx, float dy, float blurSigma, Vector4 clipRect);

	/// <summary>Closes the shadow group opened by <see cref="BeginShadow"/> (<paramref name="handle"/>).</summary>
	void EndShadow(int handle);

	/// <summary>
	/// Begins an opacity group: the commands until the matching <see cref="EndLayer"/> are rendered to an
	/// offscreen layer and composited ONCE at <paramref name="opacity"/> (scissored to <paramref name="clipRect"/>),
	/// so overlapping content in a translucent subtree blends once rather than per-primitive (no double-blend).
	/// Returns a handle to pass to <see cref="EndLayer"/>.
	/// </summary>
	int BeginLayer(float opacity, Vector4 clipRect);

	/// <summary>Closes the opacity group opened by <see cref="BeginLayer"/> (<paramref name="handle"/>).</summary>
	void EndLayer(int handle);

	/// <summary>
	/// Begins a mask group (CompositionMaskBrush): the commands until <see cref="MaskSeparator"/> are the SOURCE,
	/// the commands from there until <see cref="EndMask"/> are the MASK. The source is composited masked by the
	/// mask's alpha (source × mask.alpha). Returns a handle to pass to the other two calls.
	/// </summary>
	int BeginMask(Vector4 clipRect);

	/// <summary>Marks the end of the SOURCE group and start of the MASK group for <paramref name="handle"/>.</summary>
	void MaskSeparator(int handle);

	/// <summary>Closes the mask group opened by <see cref="BeginMask"/> (<paramref name="handle"/>).</summary>
	void EndMask(int handle);

	/// <summary>
	/// A textured image over the local rect (<paramref name="localOffset"/>, <paramref name="size"/>), placed by
	/// <paramref name="totalMatrix"/>. <paramref name="rgbaPixels"/> is unpremultiplied RGBA8 of size
	/// <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/>, sampled linearly across the rect and
	/// composited with <paramref name="opacity"/>.
	/// </summary>
	/// <param name="imageKey">Stable identity of the SOURCE image (0 = none). The backend caches the uploaded
	/// GPU texture by this key and reuses it across frames, so a static image isn't re-uploaded every frame.</param>
	void AddImage(Matrix4x4 totalMatrix, Vector2 localOffset, Vector2 size, byte[] rgbaPixels, int pixelWidth, int pixelHeight, float opacity, Vector4 clipRect, float[]? colorMatrix = null, long imageKey = 0);

	/// <summary>
	/// Pushes a rounded-rectangle clip: subsequent draws (until the matching <see cref="PopClip"/>) are clipped
	/// to the rounded shape, intersected with any enclosing clips. <paramref name="deviceRect"/> is (left, top,
	/// right, bottom) in device px and <paramref name="radii"/> the per-corner radii (TopLeft, TopRight,
	/// BottomRight, BottomLeft) in device px. Realized via a depth-buffer mask (coexists with stencil fills).
	/// </summary>
	void PushClip(Vector4 deviceRect, Vector4 radii);

	/// <summary>
	/// Pushes an inverse rounded-rect clip: clips OUT the inside of the rounded rect (keeps only the area
	/// outside it). Used to render a ring/border stroke — fill the outer rect, exclude the inner.
	/// </summary>
	void PushClipExclude(Vector4 deviceRect, Vector4 radii);

	/// <summary>
	/// Pushes an arbitrary-path clip (even-odd, same flattened contour form as <see cref="AddPath"/>): subsequent
	/// draws are clipped to the path's interior, intersected with enclosing clips. Realized via the depth mask.
	/// </summary>
	void PushClipPath(Matrix4x4 totalMatrix, Vector2[][] contours);

	/// <summary>Pops the most recent <see cref="PushClip"/>/<see cref="PushClipPath"/>.</summary>
	void PopClip();

	/// <summary>
	/// Per-visual geometry cache (mirrors Skia's <c>_picture</c>). If <paramref name="cache"/> (the object
	/// returned by a prior <see cref="EndCachedVisualRecord"/> and stored on the visual) can be replayed under
	/// the current <paramref name="totalMatrix"/>/<paramref name="opacity"/>, it is replayed here and this returns
	/// true — the caller then SKIPS painting. Otherwise it returns false and recording begins; the caller paints
	/// as usual and must call <see cref="EndCachedVisualRecord"/>. Only a pure-translation matrix change (scroll)
	/// is replayable; anything else re-records. <paramref name="clipRect"/> is the visual's device-space clip.
	/// </summary>
	bool BeginCachedVisualReplay(object? cache, Matrix4x4 totalMatrix, float opacity, Vector4 clipRect);

	/// <summary>Finalizes the recording started by <see cref="BeginCachedVisualReplay"/> and returns a cache
	/// object to store on the visual (or null if its paint isn't cacheable). Call only when Begin returned false.</summary>
	object? EndCachedVisualRecord(Matrix4x4 totalMatrix, float opacity);

	/// <summary>Marks the start of a visual's own-paint geometry so the slab allocator (UNO_WEBGPU_SLAB) can give
	/// it a stable per-visual slice. <paramref name="visualId"/> must be stable for the lifetime of the visual.
	/// No-op unless the slab is enabled.</summary>
	void BeginVisual(long visualId);

	/// <summary>Closes the bracket opened by <see cref="BeginVisual"/>.</summary>
	void EndVisual();

	/// <summary>Called once on the UI thread after the whole tree is walked. Runs the slab placement post-pass
	/// (relocate each visual's geometry to its stable slice and patch its draw commands). No-op unless enabled.</summary>
	void FinalizeBuild();
}
