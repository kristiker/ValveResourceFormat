using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using GUI.Controls;
using GUI.Types.ParticleRenderer;
using GUI.Types.Renderer.UniformBuffers;
using GUI.Utils;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat;

namespace GUI.Types.Renderer
{
    internal abstract class GLSceneViewer : GLViewerControl, IGLViewer, IDisposable
    {
        public Scene Scene { get; }
        public Scene SkyboxScene { get; protected set; }
        public VrfGuiContext GuiContext => Scene.GuiContext;

        private bool ShowBaseGrid;
        public bool ShowSkybox { get; set; } = true;
        public bool IsWireframe { get; set; }
        public bool DepthPassEnabled { get; set; } = true;

        public float Uptime { get; private set; }

        private bool showStaticOctree;
        private bool showDynamicOctree;
        private Frustum lockedCullFrustum;
        private Frustum skyboxLockedCullFrustum;

        private StorageBuffer instanceBuffer;
        private StorageBuffer transformBuffer;
        protected UniformBuffer<ViewConstants> viewBuffer;
        private UniformBuffer<LightingConstants> lightingBuffer;

        public List<(ReservedTextureSlots Slot, string Name, RenderTexture Texture)> Textures { get; } = [];

        private bool skipRenderModeChange;
        private ComboBox renderModeComboBox;
        private InfiniteGrid baseGrid;
        protected readonly Camera skyboxCamera = new();
        private OctreeDebugRenderer<SceneNode> staticOctreeRenderer;
        private OctreeDebugRenderer<SceneNode> dynamicOctreeRenderer;
        protected SelectedNodeRenderer selectedNodeRenderer;
        private Shader depthOnlyShader;

        protected GLSceneViewer(VrfGuiContext guiContext, Frustum cullFrustum) : base(guiContext)
        {
            Scene = new Scene(guiContext);
            lockedCullFrustum = cullFrustum;

            InitializeControl();
            AddWireframeToggleControl();

            GLLoad += OnLoad;

#if DEBUG
            guiContext.ShaderLoader.ShaderHotReload.ReloadShader += OnHotReload;
#endif
        }

        protected GLSceneViewer(VrfGuiContext guiContext) : base(guiContext)
        {
            Scene = new Scene(guiContext)
            {
                MainCamera = Camera
            };

            InitializeControl();
            AddCheckBox("Lock Cull Frustum", false, (v) =>
            {
                if (v)
                {
                    lockedCullFrustum = Scene.MainCamera.ViewFrustum.Clone();

                    if (SkyboxScene != null)
                    {
                        skyboxLockedCullFrustum = SkyboxScene.MainCamera.ViewFrustum.Clone();
                    }
                }
                else
                {
                    lockedCullFrustum = null;
                    skyboxLockedCullFrustum = null;
                }
            });
            AddCheckBox("Show Static Octree", showStaticOctree, (v) =>
            {
                showStaticOctree = v;

                if (showStaticOctree)
                {
                    staticOctreeRenderer.StaticBuild();
                }
            });
            AddCheckBox("Show Dynamic Octree", showDynamicOctree, (v) => showDynamicOctree = v);
            AddCheckBox("Show Tool Materials", Scene.ShowToolsMaterials, (v) =>
            {
                Scene.ShowToolsMaterials = v;

                if (SkyboxScene != null)
                {
                    SkyboxScene.ShowToolsMaterials = v;
                }
            });

            AddWireframeToggleControl();

            GLLoad += OnLoad;

#if DEBUG
            guiContext.ShaderLoader.ShaderHotReload.ReloadShader += OnHotReload;
#endif
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                instanceBuffer?.Dispose();
                transformBuffer?.Dispose();
                viewBuffer?.Dispose();
                lightingBuffer?.Dispose();

                GLPaint -= OnPaint;

#if DEBUG
                GuiContext.ShaderLoader.ShaderHotReload.ReloadShader -= OnHotReload;
#endif
            }

            base.Dispose(disposing);
        }

        protected abstract void InitializeControl();

        private void CreateBuffers()
        {
            instanceBuffer = new(0, "instance");
            transformBuffer = new(1, "transform");
            viewBuffer = new(2);
            lightingBuffer = new(3);
        }

        [System.Runtime.CompilerServices.InlineArray(SizeInBytes / 4)]
        public struct PerInstancePackedData
        {
            public const int SizeInBytes = 32;
            private uint data;

            public Color32 TintAlpha { readonly get => new(this[0]); set => this[0] = value.PackedValue; }
            public int TransformBufferIndex { readonly get => (int)this[1]; set => this[1] = (uint)value; }
            public int EnvMapIndex { readonly get => (int)this[2]; set => this[2] = (uint)value; }
            public bool CustomLightingOrigin { readonly get => (this[3] & 1) != 0; set => PackBit(ref this[3], value); }

