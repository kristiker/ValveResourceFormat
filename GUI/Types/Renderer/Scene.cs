using System.Diagnostics;
using System.Linq;
using GUI.Types.Renderer.UniformBuffers;
using GUI.Utils;

namespace GUI.Types.Renderer
{
    partial class Scene
    {
        public readonly struct UpdateContext
        {
            public float Timestep { get; }

            public UpdateContext(float timestep)
            {
                Timestep = timestep;
            }
        }

        public struct RenderContext
        {
            public GLSceneViewer View { get; init; }
            public Scene Scene { get; set; }
            public Camera Camera { get; set; }
            public Framebuffer Framebuffer { get; set; }
            public RenderPass RenderPass { get; set; }
            public Shader ReplacementShader { get; set; }
        }

        public Camera MainCamera { get; set; }
        public SceneSky Sky { get; set; }
        public WorldLightingInfo LightingInfo { get; }
        public WorldFogInfo FogInfo { get; set; } = new();
        public Dictionary<string, byte> RenderAttributes { get; } = [];
        public VrfGuiContext GuiContext { get; }
        public Octree<SceneNode> StaticOctree { get; }
        public Octree<SceneNode> DynamicOctree { get; }
        public Vector3 WorldOffset { get; set; } = Vector3.Zero;
        public float WorldScale { get; set; } = 1.0f;
        // TODO: also store skybox reference rotation

        public bool ShowToolsMaterials { get; set; }
        public bool FogEnabled { get; set; } = true;

        public IEnumerable<SceneNode> AllNodes => staticNodes.Concat(dynamicNodes);
        public uint MaxNodeId { get; private set; }

        private readonly List<SceneNode> staticNodes = [];
        private readonly List<SceneNode> dynamicNodes = [];

        public Scene(VrfGuiContext context, float sizeHint = 32768)
        {
            GuiContext = context;
            StaticOctree = new Octree<SceneNode>(sizeHint);
            DynamicOctree = new Octree<SceneNode>(sizeHint);

            LightingInfo = new(this);
        }

        public void Add(SceneNode node, bool dynamic)
        {
            if (dynamic)
            {
                dynamicNodes.Add(node);
                DynamicOctree.Insert(node, node.BoundingBox);
                node.Id = (uint)dynamicNodes.Count * 2 - 1;
            }
            else
            {
                staticNodes.Add(node);
                StaticOctree.Insert(node, node.BoundingBox);
                node.Id = (uint)staticNodes.Count * 2;
            }

            MaxNodeId = Math.Max(MaxNodeId, node.Id);
        }

        public SceneNode Find(uint id)
        {
            if (id == 0)
            {
                return null;
            }

            if (id % 2 == 1)
            {
                var index = ((int)id + 1) / 2 - 1;

                if (index >= dynamicNodes.Count)
                {
                    return null;
                }

                return dynamicNodes[index];
            }
            else
            {
                var index = (int)id / 2 - 1;

                if (index >= staticNodes.Count)
                {
                    return null;
                }

                return staticNodes[index];
            }
        }

        public void Update(float timestep)
        {
            var updateContext = new UpdateContext(timestep);

            foreach (var node in staticNodes)
            {
                node.Update(updateContext);
            }

            foreach (var node in dynamicNodes)
            {
                var oldBox = node.BoundingBox;
                node.Update(updateContext);
                DynamicOctree.Update(node, oldBox, node.BoundingBox);
            }
        }

        private readonly List<SceneNode> CullResults = [];
        private int StaticCount;
        private int LastFrustum = -1;

        public List<SceneNode> GetFrustumCullResults(Frustum frustum)
        {
            var currentFrustum = frustum.GetHashCode();
            if (LastFrustum != currentFrustum)
            {
                LastFrustum = currentFrustum;

                CullResults.Clear();
                CullResults.Capacity = staticNodes.Count + dynamicNodes.Count + 100;

                StaticOctree.Root.Query(frustum, CullResults);
                StaticCount = CullResults.Count;
            }
            else
            {
                CullResults.RemoveRange(StaticCount, CullResults.Count - StaticCount);
            }

            DynamicOctree.Root.Query(frustum, CullResults);
            return CullResults;
        }

        private readonly List<MeshBatchRenderer.Request> renderLooseNodes = [];
        private readonly List<MeshBatchRenderer.Request> renderOpaqueDrawCalls = [];
        private readonly List<MeshBatchRenderer.Request> renderStaticOverlays = [];
        private readonly List<MeshBatchRenderer.Request> renderTranslucentDrawCalls = [];

