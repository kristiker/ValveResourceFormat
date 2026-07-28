using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.CompiledShader;

namespace ValveResourceFormat.Renderer.Shaders.Vcs;

/// <summary>
/// Applies the per-dynamic-combo render state stored in a compiled pixel shader to the OpenGL pipeline,
/// and restores the renderer's default state afterwards.
/// </summary>
/// <remarks>
/// Pass-level state (whether blending is enabled at all, global depth func direction, framebuffer setup)
/// stays with the renderer's pass logic; this only applies the states a material would otherwise set.
/// </remarks>
public static class VcsRenderStateGl
{
    /// <summary>
    /// Applies the rasterizer, depth and blend state of the given render state info.
    /// </summary>
    public static void Apply(VfxRenderStateInfoPixelShader renderState)
    {
        if (renderState.RasterizerStateDesc is { } rasterizer)
        {
            switch (rasterizer.CullMode)
            {
                case VfxRenderStateInfoPixelShader.RsRasterizerStateDesc.RsCullMode.None:
                    GL.Disable(EnableCap.CullFace);
                    break;
                case VfxRenderStateInfoPixelShader.RsRasterizerStateDesc.RsCullMode.Front:
                    GL.CullFace(TriangleFace.Front);
                    break;
                default:
                    break; // Back is the renderer default
            }

            if (rasterizer.DepthBias != 0 || rasterizer.SlopeScaledDepthBias != 0f)
            {
                GL.Enable(EnableCap.PolygonOffsetFill);

                // D3D integer depth bias is in units of the smallest representable depth difference;
                // GL polygon offset units are equivalent, so the values carry over directly.
                GL.PolygonOffsetClamp(rasterizer.SlopeScaledDepthBias, rasterizer.DepthBias, rasterizer.DepthBiasClamp);
            }
        }

        if (renderState.DepthStencilStateDesc is { } depthStencil)
        {
            if (!depthStencil.DepthTestEnable)
            {
                GL.Disable(EnableCap.DepthTest);
            }
            else
            {
                // Valve stores comparisons in standard-Z convention; the renderer runs reverse-Z.
                GL.DepthFunc(VcsSamplerCache.ToGlComparison(FlipForReverseZ(depthStencil.DepthFunc)));
            }

            GL.DepthMask(depthStencil.DepthWriteEnable);
        }

        if (renderState.BlendStateDesc is { } blend)
        {
            if (blend.AlphaToCoverageEnable)
            {
                GL.Enable(EnableCap.SampleAlphaToCoverage);
            }

            if (blend.BlendEnable[0])
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendFuncSeparate(
                    ToGlSrcBlend(blend.SrcBlend[0]),
                    ToGlDstBlend(blend.DestBlend[0]),
                    ToGlSrcBlend(blend.SrcBlendAlpha[0]),
                    ToGlDstBlend(blend.DestBlendAlpha[0]));
                GL.BlendEquationSeparate(ToGlBlendOp(blend.BlendOp[0]), ToGlBlendOp(blend.BlendOpAlpha[0]));
            }

            var writeMask = blend.RenderTargetWriteMask[0];
            if (writeMask != VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.All)
            {
                GL.ColorMask(
                    writeMask.HasFlag(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.R),
                    writeMask.HasFlag(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.G),
                    writeMask.HasFlag(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.B),
                    writeMask.HasFlag(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.A));
            }
        }
    }

    /// <summary>
    /// Restores the renderer's default state for everything <see cref="Apply"/> may have changed.
    /// </summary>
    public static void Reset(VfxRenderStateInfoPixelShader renderState)
    {
        if (renderState.RasterizerStateDesc is { } rasterizer)
        {
            switch (rasterizer.CullMode)
            {
                case VfxRenderStateInfoPixelShader.RsRasterizerStateDesc.RsCullMode.None:
                    GL.Enable(EnableCap.CullFace);
                    break;
                case VfxRenderStateInfoPixelShader.RsRasterizerStateDesc.RsCullMode.Front:
                    GL.CullFace(TriangleFace.Back);
                    break;
                default:
                    break;
            }

            if (rasterizer.DepthBias != 0 || rasterizer.SlopeScaledDepthBias != 0f)
            {
                GL.Disable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffsetClamp(0f, 0f, 0f);
            }
        }

        if (renderState.DepthStencilStateDesc is { } depthStencil)
        {
            if (!depthStencil.DepthTestEnable)
            {
                GL.Enable(EnableCap.DepthTest);
            }
            else
            {
                GL.DepthFunc(DepthFunction.Greater); // The renderer's reverse-Z default
            }

            GL.DepthMask(true);
        }

        if (renderState.BlendStateDesc is { } blend)
        {
            if (blend.AlphaToCoverageEnable)
            {
                GL.Disable(EnableCap.SampleAlphaToCoverage);
            }

            if (blend.BlendEnable[0])
            {
                GL.Disable(EnableCap.Blend);
                GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
            }

            if (blend.RenderTargetWriteMask[0] != VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsColorWriteEnableBits.All)
            {
                GL.ColorMask(true, true, true, true);
            }
        }
    }

    private static RsComparison FlipForReverseZ(RsComparison comparison) => comparison switch
    {
        RsComparison.Less => RsComparison.Greater,
        RsComparison.LessEqual => RsComparison.GreaterEqual,
        RsComparison.Greater => RsComparison.Less,
        RsComparison.GreaterEqual => RsComparison.LessEqual,
        _ => comparison,
    };

    private static BlendingFactorSrc ToGlSrcBlend(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode mode)
        => (BlendingFactorSrc)ToGlBlend(mode);

    private static BlendingFactorDest ToGlDstBlend(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode mode)
        => (BlendingFactorDest)ToGlBlend(mode);

    private static BlendingFactor ToGlBlend(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode mode) => mode switch
    {
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.Zero => BlendingFactor.Zero,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.One => BlendingFactor.One,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.SrcColor => BlendingFactor.SrcColor,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.InvSrcColor => BlendingFactor.OneMinusSrcColor,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.SrcAlpha => BlendingFactor.SrcAlpha,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.InvSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.DestAlpha => BlendingFactor.DstAlpha,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.InvDestAlpha => BlendingFactor.OneMinusDstAlpha,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.DestColor => BlendingFactor.DstColor,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.InvDestColor => BlendingFactor.OneMinusDstColor,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.SrcAlphaSat => BlendingFactor.SrcAlphaSaturate,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.BlendFactor => BlendingFactor.ConstantColor,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendMode.InvBlendFactor => BlendingFactor.OneMinusConstantColor,
        _ => BlendingFactor.One,
    };

    private static BlendEquationMode ToGlBlendOp(VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp op) => op switch
    {
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp.Add => BlendEquationMode.FuncAdd,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp.Subtract => BlendEquationMode.FuncSubtract,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp.RevSubtract => BlendEquationMode.FuncReverseSubtract,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp.Min => BlendEquationMode.Min,
        VfxRenderStateInfoPixelShader.RsBlendStateDesc.RsBlendOp.Max => BlendEquationMode.Max,
        _ => BlendEquationMode.FuncAdd,
    };
}
