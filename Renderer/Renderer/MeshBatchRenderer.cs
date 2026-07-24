using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.World;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Sorts and dispatches batched mesh draw calls for a render pass.
    /// </summary>
    public static class MeshBatchRenderer
    {
        /// <summary>
        /// Draw call request with distance and render order for sorting.
        /// </summary>
#if DEBUG
        [DebuggerDisplay("{Node.DebugName,nq}")]
#endif
        public record struct Request(RenderableMesh Mesh, DrawCall? Call, float DistanceFromCamera, int RenderOrder, SceneNode Node)
        {
            /// <summary>
            /// Opportunistic instancing state: 0 or 1 = drawn individually, above 1 = head of an instanced
            /// group with that many instances, -1 = consumed by a preceding group head.
            /// </summary>
            public int InstanceCount { get; set; }

            /// <summary>Offset of this group's first entry in the per-frame object index list (valid when <see cref="InstanceCount"/> is above 1).</summary>
            public int ObjectIndexOffset { get; set; }
        }

        record struct BatchRequest(RenderableMesh Mesh, DrawCall Call, SceneNode Node, int InstanceCount, int ObjectIndexOffset);

        /// <summary>Values of the nInstancingMode shader uniform. Must match instancing.slang.</summary>
        private static class InstancingMode
        {
            /// <summary>Single draw; transform and tint come from per-draw uniforms.</summary>
            public const int None = 0;
            /// <summary>Aggregate with sequential instance transforms starting at the object's transform index.</summary>
            public const int Aggregate = 1;
            /// <summary>gl_BaseInstance offsets into the per-frame object index list; one object per instance.</summary>
            public const int ObjectList = 2;
        }

        /// <summary>Compares two requests by shader pipeline sort ID, placing custom-render nodes at the boundary.
        /// Ties are broken by geometry identity so that identical draws end up adjacent for opportunistic instancing.</summary>
        public static int CompareCustomPipeline(Request a, Request b)
        {
            const int CustomRenderSortId = 500 * -RenderMaterial.PerShaderSortIdRange;

            return (a.Call, b.Call) switch
            {
                ({ }, { }) => ComparePipelineThenGeometry(a.Call, b.Call),
                (null, { }) => b.Call.Material.SortId - CustomRenderSortId,
                ({ }, null) => CustomRenderSortId - a.Call.Material.SortId,
                (null, null) => 0,
            };
        }

        private static int ComparePipelineThenGeometry(DrawCall a, DrawCall b)
        {
            var comparison = b.Material.SortId - a.Material.SortId;

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.IndexBuffer.Handle.CompareTo(b.IndexBuffer.Handle);

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.BaseVertex.CompareTo(b.BaseVertex);

            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.StartIndex.CompareTo(b.StartIndex);

            if (comparison != 0)
            {
                return comparison;
            }

            return a.IndexCount.CompareTo(b.IndexCount);
        }

        /// <summary>Compares two requests first by render order, then by shader pipeline sort ID.</summary>
        public static int CompareRenderOrderThenPipeline(Request a, Request b)
        {
            if (a.RenderOrder == b.RenderOrder)
            {
                return a.Call!.Material.SortId - b.Call!.Material.SortId;
            }

            return a.RenderOrder - b.RenderOrder;
        }

        /// <summary>Compares two requests by distance from camera, furthest first (back-to-front).</summary>
        public static int CompareCameraDistance(Request a, Request b)
        {
            return -a.DistanceFromCamera.CompareTo(b.DistanceFromCamera);
        }

        /// <summary>Compares two requests by alpha-test flag first, then by shader program sort ID.</summary>
        public static int CompareAlphaTestThenProgram(Request a, Request b)
        {
            Debug.Assert(a.Call != null && b.Call != null);
            var alphaTestA = a.Call.Material.IsAlphaTest;
            var alphaTestB = b.Call.Material.IsAlphaTest;

            if (alphaTestA == alphaTestB)
            {
                return a.Call.Material.SortId - b.Call.Material.SortId;
            }

            return alphaTestA.CompareTo(alphaTestB);
        }

        /// <summary>Returns <see langword="true"/> if the request is a <see cref="SceneAggregate"/> with no visible children.</summary>
        public static bool IsAggregateWithNoVisibleChildren(Request req)
        {
            return req.Node is SceneAggregate { AnyChildrenVisible: false };
        }

        /// <summary>Reused scratch for the per-frame object index list; only grows, never allocates in steady state.</summary>
        private static uint[] instancingObjectIndices = new uint[4096];

        /// <summary>Combined tint of a draw as it would be packed for the vTint uniform.</summary>
        private static uint GetPackedDrawTint(RenderableMesh mesh, DrawCall call, SceneNode node)
        {
            var instanceTint = (node is SceneAggregate.Fragment fragment) ? fragment.Tint : Vector4.One;
            var tint = Vector4.Clamp(mesh.Tint * call.TintColor * instanceTint, Vector4.Zero, Vector4.One);
            return Color32.FromVector4(tint).PackedValue;
        }

        /// <summary>Whether a request can participate in an opportunistically instanced draw at all.</summary>
        private static bool IsInstanceable(in Request request)
        {
            return request.Node.Id != 0
                && request.Mesh.FlexStateManager == null // per-node morph composite textures cannot be shared
                && request.Node is not SceneAggregate // uses the sequential instance transform path
                && !request.Node.TransformBufferStale; // instanced draws read the gpu transform buffer
        }

        /// <summary>Whether two adjacent requests would issue GL-state-identical draws and can be merged into one instanced draw.</summary>
        private static bool CanJoinGroup(in Request head, in Request candidate, bool perInstanceCubemaps, bool perInstanceProbes, uint headTint)
        {
            if (candidate.Call == null || !IsInstanceable(in candidate))
            {
                return false;
            }

            // uAnimationData is a group-wide uniform; the per-instance bone offset comes from object data.
            // Instances of the same model share mesh bone layout, so this only splits mixed groups.
            if (head.Mesh.IsAnimated != candidate.Mesh.IsAnimated)
            {
                return false;
            }

            if (head.Mesh.IsAnimated
                && (head.Mesh.MeshBoneOffset != candidate.Mesh.MeshBoneOffset
                    || head.Mesh.MeshBoneCount != candidate.Mesh.MeshBoneCount
                    || head.Mesh.BoneWeightCount != candidate.Mesh.BoneWeightCount))
            {
                return false;
            }

            var a = head.Call!;
            var b = candidate.Call;

            if (!ReferenceEquals(a.Material, b.Material)
                || a.IndexBuffer.Handle != b.IndexBuffer.Handle
                || a.IndexBuffer.Offset != b.IndexBuffer.Offset
                || a.StartIndex != b.StartIndex
                || a.IndexCount != b.IndexCount
                || a.BaseVertex != b.BaseVertex
                || a.IndexType != b.IndexType
                || a.PrimitiveType != b.PrimitiveType)
            {
                return false;
            }

            // Same material implies the same input signature, so equal buffer bindings resolve to the same VAO
            var aBuffers = a.VertexBuffers;
            var bBuffers = b.VertexBuffers;

            if (aBuffers.Length != bBuffers.Length)
            {
                return false;
            }

            for (var i = 0; i < aBuffers.Length; i++)
            {
                if (aBuffers[i].Handle != bBuffers[i].Handle
                    || aBuffers[i].Offset != bBuffers[i].Offset
                    || aBuffers[i].ElementSizeInBytes != bBuffers[i].ElementSizeInBytes)
                {
                    return false;
                }
            }

            // Tint is a group-wide uniform in the object list instancing mode
            if (headTint != GetPackedDrawTint(candidate.Mesh, b, candidate.Node))
            {
                return false;
            }

            // Per-draw texture bindings must match across the group
            if (perInstanceCubemaps)
            {
                var headEnvMap = head.Node.EnvMaps.Count > 0 ? head.Node.EnvMaps[0] : null;
                var candidateEnvMap = candidate.Node.EnvMaps.Count > 0 ? candidate.Node.EnvMaps[0] : null;

                if (!ReferenceEquals(headEnvMap, candidateEnvMap))
                {
                    return false;
                }
            }

            if (perInstanceProbes && !ReferenceEquals(head.Node.LightProbeBinding, candidate.Node.LightProbeBinding))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Merges adjacent identical draws in the sorted opaque list into instanced draws. Group heads receive
        /// the instance count and an offset into the object index list, consumed members are marked skipped.
        /// The per-instance object indices are uploaded to <see cref="Scene.InstancingObjectIndicesGpu"/>.
        /// </summary>
        private static void GroupInstanceableDraws(List<Request> requests, Scene scene)
        {
            var gpuBuffer = scene.InstancingObjectIndicesGpu;

            if (gpuBuffer == null)
            {
                return;
            }

            var perInstanceCubemaps = scene.LightingInfo.CubemapType == CubemapType.IndividualCubemaps;
            var perInstanceProbes = scene.LightingInfo.LightProbeType == LightProbeType.IndividualProbes;

            var span = CollectionsMarshal.AsSpan(requests);
            var indexCount = 0;

            for (var i = 0; i < span.Length; i++)
            {
                ref var head = ref span[i];
                head.InstanceCount = 0;

                if (head.Call == null || !IsInstanceable(in head))
                {
                    continue;
                }

                var headTint = GetPackedDrawTint(head.Mesh, head.Call, head.Node);
                var groupEnd = i + 1;

                while (groupEnd < span.Length && CanJoinGroup(in head, in span[groupEnd], perInstanceCubemaps, perInstanceProbes, headTint))
                {
                    groupEnd++;
                }

                var groupCount = groupEnd - i;

                if (groupCount == 1)
                {
                    continue;
                }

                if (instancingObjectIndices.Length < indexCount + groupCount)
                {
                    Array.Resize(ref instancingObjectIndices, Math.Max(instancingObjectIndices.Length * 2, indexCount + groupCount));
                }

                head.InstanceCount = groupCount;
                head.ObjectIndexOffset = indexCount;
                instancingObjectIndices[indexCount++] = head.Node.Id;

                for (var member = i + 1; member < groupEnd; member++)
                {
                    span[member].InstanceCount = -1;
                    instancingObjectIndices[indexCount++] = span[member].Node.Id;
                }

                i = groupEnd - 1;
            }

            if (indexCount > 0)
            {
                // Orphaning upload: draws already queued against the previous contents keep their old data store
                gpuBuffer.Create(instancingObjectIndices, indexCount * sizeof(uint));
            }
        }

        /// <summary>Sorts requests according to the active render pass and issues all draw calls.</summary>
        /// <param name="requests">Draw call requests to process.</param>
        /// <param name="context">Render context describing the current pass and scene state.</param>
        public static void Render(List<Request> requests, Scene.RenderContext context)
        {
            if (context.RenderPass == RenderPass.Opaque)
            {
                requests.Sort(CompareCustomPipeline);

                if (context.ReplacementShader == null)
                {
                    GroupInstanceableDraws(requests, context.Scene);
                }
            }
            else if (context.RenderPass == RenderPass.DepthOnly)
            {
                // Shadow and depth prepass draws; the depth-only replacement shaders read
                // per-instance transforms and bones through object data like material shaders do
                requests.Sort(CompareCustomPipeline);
                GroupInstanceableDraws(requests, context.Scene);
            }
            else if (context.RenderPass == RenderPass.OpaqueAggregate)
            {
                using var _ = new GLDebugGroup("Sort Indirect Draws");
                var removed = requests.RemoveAll(IsAggregateWithNoVisibleChildren);
                requests.Sort(CompareAlphaTestThenProgram);
            }
            else if (context.RenderPass == RenderPass.StaticOverlay)
            {
                requests.Sort(CompareRenderOrderThenPipeline);
            }
            else if (context.RenderPass == RenderPass.Translucent)
            {
                requests.Sort(CompareCameraDistance);
            }

            DrawBatch(requests, context);
        }

        private ref struct Uniforms
        {
            public int AnimationData = -1;
            public int EnvmapTexture = -1;
            public int LPVIrradianceTexture = -1;
            public int Transform = -1;
            public int InstancingModeLocation = -1;
            public int Tint = -1;
            public int MeshId = -1;
            public int ShaderId = -1;
            public int ShaderProgramId = -1;
            public int MorphCompositeTexture = -1;
            public int MorphCompositeTextureSize = -1;
            public int MorphVertexIdOffset = -1;

            public Uniforms() { }
        }

        private ref struct Config
        {
            public bool NeedsCubemapBinding;
            public int LightmapGameVersionNumber;
            public bool IndirectDraw;
            public bool OpportunisticInstancing;
            public LightProbeType LightProbeType;
        }

        private static void SetInstanceTexture(Shader shader, ReservedTextureSlots slot, int location, RenderTexture texture)
        {
            shader.SetTexture((int)slot, location, texture);
        }

        private static void DrawBatch(List<Request> requests, Scene.RenderContext context)
        {
            var vao = -1;
            Shader? shader = null;
            RenderMaterial? material = null;
            Uniforms uniforms = new();
            Config config = new()
            {
                NeedsCubemapBinding = context.Scene.LightingInfo.CubemapType == CubemapType.IndividualCubemaps,
                LightmapGameVersionNumber = context.Scene.LightingInfo.LightmapGameVersionNumber,
                LightProbeType = context.Scene.LightingInfo.LightProbeType,
                IndirectDraw = context.Scene.DrawMeshletsIndirect && context.RenderPass < RenderPass.Opaque,
                OpportunisticInstancing = (context.RenderPass == RenderPass.Opaque && context.ReplacementShader == null)
                    || context.RenderPass == RenderPass.DepthOnly,
            };

            var counters = PerfStats.Active;

            foreach (var request in requests)
            {
                if (request.Call == null)
                {
                    if (context.RenderPass is RenderPass.Opaque or RenderPass.Translucent or RenderPass.Outline)
                    {
                        material?.PostRender();

                        // Custom nodes render themselves and may issue several draws internally; count them as one draw call.
                        counters.Count(Counter.DrawCall);
                        request.Node.Render(context);

                        shader = null;
                        material = null;
                        vao = -1;
                    }

                    continue;
                }

                if (config.OpportunisticInstancing && request.InstanceCount == -1)
                {
                    continue; // drawn as part of a preceding instanced group
                }

                var requestMaterial = request.Call.Material;

                if (material != requestMaterial)
                {
                    counters.Count(Counter.MaterialChange);

                    if (context.ReplacementShader?.IgnoreMaterialData != true)
                    {
                        material?.PostRender();
                    }

                    var requestShader = context.ReplacementShader ?? requestMaterial.Shader;

                    // If the material did not change, shader could not have changed
                    if (shader != requestShader)
                    {
                        shader = requestShader;
                        uniforms = new Uniforms
                        {
                            AnimationData = shader.GetUniformLocation("uAnimationData"),
                            Transform = shader.GetUniformLocation("transform"),
                            InstancingModeLocation = shader.GetUniformLocation("nInstancingMode"),
                            Tint = shader.GetUniformLocation("vTint"),
                        };

                        if (shader.Parameters.ContainsKey("S_SCENE_CUBEMAP_TYPE"))
                        {
                            uniforms.EnvmapTexture = shader.GetUniformLocation("g_tEnvironmentMap");
                        }

                        if (shader.Parameters.ContainsKey("F_MORPH_SUPPORTED"))
                        {
                            uniforms.MorphCompositeTexture = shader.GetUniformLocation("morphCompositeTexture");
                            uniforms.MorphCompositeTextureSize = shader.GetUniformLocation("morphCompositeTextureSize");
                            uniforms.MorphVertexIdOffset = shader.GetUniformLocation("morphVertexIdOffset");
                        }

                        if (shader.Parameters.ContainsKey("D_BAKED_LIGHTING_FROM_PROBE"))
                        {
                            uniforms.LPVIrradianceTexture = shader.GetUniformLocation("g_tLPV_Irradiance");
                        }

                        if (shader.Name == "vrf.picking")
                        {
                            uniforms.MeshId = shader.GetUniformLocation("meshId");
                            uniforms.ShaderId = shader.GetUniformLocation("shaderId");
                            uniforms.ShaderProgramId = shader.GetUniformLocation("shaderProgramId");
                        }

                        shader.Use();

                        if (!shader.IgnoreMaterialData)
                        {
                            foreach (var (slot, name, texture) in context.Textures)
                            {
                                shader.SetTexture((int)slot, name, texture);
                            }

                            context.Scene.LightingInfo.SetLightmapTextures(shader);
                        }

                        Debug.Assert(context.Scene.InstanceBufferGpu != null && context.Scene.TransformBufferGpu != null);
                        context.Scene.TransformBufferGpu.BindBufferBase();
                        context.Scene.InstanceBufferGpu.BindBufferBase();
                        context.Scene.InstancingObjectIndicesGpu?.BindBufferBase();

                        if (config.IndirectDraw)
                        {
                            GL.ProgramUniform1((uint)shader.Program, uniforms.InstancingModeLocation, InstancingMode.Aggregate);
                        }
                    }

                    material = requestMaterial;
                    material.Render(shader);
                }

                var requestVao = request.Call.GetVertexArrayObject(shader!);

                if (vao != requestVao)
                {
                    vao = requestVao;
                    GL.BindVertexArray(vao);
                    counters.Count(Counter.VaoChange);
                }

                var instancing = config.OpportunisticInstancing ? request.InstanceCount : 0;
                Draw(shader!, ref uniforms, ref config, new(request.Mesh, request.Call, request.Node, instancing, request.ObjectIndexOffset));
            }

            if (vao > -1)
            {
                material!.PostRender();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Draw(Shader shader, ref Uniforms uniforms, ref Config config, BatchRequest request)
        {
            if (uniforms.MeshId != -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.MeshId, (uint)request.Mesh.MeshIndex);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderId, request.Call.Material.Shader.NameHash);
                GL.ProgramUniform1((uint)shader.Program, uniforms.ShaderProgramId, (uint)request.Call.Material.Shader.Program);
            }

            if (config.IndirectDraw)
            {
                if (request.Node is SceneAggregate agg && agg.IndirectDrawCount > 0)
                {
                    // Non-indirect draws below reset this program uniform
                    if (uniforms.InstancingModeLocation > -1)
                    {
                        GL.ProgramUniform1((uint)shader.Program, uniforms.InstancingModeLocation, InstancingMode.Aggregate);
                    }

                    PerfStats.Active.CountIndirectDraw(agg.IndirectDrawCount);

                    var scene = agg.Scene;
                    if (scene.CompactMeshletDraws && agg.CompactionIndex >= 0)
                    {
                        GL.MultiDrawElementsIndirectCount(
                            request.Call.PrimitiveType,
                            request.Call.IndexType,
                            agg.IndirectDrawByteOffset,
                            agg.CompactionIndex * sizeof(uint), // drawcount buffer offset
                            agg.IndirectDrawCount, // maxdrawcount
                            0); // stride
                        return;
                    }

                    GL.MultiDrawElementsIndirect(request.Call.PrimitiveType, request.Call.IndexType, agg.IndirectDrawByteOffset, agg.IndirectDrawCount, 0);
                    return;
                }
            }

            if (config.NeedsCubemapBinding && uniforms.EnvmapTexture != -1 && request.Node.EnvMaps.Count > 0)
            {
                var envmap = request.Node.EnvMaps[0];
                SetInstanceTexture(shader, ReservedTextureSlots.EnvironmentMap, uniforms.EnvmapTexture, envmap.EnvMapTexture);
            }

            if (config.LightProbeType == LightProbeType.IndividualProbes && uniforms.LPVIrradianceTexture != -1
                && request.Node.LightProbeBinding is { } lightProbe)
            {
                request.Node.Scene.LightingInfo.SetInstanceLightProbeTextures(shader, lightProbe);
            }

            if (uniforms.AnimationData != -1)
            {
                var bAnimated = request.Mesh.IsAnimated;
                var numBones = 0u;
                var numWeights = 0u;
                var boneStart = 0u;

                if (bAnimated)
                {
                    numBones = (uint)request.Mesh.MeshBoneCount;
                    boneStart = (uint)request.Mesh.MeshBoneOffset;
                    numWeights = (uint)request.Mesh.BoneWeightCount;
                }

                GL.ProgramUniform4((uint)shader.Program, uniforms.AnimationData, bAnimated ? 1u : 0u, boneStart, numBones, numWeights);
            }

            if (uniforms.MorphVertexIdOffset != -1)
            {
                var morphComposite = request.Mesh.FlexStateManager?.MorphComposite;
                if (morphComposite != null)
                {
                    SetInstanceTexture(shader, ReservedTextureSlots.MorphCompositeTexture, uniforms.MorphCompositeTexture, morphComposite.CompositeTexture);
                    GL.ProgramUniform2(shader.Program, uniforms.MorphCompositeTextureSize, (float)morphComposite.CompositeTexture.Width, morphComposite.CompositeTexture.Height);
                }

                GL.ProgramUniform1(shader.Program, uniforms.MorphVertexIdOffset, morphComposite != null ? request.Call.VertexIdOffset : -1);
            }

            if (uniforms.Transform > -1)
            {
                var transform = request.Node.Transform.To3x4();
                GL.ProgramUniformMatrix3x4(shader.Program, uniforms.Transform, false, ref transform);
            }

            if (uniforms.Tint > -1)
            {
                var instanceTint = (request.Node is SceneAggregate.Fragment fragment) ? fragment.Tint : Vector4.One;

                // Content can author out-of-range tints (e.g. renderamt above 255 baked into the draw call
                // alpha); the packed byte color can only represent [0, 1].
                var tint = Vector4.Clamp(request.Mesh.Tint * request.Call.TintColor * instanceTint, Vector4.Zero, Vector4.One);

                GL.ProgramUniform1((uint)shader.Program, uniforms.Tint, Color32.FromVector4(tint).PackedValue);
            }

            var instanceCount = 1;
            var baseInstance = request.Node.Id;
            var instancingMode = InstancingMode.None;

            if (request.Node is SceneAggregate { InstanceTransforms.Count: > 0 } aggregate)
            {
                instanceCount = aggregate.InstanceTransforms.Count;
                instancingMode = instanceCount > 1 ? InstancingMode.Aggregate : InstancingMode.None;
            }
            else if (request.InstanceCount > 1)
            {
                instanceCount = request.InstanceCount;
                baseInstance = (uint)request.ObjectIndexOffset;
                instancingMode = InstancingMode.ObjectList;
                PerfStats.Active.Count(Counter.InstancesBatched, instanceCount);
            }

            if (uniforms.InstancingModeLocation > -1)
            {
                GL.ProgramUniform1((uint)shader.Program, uniforms.InstancingModeLocation, instancingMode);
            }

            PerfStats.Active.CountDrawCall(request.Node);

            GL.DrawElementsInstancedBaseVertexBaseInstance(
                request.Call.PrimitiveType,
                request.Call.IndexCount,
                request.Call.IndexType,
                request.Call.StartIndex,
                instanceCount,
                request.Call.BaseVertex,
                baseInstance
            );
        }
    }
}