        public void CollectSceneDrawCalls(Camera camera, Frustum cullFrustum = null)
        {
            renderOpaqueDrawCalls.Clear();
            renderStaticOverlays.Clear();
            renderTranslucentDrawCalls.Clear();
            renderLooseNodes.Clear();

            cullFrustum ??= camera.ViewFrustum;
            var cullResults = GetFrustumCullResults(cullFrustum);

            // Collect mesh calls
            foreach (var node in cullResults)
            {
                if (node is IRenderableMeshCollection meshCollection)
                {
                    foreach (var mesh in meshCollection.RenderableMeshes)
                    {
                        foreach (var call in mesh.DrawCallsOpaque)
                        {
                            renderOpaqueDrawCalls.Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                Node = node,
                            });
                        }

                        foreach (var call in mesh.DrawCallsOverlay)
                        {
                            renderStaticOverlays.Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                RenderOrder = node.OverlayRenderOrder,
                                Node = node,
                            });
                        }

                        foreach (var call in mesh.DrawCallsBlended)
                        {
                            renderTranslucentDrawCalls.Add(new MeshBatchRenderer.Request
                            {
                                Transform = node.Transform,
                                Mesh = mesh,
                                Call = call,
                                DistanceFromCamera = (node.BoundingBox.Center - camera.Location).LengthSquared(),
                                Node = node,
                            });
                        }
                    }
                }
                else if (node is SceneAggregate aggregate)
                {
                }
                else if (node is SceneAggregate.Fragment fragment)
                {
                    renderOpaqueDrawCalls.Add(new MeshBatchRenderer.Request
                    {
                        Transform = fragment.Transform,
                        Mesh = fragment.RenderMesh,
                        Call = fragment.DrawCall,
                        Node = node,
                    });
                }
                else
                {
                    renderLooseNodes.Add(new MeshBatchRenderer.Request
                    {
                        DistanceFromCamera = (node.BoundingBox.Center - camera.Location).LengthSquared(),
                        Node = node,
                    });
                }
            }

            renderLooseNodes.Sort(MeshBatchRenderer.CompareCameraDistance);
        }

        public void RenderOpaqueLayer(RenderContext renderContext)
        {
            var camera = renderContext.Camera;

            renderContext.RenderPass = RenderPass.Opaque;
            MeshBatchRenderer.Render(renderOpaqueDrawCalls, renderContext);

            renderContext.RenderPass = RenderPass.StaticOverlay;
            MeshBatchRenderer.Render(renderStaticOverlays, renderContext);

            renderContext.RenderPass = RenderPass.AfterOpaque;
            foreach (var request in renderLooseNodes)
            {
                request.Node.Render(renderContext);
            }
        }

        public void RenderTranslucentLayer(RenderContext renderContext)
        {
            renderContext.RenderPass = RenderPass.Translucent;
            foreach (var request in renderLooseNodes)
            {
                request.Node.Render(renderContext);
            }

            MeshBatchRenderer.Render(renderTranslucentDrawCalls, renderContext);
        }

        public void SetEnabledLayers(HashSet<string> layers)
        {
            foreach (var renderer in AllNodes)
            {
                renderer.LayerEnabled = layers.Contains(renderer.LayerName);
            }

            UpdateOctrees();
        }

        public void UpdateOctrees()
        {
            LastFrustum = -1;
            StaticOctree.Clear();
            DynamicOctree.Clear();

            foreach (var node in staticNodes)
            {
                if (node.LayerEnabled)
                {
                    StaticOctree.Insert(node, node.BoundingBox);
                }
            }

            foreach (var node in dynamicNodes)
            {
                if (node.LayerEnabled)
                {
                    DynamicOctree.Insert(node, node.BoundingBox);
                }
            }
        }

        public void SetFogConstants(ViewConstants viewConstants)
        {
            FogInfo.SetFogUniforms(viewConstants, FogEnabled, WorldOffset, WorldScale);
        }

        public void CalculateEnvironmentMaps()
        {
            if (LightingInfo.EnvMaps.Count == 0)
            {
                return;
            }

            var firstTexture = LightingInfo.EnvMaps.First().EnvMapTexture;

            LightingInfo.LightingData.EnvMapSizeConstants = new Vector4(firstTexture.NumMipLevels - 1, firstTexture.Depth, 0, 0);

            int ArrayIndexCompare(SceneEnvMap a, SceneEnvMap b) => a.ArrayIndex.CompareTo(b.ArrayIndex);
            int HandShakeCompare(SceneEnvMap a, SceneEnvMap b) => a.HandShake.CompareTo(b.HandShake);

            LightingInfo.EnvMaps.Sort(LightingInfo.CubemapType switch
            {
                CubemapType.CubemapArray => ArrayIndexCompare,
                _ => HandShakeCompare
            });

            var i = 0;
            Span<int> envMapArrayTextureIndices = stackalloc int[LightingConstants.MAX_ENVMAPS];

            foreach (var envMap in LightingInfo.EnvMaps.OrderByDescending((envMap) => envMap.IndoorOutdoorLevel))
            {
                if (envMap.ArrayIndex >= LightingConstants.MAX_ENVMAPS || envMap.ArrayIndex < 0)
                {
                    Log.Error(nameof(WorldLoader), $"Envmap array index {i} is too large, skipping! Max: {LightingConstants.MAX_ENVMAPS}");
                    continue;
                }

                if (LightingInfo.CubemapType == CubemapType.CubemapArray)
                {
                    //Debug.Assert( == i, "Envmap array index mismatch");
                }

                UpdateGpuEnvmapData(envMap, i, envMap.ArrayIndex);
                envMapArrayTextureIndices[i] = envMap.ArrayIndex;
                i++;
            }

            foreach (var node in AllNodes)
            {
                var preComputedHandshake = node.CubeMapPrecomputedHandshake;
                SceneEnvMap preComputed = default;

                if (preComputedHandshake > 0)
                {
                    if (LightingInfo.CubemapType == CubemapType.IndividualCubemaps
                        && preComputedHandshake <= LightingInfo.EnvMaps.Count)
                    {
                        // SteamVR Home node handshake as envmap index
                        node.EnvMap = LightingInfo.EnvMaps[preComputedHandshake - 1];
                        node.EnvMapGpuDataIndex = envMapArrayTextureIndices.IndexOf(node.EnvMap.ArrayIndex);
                    }
                    else if (LightingInfo.EnvMapHandshakes.TryGetValue(preComputedHandshake, out preComputed))
                    {
                        node.EnvMap = preComputed;
                        node.EnvMapGpuDataIndex = envMapArrayTextureIndices.IndexOf(preComputed.ArrayIndex);
                    }
                    else
                    {
#if DEBUG
                        Log.Debug(nameof(Scene), $"A envmap with handshake [{preComputedHandshake}] does not exist for node at {node.BoundingBox.Center}");
#endif
                    }
                }

                var lightingOrigin = node.LightingOrigin ?? Vector3.Zero;
                if (node.LightingOrigin.HasValue)
                {
                    // in source2 this is a dynamic combo D_SPECULAR_CUBEMAP_STATIC=1, and i guess without a loop (similar to SCENE_CUBEMAP_TYPE=1)
                    //node.EnvMap = [.. node.EnvMaps.OrderBy((envMap) => Vector3.Distance(lightingOrigin, envMap.BoundingBox.Center))];
                    // todo.
                }
            }
        }

        private void UpdateGpuEnvmapData(SceneEnvMap envMap, int index, int arrayTextureIndex)
        {
            Matrix4x4.Invert(envMap.Transform, out var invertedTransform);

            LightingInfo.LightingData.EnvMapWorldToLocal[index] = invertedTransform;
            LightingInfo.LightingData.EnvMapBoxMins[index] = new Vector4(envMap.LocalBoundingBox.Min, arrayTextureIndex);
            LightingInfo.LightingData.EnvMapBoxMaxs[index] = new Vector4(envMap.LocalBoundingBox.Max, 0);
            LightingInfo.LightingData.EnvMapEdgeInvEdgeWidth[index] = new Vector4(Vector3.One / envMap.EdgeFadeDists, 0);
            LightingInfo.LightingData.EnvMapProxySphere[index] = new Vector4(envMap.Transform.Translation, envMap.ProjectionMode);
            LightingInfo.LightingData.EnvMapColorRotated[index] = new Vector4(envMap.Tint, 0);

            // TODO
            LightingInfo.LightingData.EnvMapNormalizationSH[index] = new Vector4(0, 0, 0, 1);
        }
    }
}
