using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.SceneNodes;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Base class for debug overlay renderers that draw a batch of colored lines
    /// with the default shader: owns the vertex array/buffer pair and the common
    /// blended, depth-read-only render state.
    /// </summary>
    public abstract class LineDebugRenderer
    {
        private readonly Shader shader;
        private readonly int vaoHandle;
        private readonly int vboHandle;

        /// <summary>Gets or sets the number of vertices currently uploaded to the GPU.</summary>
        protected int VertexCount { get; set; }

        /// <summary>Creates the GPU vertex array and buffer objects for line drawing.</summary>
        /// <param name="rendererContext">Renderer context for loading shaders.</param>
        /// <param name="label">Debug label applied to the GL objects.</param>
        protected LineDebugRenderer(RendererContext rendererContext, string label)
        {
            shader = rendererContext.ShaderLoader.LoadShader("vrf.default");

            GL.CreateVertexArrays(1, out vaoHandle);
            GL.CreateBuffers(1, out vboHandle);
            GL.VertexArrayVertexBuffer(vaoHandle, 0, vboHandle, 0, SimpleVertex.SizeInBytes);
            SimpleVertex.BindDefaultShaderLayout(vaoHandle, shader.Program);

#if DEBUG
            GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vaoHandle, label.Length, label);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vboHandle, label.Length, label);
#endif
        }

        /// <summary>Uploads the line vertices to the GPU vertex buffer.</summary>
        /// <param name="vertices">Vertices to upload, two per line segment.</param>
        /// <param name="usageHint">Buffer usage hint for the upload.</param>
        protected void Upload(List<SimpleVertex> vertices, BufferUsageHint usageHint = BufferUsageHint.DynamicDraw)
        {
            VertexCount = vertices.Count;
            GL.NamedBufferData(vboHandle, VertexCount * SimpleVertex.SizeInBytes, ListAccessors<SimpleVertex>.GetBackingArray(vertices), usageHint);
        }

        /// <summary>Draws the uploaded lines as a blended overlay without writing depth.</summary>
        /// <param name="disableDepthTest">When <see langword="true"/>, the lines are drawn on top of everything.</param>
        protected void RenderLines(bool disableDepthTest = false)
        {
            if (VertexCount == 0)
            {
                return;
            }

            GL.Enable(EnableCap.Blend);

            if (disableDepthTest)
            {
                GL.Disable(EnableCap.DepthTest);
            }

            GL.DepthMask(false);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            shader.Use();
            shader.SetUniform3x4("transform", Matrix4x4.Identity);

            GL.BindVertexArray(vaoHandle);
            GL.DrawArrays(PrimitiveType.Lines, 0, VertexCount);
            GL.UseProgram(0);
            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);

            if (disableDepthTest)
            {
                GL.Enable(EnableCap.DepthTest);
            }
        }

        /// <summary>Deletes the GPU vertex and vertex array objects.</summary>
        public void Delete()
        {
            GL.DeleteBuffer(vboHandle);
            GL.DeleteVertexArray(vaoHandle);
        }
    }
}