            private static void PackBit(ref uint @uint, bool value)
            {
                @uint = (@uint & ~1u) | (value ? 1u : 0u);
            }
        }

        void UpdateInstanceBuffers()
        {
            var transformData = new List<Matrix4x4>() { Matrix4x4.Identity };

            var instanceBufferData = new PerInstancePackedData[Scene.MaxNodeId + 1];

            foreach (var node in Scene.AllNodes)
            {
                if (node.Id > Scene.MaxNodeId || node.Id < 0)
                {
                    continue;
                }

                ref var instanceData = ref instanceBufferData[node.Id];

                if (node.Transform.IsIdentity)
                {
                    instanceData.TransformBufferIndex = 0;
                }
                else
                {
                    instanceData.TransformBufferIndex = transformData.Count;
                    transformData.Add(node.Transform);
                }

                //instanceData.TintAlpha = node.TintAlpha;
                instanceData.CustomLightingOrigin = node.LightingOrigin.HasValue || Scene.LightingInfo.CubemapType == Scene.CubemapType.IndividualCubemaps;
                instanceData.EnvMapIndex = node.EnvMapGpuDataIndex;
            }

            instanceBuffer.Create(instanceBufferData, PerInstancePackedData.SizeInBytes);
            transformBuffer.Create(transformData.ToArray(), 64);
        }

        void UpdateSceneBuffersGpu(Scene scene, Camera camera)
        {
            camera.SetViewConstants(viewBuffer.Data);
            scene.SetFogConstants(viewBuffer.Data);
            viewBuffer.Update();

            lightingBuffer.Data = scene.LightingInfo.LightingData;
        }

        public virtual void PreSceneLoad()
        {
            const string vtexFileName = "ggx_integrate_brdf_lut_schlick.vtex_c";
            var assembly = Assembly.GetExecutingAssembly();

            // Load brdf lut, preferably from game.
            var brdfLutResource = GuiContext.LoadFile("textures/dev/" + vtexFileName);

            try
            {
                Stream brdfStream; // Will be used by LoadTexture, and disposed by resource

                if (brdfLutResource == null)
                {
                    brdfStream = assembly.GetManifestResourceStream("GUI.Utils." + vtexFileName);

                    brdfLutResource = new Resource() { FileName = vtexFileName };
                    brdfLutResource.Read(brdfStream);
                }

                // TODO: add annoying force clamp for lut
                Textures.Add(new(ReservedTextureSlots.BRDFLookup, "g_tBRDFLookup", GuiContext.MaterialLoader.LoadTexture(brdfLutResource)));
            }
            finally
            {
                brdfLutResource?.Dispose();
            }

            // Load default cube fog texture.
            using var cubeFogStream = assembly.GetManifestResourceStream("GUI.Utils.sky_furnace.vtex_c");
            using var cubeFogResource = new Resource() { FileName = "default_cube.vtex_c" };
            cubeFogResource.Read(cubeFogStream);

            Scene.FogInfo.DefaultFogTexture = GuiContext.MaterialLoader.LoadTexture(cubeFogResource);
        }

        public virtual void PostSceneLoad()
        {
            Scene.CalculateEnvironmentMaps();

            SkyboxScene?.CalculateEnvironmentMaps();

            UpdateInstanceBuffers();

            if (Scene.AllNodes.Any() && this is not GLWorldViewer)
            {
                var first = true;
                var bbox = new AABB();

                foreach (var node in Scene.AllNodes)
                {
                    if (first)
                    {
                        first = false;
                        bbox = node.BoundingBox;
                        continue;
                    }

                    bbox = bbox.Union(node.BoundingBox);
                }

                // If there is no bbox, LookAt will break camera, so +1 to location
                var location = new Vector3(bbox.Max.Z + 1f, 0, bbox.Max.Z) * 1.5f;

                Camera.SetLocation(location);
                Camera.LookAt(bbox.Center);
            }
            else
            {
                Camera.SetLocation(new Vector3(256));
                Camera.LookAt(new Vector3(0));
            }

            staticOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.StaticOctree, Scene.GuiContext, false);
            dynamicOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.DynamicOctree, Scene.GuiContext, true);

            SetAvailableRenderModes();
        }

        protected abstract void LoadScene();

        protected abstract void OnPicked(object sender, PickingTexture.PickingResponse pixelInfo);

        protected virtual void OnLoad(object sender, EventArgs e)
        {
            baseGrid = new InfiniteGrid(Scene);
            selectedNodeRenderer = new(Scene);

            Camera.SetViewportSize(GLControl.Width, GLControl.Height);

            Camera.Picker = new PickingTexture(Scene.GuiContext, OnPicked);
            depthOnlyShader = GuiContext.ShaderLoader.LoadShader("vrf.depth_only"); // TODO: Morph support

            CreateBuffers();

            var timer = Stopwatch.StartNew();
            PreSceneLoad();
            LoadScene();
            timer.Stop();
            Log.Debug(GetType().Name, $"Loading scene time: {timer.Elapsed}, shader variants: {GuiContext.ShaderLoader.ShaderCount}, materials: {GuiContext.MaterialLoader.MaterialCount}");

            PostSceneLoad();

            viewBuffer.Data.ClearColor = Settings.BackgroundColor;
            if (Scene.Sky is null)
            {
                MainFramebuffer.ClearColor = viewBuffer.Data.ClearColor;
            }

            GLLoad -= OnLoad;
            GLPaint += OnPaint;

            GuiContext.ClearCache();
        }


        protected virtual void OnPaint(object sender, RenderEventArgs e)
        {
            Uptime += e.FrameTime;
            viewBuffer.Data.Time = Uptime;

#if DEBUG
            const string UpdateLoop = "Update Loop";
            GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, UpdateLoop.Length, UpdateLoop);
