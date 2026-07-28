using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Shaders;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Draws the top level of the occlusion culling depth pyramid over the finished frame, so that what
    /// the culling shader tests against can be looked at directly.
    /// </summary>
    public class DepthPyramidDebugRenderer
    {
        private readonly RendererContext rendererContext;
        private Shader? shader;

        /// <summary>Initializes the depth pyramid debug renderer.</summary>
        /// <param name="rendererContext">Renderer context used to load the shader and the empty vertex array.</param>
        public DepthPyramidDebugRenderer(RendererContext rendererContext)
        {
            this.rendererContext = rendererContext;
        }

        /// <summary>Draws the depth pyramid's top mip level over the whole viewport.</summary>
        /// <param name="scene">Scene owning the depth pyramid.</param>
        /// <param name="camera">Camera the pyramid was built for, which the depth is relative to.</param>
        /// <param name="width">Viewport width in pixels.</param>
        /// <param name="height">Viewport height in pixels.</param>
        public void Render(Scene scene, Camera camera, int width, int height)
        {
            var pyramid = scene.DepthPyramid;

            if (pyramid == null)
            {
                return;
            }

            using var _ = new GLDebugGroup("Depth Pyramid Debug");

            shader ??= rendererContext.ShaderLoader.LoadShader("vrf.depth_pyramid_debug");
            shader.Use();

            // Nothing else samples the pyramid - the culling shader only texel fetches it - so this is
            // free to keep its texels square and visible once it is blown up to the size of the window.
            pyramid.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);

            shader.SetTexture(0, "g_tDepthPyramid", pyramid);
            shader.SetUniform2("g_vViewportSize", new Vector2(width, height));
            shader.SetUniform2("g_vSceneDepthRange", new Vector2(Renderer.DepthRange.Scene.Near, Renderer.DepthRange.Scene.Far));
            shader.SetUniform1("g_flNearPlane", camera.ProjectionMatrix.M43);

            GL.Viewport(0, 0, width, height);
            GL.DepthMask(false);
            GL.Disable(EnableCap.DepthTest);

            GL.BindVertexArray(rendererContext.MeshBufferCache.EmptyVAO);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
        }
    }
}
