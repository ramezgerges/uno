using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Size = Windows.Foundation.Size;

using Silk.NET.OpenGL;
using Tutorial;
using Shader = Tutorial.Shader;

namespace UITests.Shared.Windows_UI_Composition
{
	public class GlCanvasElementImpl() : GLCanvasElement(new Size(1200, 800))
	{
		private static BufferObject<float> Vbo;
		private static BufferObject<uint> Ebo;
		private static VertexArrayObject<float, uint> Vao;
		private static Shader Shader;

		static float[] vertices = {
			// Front face
			0.5f,  0.5f,  0.5f, 1.0f, 0.4f, 0.6f,
			-0.5f,  0.5f,  0.5f, 1.0f, 0.9f, 0.2f,
			-0.5f, -0.5f,  0.5f, 0.7f, 0.3f, 0.8f,
			0.5f, -0.5f,  0.5f, 0.5f, 0.3f, 1.0f,

			// Back face
			0.5f,  0.5f, -0.5f, 0.2f, 0.6f, 1.0f,
			-0.5f,  0.5f, -0.5f, 0.6f, 1.0f, 0.4f,
			-0.5f, -0.5f, -0.5f, 0.6f, 0.8f, 0.8f,
			0.5f, -0.5f, -0.5f, 0.4f, 0.8f, 0.8f,
		};

		static uint[] triangle_indices = {
			// Front
			0, 1, 2,
			2, 3, 0,

			// Right
			0, 3, 7,
			7, 4, 0,

			// Bottom
			2, 6, 7,
			7, 3, 2,

			// Left
			1, 5, 6,
			6, 2, 1,

			// Back
			4, 7, 6,
			6, 5, 4,

			// Top
			5, 1, 0,
			0, 4, 5,
		};

		protected override void Init(GL gl)
		{
			Ebo = new BufferObject<uint>(gl, triangle_indices, BufferTargetARB.ElementArrayBuffer);
			Vbo = new BufferObject<float>(gl, vertices, BufferTargetARB.ArrayBuffer);
			Vao = new VertexArrayObject<float, uint>(gl, Vbo, Ebo);

			Vao.VertexAttributePointer(0, 3, VertexAttribPointerType.Float, 6, 0);
			Vao.VertexAttributePointer(1, 3, VertexAttribPointerType.Float, 6, 3);

			Shader = new Shader(gl, "/home/ramez/Downloads/shader.vert", "/home/ramez/Downloads/shader.frag");
		}

		protected override void OnDestroy(GL gl)
		{
			Vbo.Dispose();
			Ebo.Dispose();
			Vao.Dispose();
			Shader.Dispose();
		}

		unsafe protected override void RenderOverride(GL Gl)
		{
			Gl.Enable(EnableCap.DepthTest);
			Gl.ClearColor(0.1f, 0.12f, 0.2f, 1);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            Vao.Bind();
            Shader.Use();

            var transform = new Matrix4x4(-1, 0, 0, 0, 0, -1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1) * RotateY((float)(2 * Math.PI * animation(400))) * RotateX((float)(0.15 * Math.PI)) * Translate(0, 0, 300) * Perspective();

            Shader.SetUniform("transform", transform);
            Gl.DrawElements(PrimitiveType.Triangles, (uint) triangle_indices.Length, DrawElementsType.UnsignedInt, null);
			Gl.Disable(EnableCap.DepthTest);

			Invalidate();

            static float animation(float duration) {
                var ms_time = Stopwatch.GetTimestamp() / 10000;
                var ms_duration = duration * 1000;
                float ms_position = ms_time % ms_duration;

                return ms_position / ms_duration;
            }

            static Matrix4x4 Perspective()
            {
                const float
                    r = 0.5f,  // Half of the viewport width (at the near plane)
                    t = 0.5f,  // Half of the viewport height (at the near plane)
                    n = 1,  // Distance to near clipping plane
                    f = 5;  // Distance to far clipping plane

                // Note that while n and f are given as positive integers above,
                // the camera is looking in the negative direction. So we will see
                // stuff between z = -n and z = -f.

                return new Matrix4x4(
                    n / r, 0, 0, 0,
                    0, n / t, 0, 0,
                    0, 0, (-f - n) / (f - n), -1,
                    0, 0, (2 * f * n) / (n - f), 0
                );
            }

            static Matrix4x4 Translate(float x, float y, float z)
            {
                return new Matrix4x4(
                    1, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0,
                    x, y, z, 1
                );
            }

            static Matrix4x4 RotateX(float theta)
            {
                return new Matrix4x4(
                    1, 0, 0, 0,
                    0, (float)Math.Cos(theta), (float)Math.Sin(theta), 0,
                    0, (float)-Math.Sin(theta), (float)Math.Cos(theta), 0,
                    0, 0, 0, 1
                );
            }

            static Matrix4x4 RotateY(float theta)
            {
                return new Matrix4x4(
                    (float)Math.Cos(theta), 0, (float)-Math.Sin(theta), 0,
                    0, 1,           0, 0,
                    (float)Math.Sin(theta), 0, (float)Math.Cos(theta), 0,
                    0, 0,           0, 1
                );
            }
		}
	}
}
