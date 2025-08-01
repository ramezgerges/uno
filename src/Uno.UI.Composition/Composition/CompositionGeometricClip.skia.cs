#nullable enable

using System;
using SkiaSharp;
using Windows.ApplicationModel.Contacts;
using Windows.Foundation;

namespace Microsoft.UI.Composition;

partial class CompositionGeometricClip
{
	private protected override bool GetBoundsCore(Visual visual, out Rect bounds)
	{
		if (Geometry is not null)
		{
			var geometry = Geometry.BuildGeometry();

			if (geometry is SkiaGeometrySource2D skiaGeometrySource)
			{
				bounds = skiaGeometrySource.Geometry.TightBounds.ToRect();
				return true;
			}
			else
			{
				throw new InvalidOperationException($"Clipping with source {geometry} is not supported");
			}
		}

		bounds = Rect.Empty;
		return false;
	}

	internal override SKPath? GetClipPath(Visual visual)
	{
		if (Geometry is not null)
		{
			var geometry = Geometry.BuildGeometry();

			if (geometry is SkiaGeometrySource2D geometrySource)
			{
				var path = geometrySource.Geometry;
				if (!TransformMatrix.IsIdentity)
				{
					var transformedPath = new SKPath();
					path.Transform(TransformMatrix.ToSKMatrix(), transformedPath);
					path = transformedPath;
				}

				return path;
			}
			else
			{
				throw new InvalidOperationException($"Clipping with source {geometry} is not supported");
			}
		}

		return null;
	}
}
