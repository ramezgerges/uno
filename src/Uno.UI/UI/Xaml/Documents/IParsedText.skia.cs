using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Documents;

internal interface IParsedText
{
	void Draw(in Visual.PaintingSession session,
		(int index, CompositionBrush brush, float thickness)? caret, // null to skip drawing a caret
		IEnumerable<TextHighlighter> highlighters,
		(int startIndex, int length)? compositionRange
	);

	// EXPERIMENTAL WebGPU path: emit glyph outlines (filled via stencil-then-cover) into the draw list, plus the
	// highlighter backgrounds + per-range foreground override (mirrors Draw's highlighter handling).
	void DrawWebGpu(IWebGpuDrawList draw, global::System.Numerics.Matrix4x4 matrix, global::System.Numerics.Vector4 clip, float opacity, IEnumerable<TextHighlighter> highlighters, (int index, CompositionBrush brush, float thickness)? caret);

	Rect GetRectForIndex(int adjustedIndex);

	int GetIndexAt(Point p, bool ignoreEndingNewLine, bool extendedSelection);

	Hyperlink GetHyperlinkAt(Point point);

	/// <param name="right">when on a word boundary, decides whether to return the left or the right word</param>
	(int start, int length) GetWordAt(int index, bool right);

	internal (int start, int length, bool firstLine, bool lastLine, int lineIndex) GetLineAt(int index);

	bool IsBaseDirectionRightToLeft { get; }
}
