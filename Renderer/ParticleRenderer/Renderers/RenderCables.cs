using OpenTK.Graphics.OpenGL;

namespace ValveResourceFormat.Renderer.Particles.Renderers
{
    internal class RenderCables : ParticleFunctionRenderer
    {
        private const string ShaderName = "vrf.particle_trail";

        private readonly Shader shader;
        private readonly RendererContext RendererContext;
        private readonly int vaoHandle;
        private readonly RenderMaterial material;

        private readonly INumberProvider textureRepeatsPerSegment = new LiteralNumberProvider(1f);
        private readonly float maxTesselation = 100f;
        private readonly int roundness;

        public RenderCables(ParticleDefinitionParser parse, RendererContext rendererContext) : base(parse)
        {
            RendererContext = rendererContext;
            shader = RendererContext.ShaderLoader.LoadShader(ShaderName);

            // The same quad is reused for all cable segments
            vaoHandle = SetupQuadBuffer();

            string? materialName = null;

            if (parse.Data.ContainsKey("m_hMaterial"))
            {
                materialName = parse.Data.GetProperty<string>("m_hMaterial");
            }

            if (materialName == null)
            {
                material = RendererContext.MaterialLoader.GetMaterial(null, null);
            }
            else
            {
                material = RendererContext.MaterialLoader.GetMaterial(materialName, null);
            }

#if DEBUG
            var vaoLabel = $"{nameof(RenderCables)}: {material.Material.Name}";
            GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vaoHandle, Math.Min(GLEnvironment.MaxLabelLength, vaoLabel.Length), vaoLabel);
#endif

            // Cable-specific parameters
            textureRepeatsPerSegment = parse.NumberProvider("m_flTextureRepeatsPerSegment", textureRepeatsPerSegment);
            maxTesselation = parse.Float("m_nMaxTesselation", maxTesselation);
            roundness = parse.Int32("m_nRoundness", roundness);
        }

        public override void SetWireframe(bool isWireframe)
        {
            shader.SetUniform1("isWireframe", isWireframe ? 1 : 0);
        }

        private int SetupQuadBuffer()
        {
            var vertices = new[]
            {
                -1.0f, -1.0f, 0.0f,
                -1.0f, 1.0f, 0.0f,
                1.0f, -1.0f, 0.0f,
                1.0f, 1.0f, 0.0f,
            };

            GL.CreateVertexArrays(1, out int vao);
            GL.CreateBuffers(1, out int buffer);
            GL.NamedBufferData(buffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
            GL.VertexArrayVertexBuffer(vao, 0, buffer, 0, sizeof(float) * 3);

            var attributeLocation = GL.GetAttribLocation(shader.Program, "aVertexPosition");
            GL.EnableVertexArrayAttrib(vao, attributeLocation);
            GL.VertexArrayAttribFormat(vao, attributeLocation, 3, VertexAttribType.Float, false, 0);
            GL.VertexArrayAttribBinding(vao, attributeLocation, 0);

            return vao;
        }

        public override void Render(ParticleCollection particleBag, ParticleSystemRenderState systemRenderState, Matrix4x4 modelViewMatrix)
        {
            var particles = particleBag.Current;

            if (particles.Length < 2)
            {
                return; // Need at least 2 particles to render a cable
            }

            shader.Use();

            GL.BindVertexArray(vaoHandle);

            // Render material
            material.Render(shader);

            // Create billboarding rotation (always facing camera)
            if (!Matrix4x4.Decompose(modelViewMatrix, out _, out var modelViewRotation, out _))
            {
                throw new InvalidOperationException("Matrix decompose failed");
            }

            modelViewRotation = Quaternion.Inverse(modelViewRotation);

            // Render cable segments between consecutive particles
            for (var i = 0; i < particles.Length - 1; i++)
            {
                ref var particle1 = ref particles[i];
                ref var particle2 = ref particles[i + 1];

                var position1 = particle1.Position;
                var position2 = particle2.Position;
                var difference = position2 - position1;
                var direction = Vector3.Normalize(difference);
                var segmentLength = difference.Length();

                if (segmentLength < 0.001f)
                {
                    continue; // Skip zero-length segments
                }

                var midPoint = position1 + (0.5f * difference);

                // Average radius and alpha between the two particles
                var radius = (particle1.Radius + particle2.Radius) * 0.5f;
                var alpha = (particle1.Alpha + particle2.Alpha) * 0.5f;
                var color = (particle1.Color + particle2.Color) * 0.5f;

                // Scale matrix: width = radius, length = segment length / 2
                var scaleMatrix = Matrix4x4.CreateScale(radius, segmentLength / 2f, 1);

                // todo: render similar to MeshBatchRenderer
                // ...
            }

            material.PostRender();
            GL.UseProgram(0);
            GL.BindVertexArray(0);
        }

        public override IEnumerable<string> GetSupportedRenderModes() => shader.RenderModes;

        public override void SetRenderMode(string renderMode)
        {
        }
    }
}
