#nullable enable

using System;
using System.Numerics;
using Uno.UI.Composition;
using Uno.UI.Composition.Drawing;

namespace Microsoft.UI.Composition;

public partial class Visual
{
	private interface IPrivateSessionFactory
	{
		void CreateInstance(Visual visual, IDrawingSession drawingSession, ref Matrix4x4 rootTransform, float opacity, DamageRegion? damage, ref Matrix4x4 relativeMatrix, out PaintingSession session);
	}

	/// <summary>
	/// Represents the "context" in which a visual draws.
	/// </summary>
	internal readonly ref struct PaintingSession
	{
		// This dance is done to make it so that only Visual can create a PaintingSession
		public readonly struct SessionFactory : IPrivateSessionFactory
		{
			void IPrivateSessionFactory.CreateInstance(Visual visual, IDrawingSession drawingSession, ref Matrix4x4 rootTransform, float opacity, DamageRegion? damage, ref Matrix4x4 relativeMatrix, out PaintingSession session)
			{
				session = new PaintingSession(visual, drawingSession, ref rootTransform, opacity, damage, ref relativeMatrix);
			}
		}

		private PaintingSession(Visual visual, IDrawingSession drawingSession, ref Matrix4x4 rootTransform, float opacity, DamageRegion? damage, ref Matrix4x4 relativeMatrix)
		{
			Session = drawingSession;
			RootTransform = ref rootTransform;
			Opacity = opacity;
			Damage = damage;
			RelativeMatrix = ref relativeMatrix;

			_saveCount = Session.Save();
		}

		public void Dispose() => Session.RestoreToCount(_saveCount);

		/// <summary>The backend-neutral drawing surface for this session.</summary>
		public readonly IDrawingSession Session;

		/// <summary>The transform matrix to the root visual of this drawing session (which isn't necessarily the identity matrix due to scaling (DPI) and/or RenderTargetBitmap.</summary>
		public readonly ref Matrix4x4 RootTransform;

		/// <summary>When set, this session's own visual draws under this matrix - its transform relative to the recording
		/// origin - then <see cref="RootTransform"/>, instead of under its TotalMatrix. Used where the origin's TotalMatrix
		/// has no inverse to take back out (it flattens Z). Each visual composes its own from its parent's with one
		/// multiply, so no inverse and no walk up the tree are needed.</summary>
		public readonly ref Matrix4x4 RelativeMatrix;

		public readonly float Opacity;

		/// <summary>The per-frame damage (dirty-region) accumulator threaded through the render walk, or null when
		/// damage tracking is disabled for this pass (e.g. offscreen surfaces / RenderTargetBitmap that repaint fully).</summary>
		public readonly DamageRegion? Damage;

		private readonly int _saveCount;
	}
}