#endif

            Scene.Update(e.FrameTime);

            if (SkyboxScene != null)
            {
                SkyboxScene.Update(e.FrameTime);

                skyboxCamera.CopyFrom(Camera);
                skyboxCamera.SetScaledProjectionMatrix();
                skyboxCamera.SetLocation(Camera.Location - SkyboxScene.WorldOffset);
            }

            selectedNodeRenderer.Update(new Scene.UpdateContext(e.FrameTime));

            Scene.CollectSceneDrawCalls(Camera, lockedCullFrustum);
            SkyboxScene?.CollectSceneDrawCalls(skyboxCamera, skyboxLockedCullFrustum);

#if DEBUG
            const string ScenesRender = "Scenes Render";
            GL.PopDebugGroup();
            GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, ScenesRender.Length, ScenesRender);
#endif

            var renderContext = new Scene.RenderContext
            {
                View = this,
                Camera = Camera,
                Framebuffer = MainFramebuffer,
            };

            if (Camera.Picker.IsActive)
            {
                renderContext.ReplacementShader = Camera.Picker.Shader;
                renderContext.Framebuffer = Camera.Picker;

                RenderScenesWithView(renderContext);
                Camera.Picker.Finish();
            }

            if (Camera.Picker.DebugShader is not null)
            {
                renderContext.ReplacementShader = Camera.Picker.DebugShader;
            }

            RenderScenesWithView(renderContext);

#if DEBUG
            const string LinesRender = "Lines Render";
            GL.PopDebugGroup();
            GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, LinesRender.Length, LinesRender);
#endif

            selectedNodeRenderer.Render(renderContext);

            if (showStaticOctree)
            {
                staticOctreeRenderer.Render();
            }

            if (showDynamicOctree)
            {
                dynamicOctreeRenderer.Render();
            }

            if (ShowBaseGrid)
            {
                baseGrid.Render(renderContext);
            }

#if DEBUG
            GL.PopDebugGroup();
#endif
        }

        private void RenderScenesWithView(Scene.RenderContext renderContext)
        {
            GL.Viewport(0, 0, renderContext.Framebuffer.Width, renderContext.Framebuffer.Height);
            renderContext.Framebuffer.Clear();

            GL.DepthRange(0.05, 1);
            UpdateSceneBuffersGpu(Scene, Camera);

            // Depth pass
            if (DepthPassEnabled)
            {
#if DEBUG
                const string DepthPass = "Depth Pass";
                GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, DepthPass.Length, DepthPass);
#endif

                GL.ColorMask(false, false, false, false);
                GL.DepthFunc(DepthFunction.Greater);

                var oldReplacementShader = renderContext.ReplacementShader;
                renderContext.ReplacementShader = depthOnlyShader;

                renderContext.Scene = Scene;
                Scene.DepthPassOpaque(renderContext);

                renderContext.ReplacementShader = oldReplacementShader;

                GL.ColorMask(true, true, true, true);
                GL.DepthFunc(DepthFunction.Gequal);

#if DEBUG
                GL.PopDebugGroup();
#endif
            }

            // TODO: check if renderpass allows wireframe mode
            if (IsWireframe)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            }

#if DEBUG
            const string MainSceneOpaqueRender = "Main Scene Opaque Render";
            GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, MainSceneOpaqueRender.Length, MainSceneOpaqueRender);
#endif

            renderContext.Scene = Scene;
            Scene.RenderOpaqueLayer(renderContext);

#if DEBUG
            GL.PopDebugGroup();
#endif

            // 3D Sky
            GL.DepthRange(0, 0.05);
            if (ShowSkybox && SkyboxScene != null)
            {
#if DEBUG
                const string SkySceneRender = "3D Sky Scene Render";
                GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, SkySceneRender.Length, SkySceneRender);
