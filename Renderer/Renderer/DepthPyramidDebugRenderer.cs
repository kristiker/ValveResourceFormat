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
        // Channel mapping for texture_decode.frag.slang: red into rgb, nothing into alpha.
        private const uint SingleRedChannel = 0xFFFFFF00;

        // ColorSpace_Linear in texture_decode.frag.slang, which applies a linear to gamma curve. Under
        // reverse Z everything but the closest geometry sits at the very bottom of the depth range, so
        // without the curve there is nothing to see.
        private const int DecodeFlagLinearToGamma = 1 << 5;

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
        /// <param name="width">Viewport width in pixels.</param>
        /// <param name="height">Viewport height in pixels.</param>
        public void Render(Scene scene, int width, int height)
        {
            var pyramid = scene.DepthPyramid;

            if (pyramid == null)
            {
                return;
            }

            using var _ = new GLDebugGroup("Depth Pyramid Debug");

            shader ??= rendererContext.ShaderLoader.LoadShader("vrf.texture_decode", ("S_TYPE_TEXTURE2D", 1));
            shader.Use();

            // Nothing else samples the pyramid - the culling shader only texel fetches it - so this is
            // free to keep its texels square and visible once it is blown up to the size of the window.
            pyramid.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);

            shader.SetTexture(0, "g_tInputTexture", pyramid);
            shader.SetUniform4("g_vInputTextureSize", new Vector4(pyramid.Width, pyramid.Height, 1f, pyramid.NumMipLevels));
            shader.SetUniform2("g_vViewportSize", new Vector2(width, height));
            shader.SetUniform1("g_nSelectedMip", 0);
            shader.SetUniform1("g_nSelectedChannels", SingleRedChannel);
            shader.SetUniform1("g_nDecodeFlags", DecodeFlagLinearToGamma);

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
