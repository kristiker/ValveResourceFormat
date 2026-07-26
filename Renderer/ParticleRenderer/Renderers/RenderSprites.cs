using System.Buffers;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Particles.Renderers
{
    /// <summary>
    /// Renders particles as camera-facing or orientation-aligned textured quads (sprites),
    /// with support for sprite sheet animation, blend modes, and per-particle color and alpha.
    /// </summary>
    /// <remarks>
    /// The workhorse renderer used by most effects. Multi-frame sequences can be animated or
    /// used to provide visual variation.
    /// </remarks>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particles/C_OP_RenderSprites">C_OP_RenderSprites</seealso>
    internal class RenderSprites : ParticleFunctionRenderer
    {
        private const string ShaderName = "vrf.particle_sprite";
        // One set per particle, not per vertex: position 3, right 3, up 3, colour 4, uv rect 4,
        // next-frame uv rect 4, frame blend 1.
        private const int VertexSize = 22;

        // The shader keeps one sampler per layer, so this is a hard ceiling rather than a preference.
        private const int MaxTextureLayers = 5;

        private static readonly INumberProvider OneNumberProvider = new LiteralNumberProvider(1f);

        // Interpolated names would allocate on every draw, and this is per-frame renderer code.
        private static readonly string[] LayerTextureUniforms = ["uTexture", "uTextureLayer1", "uTextureLayer2", "uTextureLayer3", "uTextureLayer4"];
        private static readonly string[] LayerChannelsUniforms = ["uLayerChannels[0]", "uLayerChannels[1]", "uLayerChannels[2]", "uLayerChannels[3]", "uLayerChannels[4]"];
        private static readonly string[] LayerBlendModeUniforms = ["uLayerBlendMode[0]", "uLayerBlendMode[1]", "uLayerBlendMode[2]", "uLayerBlendMode[3]", "uLayerBlendMode[4]"];
        private static readonly string[] LayerBlendUniforms = ["uLayerBlend[0]", "uLayerBlend[1]", "uLayerBlend[2]", "uLayerBlend[3]", "uLayerBlend[4]"];

        /// <summary>One entry of m_vecTexturesInput: a texture plus how it folds into the layers below it.</summary>
        private sealed class TextureLayer(RenderTexture texture)
        {
            public RenderTexture Texture { get; } = texture;
            public SpriteCardTextureChannel Channels { get; init; } = SpriteCardTextureChannel.SPRITECARD_TEXTURE_CHANNEL_MIX_RGBA;
            public ParticleTextureLayerBlendType BlendMode { get; init; } = ParticleTextureLayerBlendType.SPRITECARD_TEXTURE_BLEND_MULTIPLY;
            public INumberProvider Blend { get; init; } = OneNumberProvider;
        }

        private readonly Shader shader;
        private readonly RendererContext RendererContext;
        private readonly int vaoHandle;
        private readonly TextureLayer[] layers;

        private readonly float animationRate = 0.1f;
        private readonly ParticleAnimationType animationType = ParticleAnimationType.ANIMATION_TYPE_FIXED_RATE;
        private readonly INumberProvider minSize = new LiteralNumberProvider(0f);
        private readonly INumberProvider maxSize = new LiteralNumberProvider(5000f);
        private readonly INumberProvider startFadeSize = new LiteralNumberProvider(100000000f);
        private readonly INumberProvider endFadeSize = new LiteralNumberProvider(200000000f);
        private readonly bool distanceAlpha;

        // m_flStartFadeDot/m_flEndFadeDot: the normal-aligned modes fade out as the card turns edge-on to
        // the camera. The defaults span 1..2 against a value that never exceeds 1, so no fade by default.
        private readonly float startFadeDot = 1f;
        private readonly float endFadeDot = 2f;

        // m_flCenterXOffset/m_flCenterYOffset shift the quad within its own corner space, before the
        // radius scale, so the card pivots about a point other than its middle.
        private readonly INumberProvider centerXOffset = new LiteralNumberProvider(0f);
        private readonly INumberProvider centerYOffset = new LiteralNumberProvider(0f);
        private readonly bool gammaCorrectVertexColors;

        // m_bBlendFramesSeq0 cross-fades consecutive sheet frames instead of stepping between them.
        // m_bMaxLuminanceBlendingSequence0 swaps the plain lerp for a luminance-weighted one, which keeps
        // the brighter of the two frames dominant through the cross-fade.
        private readonly bool blendFrames = true;
        private readonly bool maxLuminanceFrameBlend;

        private readonly INumberProvider radiusScale = new LiteralNumberProvider(1f);
        private readonly INumberProvider alphaScale = new LiteralNumberProvider(1f);

        private readonly bool animateInFps;
        private readonly ParticleBlendMode blendMode = ParticleBlendMode.PARTICLE_OUTPUT_BLEND_MODE_ALPHA;
        private readonly INumberProvider overbrightFactor = new LiteralNumberProvider(1);
        private readonly ParticleOrientation orientationType;
        private readonly INumberProvider desaturation = new LiteralNumberProvider(0);
        // -1 means no control point, so no shift.
        private readonly int hsvShiftControlPoint = -1;
        private readonly INumberProvider diffuseAmount = new LiteralNumberProvider(1);
        private readonly INumberProvider selfIllumAmount = new LiteralNumberProvider(0);
        private readonly INumberProvider alphaMapToZero = new LiteralNumberProvider(0);
        private readonly INumberProvider alphaMapToOne = new LiteralNumberProvider(1);
        private readonly bool hasAlphaRemap;

        // m_nFeatheringMode: OFF, ON_OPTIONAL or ON_REQUIRED. We treat the two "on" values alike, since the
        // distinction is about whether the engine may skip the effect when the depth copy is unavailable.
        private readonly ParticleDepthFeatheringMode featheringMode;
        private readonly INumberProvider featheringMinDist = new LiteralNumberProvider(0f);
        private readonly INumberProvider featheringMaxDist = new LiteralNumberProvider(0f);

        private readonly bool outline;
        private readonly Vector4 outlineColor = Vector4.One;
        // Start0, End0, Start1, End1 -- the order the shader's two-sided ramp wants them in.
        private readonly Vector4 outlineRanges = new(0.5f, 0.7f, 0.6f, 0.8f);
        private int vertexBufferHandle;


        public RenderSprites(ParticleDefinitionParser parse, RendererContext rendererContext) : base(parse)
        {
            RendererContext = rendererContext;

            blendMode = parse.Enum<ParticleBlendMode>("m_nOutputBlendMode", blendMode);

            // The blend mode is a runtime uniform, not a static combo, so every sprite renderer shares one
            // compiled program regardless of m_nOutputBlendMode.
            shader = RendererContext.ShaderLoader.LoadShader(ShaderName);

            // The same quad is reused for all particles
            vaoHandle = SetupQuadBuffer();

            string? textureName = null;

            if (parse.Data.ContainsKey("m_hTexture"))
            {
                // Legacy single-texture form; equivalent to one layer with every control at its default.
                textureName = parse.Data.GetStringProperty("m_hTexture");
                layers = [new TextureLayer(rendererContext.MaterialLoader.GetTexture(textureName, srgbRead: true))];
            }
            else
            {
                var parsed = new List<TextureLayer>();

                foreach (var textureInput in parse.Array("m_vecTexturesInput"))
                {
                    if (!textureInput.Boolean("m_bEnabled", true))
                    {
                        continue;
                    }

                    // A gradient layer synthesizes its texture from m_Gradient rather than loading one.
                    if (textureInput.Boolean("m_bReplaceTextureWithGradient", false)
                        || !textureInput.Data.ContainsKey("m_hTexture"))
                    {
                        continue;
                    }

                    if (parsed.Count == MaxTextureLayers)
                    {
                        break;
                    }

                    var layerTextureName = textureInput.Data.GetStringProperty("m_hTexture");
                    textureName ??= layerTextureName;

                    parsed.Add(new TextureLayer(rendererContext.MaterialLoader.GetTexture(layerTextureName, srgbRead: true))
                    {
                        Channels = textureInput.Enum("m_nTextureChannels", SpriteCardTextureChannel.SPRITECARD_TEXTURE_CHANNEL_MIX_RGBA),
                        BlendMode = textureInput.Enum("m_nTextureBlendMode", ParticleTextureLayerBlendType.SPRITECARD_TEXTURE_BLEND_MULTIPLY),
                        Blend = textureInput.NumberProvider("m_flTextureBlend", OneNumberProvider),
                    });
                }

                layers = parsed.Count > 0 ? [.. parsed] : [new TextureLayer(rendererContext.MaterialLoader.GetErrorTexture())];
            }

#if DEBUG
            var vaoLabel = $"{nameof(RenderSprites)}: {System.IO.Path.GetFileName(textureName)}";
            GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vaoHandle, Math.Min(GLEnvironment.MaxLabelLength, vaoLabel.Length), vaoLabel);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vertexBufferHandle, Math.Min(GLEnvironment.MaxLabelLength, vaoLabel.Length), vaoLabel);
#endif

            animateInFps = parse.Boolean("m_bAnimateInFPS", animateInFps);
            overbrightFactor = parse.NumberProvider("m_flOverbrightFactor", overbrightFactor);
            orientationType = parse.Enum("m_nOrientationType", orientationType);
            animationRate = parse.Float("m_flAnimationRate", animationRate);
            minSize = parse.NumberProvider("m_flMinSize", minSize);
            maxSize = parse.NumberProvider("m_flMaxSize", maxSize);
            startFadeSize = parse.NumberProvider("m_flStartFadeSize", startFadeSize);
            endFadeSize = parse.NumberProvider("m_flEndFadeSize", endFadeSize);
            distanceAlpha = parse.Boolean("m_bDistanceAlpha", distanceAlpha);
            startFadeDot = parse.Float("m_flStartFadeDot", startFadeDot);
            endFadeDot = parse.Float("m_flEndFadeDot", endFadeDot);
            centerXOffset = parse.NumberProvider("m_flCenterXOffset", centerXOffset);
            centerYOffset = parse.NumberProvider("m_flCenterYOffset", centerYOffset);
            gammaCorrectVertexColors = parse.Boolean("m_bGammaCorrectVertexColors", gammaCorrectVertexColors);
            blendFrames = parse.Boolean("m_bBlendFramesSeq0", blendFrames);
            maxLuminanceFrameBlend = parse.Boolean("m_bMaxLuminanceBlendingSequence0", maxLuminanceFrameBlend);
            animationType = parse.Enum<ParticleAnimationType>("m_nAnimationType", animationType);
            radiusScale = parse.NumberProvider("m_flRadiusScale", radiusScale);
            alphaScale = parse.NumberProvider("m_flAlphaScale", alphaScale);
            desaturation = parse.NumberProvider("m_flDesaturation", desaturation);
            hsvShiftControlPoint = parse.Int32("m_nHSVShiftControlPoint", hsvShiftControlPoint);
            diffuseAmount = parse.NumberProvider("m_flDiffuseAmount", diffuseAmount);
            selfIllumAmount = parse.NumberProvider("m_flSelfIllumAmount", selfIllumAmount);
            alphaMapToZero = parse.NumberProvider("m_flSourceAlphaValueToMapToZero", alphaMapToZero);
            alphaMapToOne = parse.NumberProvider("m_flSourceAlphaValueToMapToOne", alphaMapToOne);

            // The remap is a smoothstep, so the nominal (0, 1) range is not the identity. Only enable it
            // where the effect actually authored a bound, otherwise every untouched particle would get an
            // ease curve applied to its alpha.
            hasAlphaRemap = parse.Data.ContainsKey("m_flSourceAlphaValueToMapToZero")
                || parse.Data.ContainsKey("m_flSourceAlphaValueToMapToOne");

            featheringMode = parse.Enum("m_nFeatheringMode", featheringMode);
            featheringMinDist = parse.NumberProvider("m_flFeatheringMinDist", featheringMinDist);
            featheringMaxDist = parse.NumberProvider("m_flFeatheringMaxDist", featheringMaxDist);

            outline = parse.Boolean("m_bOutline", outline);

            if (outline)
            {
                var color = parse.Color24("m_OutlineColor", new Vector3(1f));
                outlineColor = new Vector4(color, parse.Int32("m_nOutlineAlpha", 255) / 255f);
                outlineRanges = new Vector4(
                    parse.Float("m_flOutlineStart0", outlineRanges.X),
                    parse.Float("m_flOutlineEnd0", outlineRanges.Y),
                    parse.Float("m_flOutlineStart1", outlineRanges.Z),
                    parse.Float("m_flOutlineEnd1", outlineRanges.W));
            }
        }

        /// <inheritdoc/>
        public override bool WantsSceneDepth => featheringMode != ParticleDepthFeatheringMode.PARTICLE_DEPTH_FEATHERING_OFF;

        public override void SetWireframe(bool isWireframe)
        {
            // Solid color
            shader.SetUniform1("isWireframe", isWireframe ? 1 : 0);
        }

        private int SetupQuadBuffer()
        {
            const int stride = sizeof(float) * VertexSize;

            GL.CreateVertexArrays(1, out int vao);
            GL.CreateBuffers(1, out vertexBufferHandle);
            GL.VertexArrayVertexBuffer(vao, 0, vertexBufferHandle, 0, stride);

            // Every attribute advances once per particle; the corners come from gl_VertexID instead, so
            // there is no per-vertex data and no index buffer.
            GL.VertexArrayBindingDivisor(vao, 0, 1);

            // A driver is free to drop an attribute whose only use sits behind a uniform branch, in which
            // case GetAttribLocation reports -1 and binding it would raise a GL error.
            void SetupAttribute(string name, int components, int offsetInFloats)
            {
                var location = GL.GetAttribLocation(shader.Program, name);

                if (location < 0)
                {
                    return;
                }

                GL.EnableVertexArrayAttrib(vao, location);
                GL.VertexArrayAttribFormat(vao, location, components, VertexAttribType.Float, false, sizeof(float) * offsetInFloats);
                GL.VertexArrayAttribBinding(vao, location, 0);
            }

            SetupAttribute("aPosition", 3, 0);
            SetupAttribute("aRight", 3, 3);
            SetupAttribute("aUp", 3, 6);
            SetupAttribute("aVertexColor", 4, 9);
            SetupAttribute("aUvRect", 4, 13);
            SetupAttribute("aUvRectNextFrame", 4, 17);
            SetupAttribute("aFrameBlend", 1, 21);

            return vao;
        }

        // A quad orientation matrix from a base (right, up) pair with the particle roll folded in, matching the
        // spritecard vertex shader. The axes are intentionally not re-normalized (some modes rely on that, e.g.
        // SCREEN_Z foreshortens as the camera tilts). The face row is only the normal and does not affect corners.
        private static Matrix4x4 QuadBasis(Vector3 baseRight, Vector3 baseUp, float roll)
        {
            var c = MathF.Cos(roll);
            var s = MathF.Sin(roll);
            var right = (baseRight * c) + (baseUp * s);
            var up = (baseUp * c) - (baseRight * s);
            var face = Vector3.Cross(right, up);
            face = face.LengthSquared() > 1e-12f ? Vector3.Normalize(face) : Vector3.UnitZ;
            return new Matrix4x4(
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                face.X, face.Y, face.Z, 0f,
                0f, 0f, 0f, 1f);
        }

        // World-space camera forward (into the scene): the billboard maps local +Z to the toward-camera axis.
        private static Vector3 CameraForward(Matrix4x4 billboard)
            => -new Vector3(billboard.M31, billboard.M32, billboard.M33);

        // SCREEN_Z_ALIGNED: up locked to world +Z, right = cross(worldZ, forward) left un-normalized, so the
        // sprite yaws about vertical to face the camera and foreshortens as the view tilts off-horizontal.
        private static Matrix4x4 ScreenZAlignedBasis(Matrix4x4 billboard, float roll)
            => QuadBasis(Vector3.Cross(Vector3.UnitZ, CameraForward(billboard)), Vector3.UnitZ, roll);

        // WORLD_Z_ALIGNED: the quad lies flat in the world XY plane (normal = +Z), rolling about vertical,
        // independent of the camera.
        private static Matrix4x4 WorldZAlignedBasis(float roll)
            => QuadBasis(new Vector3(0f, -1f, 0f), new Vector3(1f, 0f, 0f), roll);

        // ALIGN_TO_PARTICLE_NORMAL: quad plane perpendicular to the particle normal, with the shader's canonical
        // tangent frame. The reference axis is world -Y once the normal tilts at all off horizontal, and world
        // +Z only while it is nearly horizontal; either choice stays clear of the normal.
        private static Matrix4x4 ParticleNormalBasis(Vector3 normal, float roll)
        {
            var reference = MathF.Abs(normal.Z) > 0.1f ? new Vector3(0f, -1f, 0f) : new Vector3(0f, 0f, 1f);
            var up = Vector3.Normalize(Vector3.Cross(normal, reference));
            var right = Vector3.Cross(up, normal);
            return QuadBasis(right, up, roll);
        }

        // SCREENALIGN_TO_PARTICLE_NORMAL: the quad's right edge follows the particle normal while it turns toward
        // the camera about that normal. Falls back to a billboard when the normal points at the camera.
        private static Matrix4x4 ScreenAlignToNormalBasis(Matrix4x4 billboard, Vector3 normal, float roll)
        {
            var n = Vector3.Normalize(normal);
            var w = Vector3.Cross(n, CameraForward(billboard));
            if (w.LengthSquared() < 1e-8f)
            {
                return billboard;
            }

            return QuadBasis(n, Vector3.Normalize(w), roll);
        }

        /// <summary>Fills and uploads the instance buffer, returning the number of particles emitted.</summary>
        private int UpdateVertices(ParticleCollection particles, ParticleSystemRenderState systemRenderState, Camera camera)
        {
            var modelViewMatrix = camera.CameraViewMatrix;

            // Create billboarding rotation (always facing camera)
            if (!Matrix4x4.Decompose(modelViewMatrix, out _, out var modelViewRotation, out _))
            {
                throw new InvalidOperationException("Matrix decompose failed");
            }

            modelViewRotation = Quaternion.Inverse(modelViewRotation);
            var billboardMatrix = Matrix4x4.CreateFromQuaternion(modelViewRotation);

            // Distance-driven size and fade. All four bounds are fractions of the screen a sprite may
            // cover, so they compare against radius / (distance * tan(fov/2)): the minimum keeps tiny
            // flashes visible at any camera distance, and the two fade bounds dissolve a sprite that grows
            // past them. The whole group is gated on m_bDistanceAlpha, as it is in the shader.
            var minScreenSize = minSize.NextNumber(systemRenderState);
            var maxScreenSize = maxSize.NextNumber(systemRenderState);
            var startFadeScreenSize = startFadeSize.NextNumber(systemRenderState);
            var endFadeScreenSize = endFadeSize.NextNumber(systemRenderState);
            var tanHalfFov = MathF.Tan(camera.GetFOV() * 0.5f);

            var centerOffset = new Vector2(
                centerXOffset.NextNumber(systemRenderState),
                centerYOffset.NextNumber(systemRenderState));

            // Only the two normal-aligned modes fade by view angle, and only when the range can actually
            // be entered: the value it tests is a dot product magnitude, so it never exceeds 1.
            var viewAngleFadeActive = startFadeDot < 1f
                && endFadeDot > startFadeDot
                && orientationType is ParticleOrientation.PARTICLE_ORIENTATION_ALIGN_TO_PARTICLE_NORMAL
                    or ParticleOrientation.PARTICLE_ORIENTATION_SCREENALIGN_TO_PARTICLE_NORMAL;

            // Update vertex buffer
            var rawVertices = ArrayPool<float>.Shared.Rent(particles.Count * VertexSize);

            try
            {
                var i = 0;
                foreach (ref var particle in particles.Current)
                {
                    var radiusScale = this.radiusScale.NextNumber(ref particle, systemRenderState);

                    // Scales rgb and alpha alike, matching the shader's fade of the whole vertex colour.
                    var colorFade = 1f;

                    // The view-angle fade touches alpha only, unlike the size fade below.
                    var alphaFade = 1f;

                    if (viewAngleFadeActive)
                    {
                        var toCamera = camera.Location - particle.Position;

                        if (toCamera.LengthSquared() > 1e-12f)
                        {
                            var facing = MathF.Abs(Vector3.Dot(Vector3.Normalize(particle.Normal), Vector3.Normalize(toCamera)));
                            alphaFade = 1f - MathUtils.Smoothstep(startFadeDot, endFadeDot, facing);
                        }
                    }

                    if (distanceAlpha && tanHalfFov > 0f)
                    {
                        var screenHalfHeight = Vector3.Distance(camera.Location, particle.Position) * tanHalfFov;
                        var radius = particle.Radius * radiusScale;
                        var fadeStart = startFadeScreenSize * screenHalfHeight;
                        var fadeEnd = endFadeScreenSize * screenHalfHeight;

                        if (radius > fadeStart)
                        {
                            if (radius >= fadeEnd)
                            {
                                // Faded out entirely; emitting the quad would only cost overdraw.
                                continue;
                            }

                            colorFade = 1f - ((radius - fadeStart) / (fadeEnd - fadeStart));
                        }

                        if (particle.Radius > 0f)
                        {
                            // Expressed back as a scale, because the corner transform takes one. Nested
                            // min/max rather than a clamp: an inverted range has to resolve to the maximum
                            // the way the shader's does, not throw.
                            radiusScale = MathF.Min(MathF.Max(radius, minScreenSize * screenHalfHeight), maxScreenSize * screenHalfHeight) / particle.Radius;
                        }
                    }

                    // Per-mode quad orientation, ported from the spritecard vertex shader (roll = Rotation.Z).
                    // SCREEN_ALIGNED is the plain camera billboard; FULL_3AXIS_ROTATION has no shader variant and
                    // uses the particle's full rotation basis.
                    var roll = particle.Rotation.Z;
                    var modelMatrix = orientationType switch
                    {
                        ParticleOrientation.PARTICLE_ORIENTATION_SCREEN_ALIGNED => particle.GetRotationMatrix() * billboardMatrix * particle.GetTransformationMatrix(radiusScale),
                        ParticleOrientation.PARTICLE_ORIENTATION_SCREEN_Z_ALIGNED => ScreenZAlignedBasis(billboardMatrix, roll) * particle.GetTransformationMatrix(radiusScale),
                        ParticleOrientation.PARTICLE_ORIENTATION_WORLD_Z_ALIGNED => WorldZAlignedBasis(roll) * particle.GetTransformationMatrix(radiusScale),
                        ParticleOrientation.PARTICLE_ORIENTATION_ALIGN_TO_PARTICLE_NORMAL => ParticleNormalBasis(particle.Normal, roll) * particle.GetTransformationMatrix(radiusScale),
                        ParticleOrientation.PARTICLE_ORIENTATION_SCREENALIGN_TO_PARTICLE_NORMAL => ScreenAlignToNormalBasis(billboardMatrix, particle.Normal, roll) * particle.GetTransformationMatrix(radiusScale),
                        _ => particle.GetRotationMatrix() * particle.GetTransformationMatrix(radiusScale),
                    };

                    // The corner map is corner.x * row0 + corner.y * row1 + translation, so the first two
                    // rows are the card's axes with the radius already in them. Extracting them lets the
                    // vertex shader expand the quad, instead of four Vector4.Transform calls here.
                    var right = new Vector3(modelMatrix.M11, modelMatrix.M12, modelMatrix.M13);
                    var up = new Vector3(modelMatrix.M21, modelMatrix.M22, modelMatrix.M23);

                    // The centre offset is measured in half-widths, so it folds into the origin along
                    // those same axes rather than into the world position.
                    var origin = new Vector3(modelMatrix.M41, modelMatrix.M42, modelMatrix.M43)
                        + (centerOffset.X * right)
                        + (centerOffset.Y * up);

                    var alphaScale = this.alphaScale.NextNumber(ref particle, systemRenderState);

                    var quadStart = i * VertexSize;
                    rawVertices[quadStart + 0] = origin.X;
                    rawVertices[quadStart + 1] = origin.Y;
                    rawVertices[quadStart + 2] = origin.Z;
                    rawVertices[quadStart + 3] = right.X;
                    rawVertices[quadStart + 4] = right.Y;
                    rawVertices[quadStart + 5] = right.Z;
                    rawVertices[quadStart + 6] = up.X;
                    rawVertices[quadStart + 7] = up.Y;
                    rawVertices[quadStart + 8] = up.Z;
                    rawVertices[quadStart + 9] = particle.Color.X * colorFade;
                    rawVertices[quadStart + 10] = particle.Color.Y * colorFade;
                    rawVertices[quadStart + 11] = particle.Color.Z * colorFade;
                    rawVertices[quadStart + 12] = particle.Alpha * alphaScale * colorFade * alphaFade;

                    // UVs. Animated sheets emit the frame the particle is on plus the one after it, and
                    // how far between them it sits, so the fragment shader can cross-fade rather than step.
                    var uvMin = Vector2.Zero;
                    var uvMax = Vector2.One;
                    var uvNextMin = Vector2.Zero;
                    var uvNextMax = Vector2.One;
                    var frameBlend = 0f;

                    // The sheet sequence comes from the base layer; extra layers ride its frame timing.
                    var spriteSheetData = layers[0].Texture.SpriteSheetData;
                    if (spriteSheetData != null && spriteSheetData.Sequences.Length > 0 && spriteSheetData.Sequences[0].Frames.Length > 0)
                    {
                        var sequence = spriteSheetData.Sequences[particle.Sequence % spriteSheetData.Sequences.Length];

                        var frame = sequence.Frames.Length > 1
                            ? GetSheetFrame(ref particle, sequence.FramesPerSecond, animationRate, animationType, animateInFps)
                            : 0f;

                        var frameId = (int)MathF.Floor(frame);
                        frameBlend = frame - frameId;

                        // TODO: Support more than one image per frame?
                        var currentImage = sequence.Frames[ResolveSheetFrame(frameId, sequence.Frames.Length, sequence.Clamp)].Images[0];
                        var nextImage = sequence.Frames[ResolveSheetFrame(frameId + 1, sequence.Frames.Length, sequence.Clamp)].Images[0];

                        uvMin = currentImage.UncroppedMin;
                        uvMax = currentImage.UncroppedMax;
                        uvNextMin = nextImage.UncroppedMin;
                        uvNextMax = nextImage.UncroppedMax;
                    }

                    rawVertices[quadStart + 13] = uvMin.X;
                    rawVertices[quadStart + 14] = uvMin.Y;
                    rawVertices[quadStart + 15] = uvMax.X;
                    rawVertices[quadStart + 16] = uvMax.Y;
                    rawVertices[quadStart + 17] = uvNextMin.X;
                    rawVertices[quadStart + 18] = uvNextMin.Y;
                    rawVertices[quadStart + 19] = uvNextMax.X;
                    rawVertices[quadStart + 20] = uvNextMax.Y;
                    rawVertices[quadStart + 21] = frameBlend;

                    i++;
                }

                GL.NamedBufferData(vertexBufferHandle, i * VertexSize * sizeof(float), rawVertices, BufferUsageHint.DynamicDraw);

                return i;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rawVertices);
            }
        }

        public override void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Camera camera)
        {
            if (particleBag.Count == 0)
            {
                return;
            }

            // Update vertex buffer. Fully faded particles are skipped, so this can be fewer than the
            // live particle count.
            var quadCount = UpdateVertices(particleBag, systemRenderState, camera);

            if (quadCount == 0)
            {
                return;
            }

            // Draw it. The translucent pass leaves blend/depth state to each custom draw, so enable blending and
            // stop depth writes here; otherwise sprites are opaque. The cable renderer instead draws opaque with depth writes.
            GL.Enable(EnableCap.Blend);
            GL.DepthMask(false);

            // Premultiplied output; the shader zeroes the blend weight for the additive modes, which turns
            // this into a plain add. Spritecard ships no blend state that scales the destination -- MOD2X
            // included -- so there is no mode here that darkens what is behind the particle either.
            GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

            GL.Disable(EnableCap.CullFace);

            shader.Use();
            GL.BindVertexArray(vaoHandle);

            // Layer 0 keeps the plain uTexture name; the rest take a sampler each. Units past the layer
            // count are never sampled, but they get layer 0's texture so no sampler is left unbound.
            for (var layer = 0; layer < MaxTextureLayers; layer++)
            {
                var source = layer < layers.Length ? layers[layer] : layers[0];
                shader.SetTexture(RenderMaterial.TextureUnitStart + layer, LayerTextureUniforms[layer], source.Texture);
            }

            shader.SetUniform1("uLayerCount", layers.Length);

            for (var layer = 0; layer < layers.Length; layer++)
            {
                shader.SetUniform1(LayerChannelsUniforms[layer], (int)layers[layer].Channels);
                shader.SetUniform1(LayerBlendModeUniforms[layer], (int)layers[layer].BlendMode);
                shader.SetUniform1(LayerBlendUniforms[layer], layers[layer].Blend.NextNumber(systemRenderState));
            }

            // TODO: This formula is a guess but still seems too bright compared to valve particles
            shader.SetUniform1("uOverbrightFactor", overbrightFactor.NextNumber(systemRenderState));
            shader.SetUniform1("uColorFactor", diffuseAmount.NextNumber(systemRenderState) + selfIllumAmount.NextNumber(systemRenderState));
            shader.SetUniform1("uDesaturation", desaturation.NextNumber(systemRenderState));

            // The control point carries (hue offset, saturation scale, value scale). Identity when absent.
            shader.SetUniform3("uHsvShift", hsvShiftControlPoint >= 0
                ? systemRenderState.GetControlPoint(hsvShiftControlPoint).Position
                : new Vector3(0f, 1f, 1f));

            // x >= y disables the remap in the shader.
            var alphaRemapRange = hasAlphaRemap
                ? new Vector2(alphaMapToZero.NextNumber(systemRenderState), alphaMapToOne.NextNumber(systemRenderState))
                : new Vector2(1f, 0f);
            shader.SetUniform2("uAlphaRemapRange", alphaRemapRange);

            // g_tSceneDepth lives on a reserved texture unit that the scene keeps bound for the whole pass,
            // so this only has to point the sampler at that unit -- never rebind the texture here.
            shader.SetUniform1("g_tSceneDepth", (int)ReservedTextureSlots.SceneDepth);

            var featheringRange = WantsSceneDepth
                ? new Vector2(featheringMinDist.NextNumber(systemRenderState), featheringMaxDist.NextNumber(systemRenderState))
                : Vector2.Zero;
            shader.SetUniform2("uFeatheringRange", featheringRange);

            shader.SetUniform1("uGammaCorrectVertexColors", gammaCorrectVertexColors);
            shader.SetUniform1("uBlendFrames", blendFrames);
            shader.SetUniform1("uMaxLuminanceFrameBlend", maxLuminanceFrameBlend);
            shader.SetUniform1("uOutline", outline);
            shader.SetUniform4("uOutlineColor", outlineColor);
            shader.SetUniform4("uOutlineRanges", outlineRanges);

            // Set every draw: the program is shared with every other sprite renderer, whatever their mode.
            shader.SetUniform1("uBlendMode", (int)blendMode);

            // DRAW
            PerfStats.Active.Count(Counter.ParticleDraw);
            GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, quadCount);

            GL.Enable(EnableCap.CullFace);
        }

        public override IEnumerable<string> GetSupportedRenderModes() => shader.RenderModes;

        public override void SetRenderMode(string renderMode)
        {
        }

        public override void Delete()
        {
            GL.DeleteVertexArray(vaoHandle);
            GL.DeleteBuffer(vertexBufferHandle);
        }
    }
}