#endif

                renderContext.Camera = skyboxCamera;

                UpdateSceneBuffersGpu(SkyboxScene, skyboxCamera);
                renderContext.Scene = SkyboxScene;

                //SkyboxScene.RenderOpaqueLayer(renderContext);
                //SkyboxScene.RenderTranslucentLayer(renderContext);

                // Back to main Scene
                UpdateSceneBuffersGpu(Scene, Camera);

#if DEBUG
                GL.PopDebugGroup();
#endif
            }

            // 2D Sky
            if (Scene.Sky != null)
            {
#if DEBUG
                const string SkyRender = "2D Sky Render";
                GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, SkyRender.Length, SkyRender);
#endif

                Scene.Sky.Render(renderContext);

#if DEBUG
                GL.PopDebugGroup();
#endif
            }

            GL.DepthRange(0.05, 1);

#if DEBUG
            const string MainSceneTranslucentRender = "Main Scene Translucent Render";
            GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 1, MainSceneTranslucentRender.Length, MainSceneTranslucentRender);
#endif

            Scene.RenderTranslucentLayer(renderContext);

#if DEBUG
            GL.PopDebugGroup();
#endif

            if (IsWireframe)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }
        }

        protected void AddBaseGridControl()
        {
            ShowBaseGrid = true;

            AddCheckBox("Show Grid", ShowBaseGrid, (v) => ShowBaseGrid = v);
        }

        protected void AddWireframeToggleControl()
        {
            AddCheckBox("Show Wireframe", false, (v) => IsWireframe = v);
            AddCheckBox("Enable Depth Pass", DepthPassEnabled, (v) => DepthPassEnabled = v);
        }

        protected void AddRenderModeSelectionControl()
        {
            renderModeComboBox ??= AddSelection("Render Mode", (renderMode, _) =>
            {
                if (skipRenderModeChange)
                {
                    skipRenderModeChange = false;
                    return;
                }

                SetRenderMode(renderMode);
            });
        }

        private void SetAvailableRenderModes(bool keepCurrentSelection = false)
        {
            if (renderModeComboBox != null)
            {
                var selectedIndex = 0;
                var supportedRenderModes = Scene.AllNodes
                    .SelectMany(r => r.GetSupportedRenderModes())
                    .Concat(Camera.Picker.Shader.RenderModes)
                    .Distinct()
                    .Prepend("Default Render Mode")
                    .ToArray();

                if (keepCurrentSelection)
                {
                    selectedIndex = Array.IndexOf(supportedRenderModes, renderModeComboBox.SelectedItem.ToString());

                    if (selectedIndex < 0)
                    {
                        selectedIndex = 0;
                    }
                }

                renderModeComboBox.BeginUpdate();
                renderModeComboBox.Items.Clear();
                renderModeComboBox.Enabled = true;
                renderModeComboBox.Items.AddRange(supportedRenderModes);
                skipRenderModeChange = true;
                renderModeComboBox.SelectedIndex = selectedIndex;
                renderModeComboBox.EndUpdate();
            }
        }

        protected void SetEnabledLayers(HashSet<string> layers)
        {
            Scene.SetEnabledLayers(layers);
            SkyboxScene?.SetEnabledLayers(layers);

            staticOctreeRenderer = new OctreeDebugRenderer<SceneNode>(Scene.StaticOctree, Scene.GuiContext, false);
        }

        private void SetRenderMode(string renderMode)
        {
            var title = Program.MainForm.Text;
            Program.MainForm.Text = "Source 2 Viewer - Reloading shaders…";

            try
            {
                Camera?.Picker.SetRenderMode(renderMode);
                Scene.Sky?.SetRenderMode(renderMode);
                selectedNodeRenderer.SetRenderMode(renderMode);

                foreach (var node in Scene.AllNodes)
                {
                    node.SetRenderMode(renderMode);
                }

                if (SkyboxScene != null)
                {
                    foreach (var node in SkyboxScene.AllNodes)
                    {
                        node.SetRenderMode(renderMode);
                    }
                }
            }
            finally
            {
                Program.MainForm.Text = title;
            }
        }

        protected override void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyData == Keys.Delete)
            {
                selectedNodeRenderer.DisableSelectedNodes();
                return;
            }

            base.OnKeyDown(sender, e);
        }

#if DEBUG
        private void OnHotReload(object sender, string e)
        {
            if (renderModeComboBox != null)
            {
                SetAvailableRenderModes(true);
            }

            foreach (var node in Scene.AllNodes)
            {
                node.UpdateVertexArrayObjects();
            }

            if (SkyboxScene != null)
            {
                foreach (var node in SkyboxScene.AllNodes)
                {
                    node.UpdateVertexArrayObjects();
                }
            }
        }
#endif
    }
}
