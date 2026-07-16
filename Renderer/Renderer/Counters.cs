using System.Diagnostics;
using System.Text;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.SceneEnvironment;
using ValveResourceFormat.Renderer.SceneNodes;

namespace ValveResourceFormat.Renderer;

/// <summary>Scalar per-frame counters incremented via <see cref="Counters.Count"/>.</summary>
internal enum Counter
{
    SceneObjectsInView,
    DrawCalls,
    MeshletDispatches,
    MaterialChanges,
    DirectionalShadowMaps,
    BarnShadowMaps,
    ShadowFacesSubmitted,
    ParticleSystems,
    ParticleDraws,
}

/// <summary>
/// Collects per frame rendering statistics (draw calls, triangles, lights, etc).
/// </summary>
public class Counters
{
    private enum LightGroup
    {
        Omni,
        Spot,
        Barn,
        Rect,
        Environment,
    }

    private static readonly string[] LightGroupNames = ["omni", "spot", "barn", "rect", "directional"];

    // Declared after LightGroupNames so the instance created here sees it initialized (static initializers run in textual order).
    /// <summary>Counters for the frame currently being rendered. Always non-null; collects nothing while <see cref="Capture"/> is off.</summary>
    internal static Counters Active { get; private set; } = new();

    /// <summary>Gets or sets whether statistics are actively collected this frame.</summary>
    public bool Capture { get; set; }

    // Counting is suspended (nested) around passes that should not contribute to stats: shadows, depth prepass, picking.
    private int suspendDepth;
    private bool Counting => Capture && suspendDepth == 0;

    // Per-frame scalar counters, indexed by Counter.
    private readonly int[] counts = new int[Enum.GetValues<Counter>().Length];
    private readonly int[] lightsInView = new int[LightGroupNames.Length];
    private readonly int[] staticLightsInView = new int[LightGroupNames.Length];

    // Fraction (0-1) of the barn shadow atlas texture occupied by binned faces this frame.
    private float barnShadowAtlasUsage;

    // A single GPU primitives-generated query wraps the whole frame, so the triangle count includes GPU-culled
    // indirect draws and particles. Two query objects are ping-ponged so the previous frame's result can be read
    // back without blocking (one frame of latency).
    private readonly int[] triangleQueries = [0, 0];
    private int triangleQueryWrite;
    private long trianglesRendered;

    // Cached scene totals, recomputed once per second
    private long totalTriangles;
    private int totalDrawCalls;
    private int totalSceneObjects;
    private int totalMaterials;
    private int totalParticleSystems;
    private readonly int[] totalLights = new int[LightGroupNames.Length];
    private readonly int[] totalStaticLights = new int[LightGroupNames.Length];
    private long lastTotalsUpdate;

    /// <summary>Suspends stat collection for the following draws until <see cref="ResumeCounting"/> is called. Nestable.</summary>
    internal void SuspendCounting() => suspendDepth++;

    /// <summary>Resumes stat collection suspended by <see cref="SuspendCounting"/>.</summary>
    internal void ResumeCounting() => suspendDepth--;

    /// <summary>Increments a scalar counter.</summary>
    internal void Count(Counter counter, int amount = 1)
    {
        if (!Counting)
        {
            return;
        }

        counts[(int)counter] += amount;
    }

    /// <summary>Records the fraction (0-1) of the barn shadow atlas occupied by binned faces this frame.</summary>
    internal void SetBarnShadowAtlasUsage(float fraction)
    {
        if (!Counting)
        {
            return;
        }

        barnShadowAtlasUsage = fraction;
    }

    /// <summary>Counts a direct GL draw call for the given node.</summary>
    internal void CountDrawCall(SceneNode node)
    {
        if (!Counting)
        {
            return;
        }

        counts[(int)Counter.DrawCalls]++;
    }

    /// <summary>Counts an indirect multi-draw submission of an aggregate's meshlets. The meshlet count is as submitted, before GPU culling; triangles are measured by the frame primitive query instead.</summary>
    internal void CountIndirectDraw(SceneAggregate aggregate)
    {
        if (!Counting)
        {
            return;
        }

        counts[(int)Counter.MeshletDispatches] += aggregate.IndirectDrawCount;
    }

    /// <summary>Counts a light that passed frustum culling this frame.</summary>
    internal void CountLightInView(SceneLight light)
    {
        if (!Counting)
        {
            return;
        }

        if (SceneLight.IsRealTimeLight(light))
        {
            lightsInView[(int)GetLightGroup(light)]++;
        }
        else
        {
            staticLightsInView[(int)GetLightGroup(light)]++;
        }
    }

    private static LightGroup GetLightGroup(SceneLight light) => light.Entity switch
    {
        SceneLight.EntityType.Omni or SceneLight.EntityType.Omni2 => LightGroup.Omni,
        SceneLight.EntityType.Spot => LightGroup.Spot,
        SceneLight.EntityType.Barn => LightGroup.Barn,
        SceneLight.EntityType.Rect => LightGroup.Rect,
        _ => LightGroup.Environment,
    };

    private void UpdateTotals(Scene scene, Scene? skyboxScene)
    {
        if (lastTotalsUpdate != 0 && Stopwatch.GetElapsedTime(lastTotalsUpdate).TotalSeconds < 1.0)
        {
            return;
        }

        lastTotalsUpdate = Stopwatch.GetTimestamp();

        totalTriangles = 0;
        totalDrawCalls = 0;
        totalSceneObjects = 0;
        totalParticleSystems = 0;
        Array.Clear(totalLights);
        Array.Clear(totalStaticLights);

        AccumulateTotals(scene);

        if (skyboxScene != null)
        {
            AccumulateTotals(skyboxScene);
        }

        totalMaterials = scene.RendererContext.MaterialLoader.MaterialCount;
    }

    private void AccumulateTotals(Scene scene)
    {
        foreach (var node in scene.AllNodes)
        {
            totalSceneObjects++;

            switch (node)
            {
                case SceneAggregate.Fragment:
                    break; // draw calls are shared with and counted by the parent aggregate

                case SceneAggregate aggregate:
                    {
                        var instanceCount = Math.Max(1, aggregate.InstanceTransforms.Count);

                        foreach (var drawCall in aggregate.RenderMesh.DrawCalls)
                        {
                            totalDrawCalls++;
                            totalTriangles += (long)(drawCall.IndexCount / 3) * instanceCount;
                        }

                        break;
                    }

                case MeshCollectionNode meshCollection:
                    {
                        foreach (var mesh in meshCollection.RenderableMeshes)
                        {
                            foreach (var drawCall in mesh.DrawCalls)
                            {
                                totalDrawCalls++;
                                totalTriangles += drawCall.IndexCount / 3;
                            }
                        }

                        break;
                    }

                case SceneLight light:
                    if (SceneLight.IsRealTimeLight(light))
                    {
                        totalLights[(int)GetLightGroup(light)]++;
                    }
                    else
                    {
                        totalStaticLights[(int)GetLightGroup(light)]++;
                    }
                    break;

                case ParticleSceneNode:
                    totalParticleSystems++;
                    break;

                default:
                    break;
            }
        }
    }

    private static string FormatLightCounts(int[] counts, int[] visibleGroups)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < counts.Length; i++)
        {
            if (visibleGroups[i] == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(counts[i]);
            sb.Append(' ');
            sb.Append(LightGroupNames[i]);
        }

        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>
    /// Renders the collected statistics to screen using the provided text renderer.
    /// </summary>
    /// <param name="textRenderer">Text renderer to use for display.</param>
    /// <param name="camera">Camera for positioning text.</param>
    /// <param name="scene">Main scene, used to compute map totals.</param>
    /// <param name="skyboxScene">Optional 3D skybox scene, included in map totals.</param>
    /// <param name="x">X position (0-1 as fraction of screen width).</param>
    /// <param name="y">Y position (0-1 as fraction of screen height).</param>
    /// <param name="scale">Text scale.</param>
    public void DisplayStats(TextRenderer textRenderer, Camera camera, Scene scene, Scene? skyboxScene, float x = 0.02f, float y = 0.05f, float scale = 11f)
    {
        if (!Capture)
        {
            return;
        }

        UpdateTotals(scene, skyboxScene);

        var lineHeight = scale * 1.5f / camera.WindowSize.Y;
        var valueColor = new Color32(150, 255, 150);
        var offset = y;

        void AddLine(string text, Color32 color)
        {
            textRenderer.AddTextRelative(new TextRenderer.TextRenderRequest
            {
                X = x,
                Y = offset,
                Scale = scale,
                Color = color,
                Text = text,
            }, camera);

            offset += lineHeight;
        }

        AddLine("Render Stats", new Color32(255, 200, 0));

        AddLine($"Triangles:        rendered {trianglesRendered:N0} of {totalTriangles:N0}", valueColor);
        AddLine($"Scene objects:    drawn {counts[(int)Counter.SceneObjectsInView]:N0} of {totalSceneObjects:N0} scene objects in {counts[(int)Counter.DrawCalls]:N0} draw calls and {counts[(int)Counter.MeshletDispatches]:N0} meshlet dispatches ({totalDrawCalls:N0} total draw calls)", valueColor);
        AddLine($"Materials:        {counts[(int)Counter.MaterialChanges]:N0} changes between drawcalls, {totalMaterials:N0} total materials in scene", valueColor);
        AddLine($"Dynamic Lights:   in view {FormatLightCounts(lightsInView, totalLights)} out of total {FormatLightCounts(totalLights, totalLights)}", valueColor);
        AddLine($"Static Lights:    in view {FormatLightCounts(staticLightsInView, totalStaticLights)} out of total {FormatLightCounts(totalStaticLights, totalStaticLights)}", valueColor);
        AddLine($"Shadow maps:      {counts[(int)Counter.DirectionalShadowMaps]:N0} directional, {counts[(int)Counter.BarnShadowMaps]:N0} barn, {counts[(int)Counter.ShadowFacesSubmitted]:N0} faces binned, {barnShadowAtlasUsage:0%} atlas utilization", valueColor);
        AddLine($"Particle Systems: {counts[(int)Counter.ParticleSystems]:N0} particle systems rendered in {counts[(int)Counter.ParticleDraws]:N0} draw calls out of {totalParticleSystems:N0} total particle systems", valueColor);
    }

    /// <summary>Resets per-frame counters, reads back the previous frame's triangle count, and begins this frame's triangle query.</summary>
    public void MarkFrameBegin()
    {
        Active = this;

        if (!Capture)
        {
            return;
        }

        suspendDepth = 0;
        Array.Clear(counts);
        Array.Clear(lightsInView);
        Array.Clear(staticLightsInView);
        barnShadowAtlasUsage = 0;

        // Read back the query that ended last frame without blocking. If the GPU hasn't finished it yet, keep the
        // previous value rather than stomping it to zero (QueryResultNoWait leaves the out param unmodified when not ready).
        var lastQuery = triangleQueries[triangleQueryWrite];
        if (lastQuery != 0)
        {
            GL.GetQueryObject(lastQuery, GetQueryObjectParam.QueryResultAvailable, out long available);
            if (available != 0)
            {
                GL.GetQueryObject(lastQuery, GetQueryObjectParam.QueryResult, out long result);
                trianglesRendered = result;
            }
        }

        // Begin this frame's query on the other buffer.
        triangleQueryWrite = 1 - triangleQueryWrite;
        if (triangleQueries[triangleQueryWrite] == 0)
        {
            triangleQueries[triangleQueryWrite] = GL.GenQuery();
        }

        GL.BeginQuery(QueryTarget.PrimitivesGenerated, triangleQueries[triangleQueryWrite]);
    }

    /// <summary>Ends this frame's triangle query.</summary>
    public void MarkFrameEnd()
    {
        if (!Capture)
        {
            return;
        }

        GL.EndQuery(QueryTarget.PrimitivesGenerated);
    }

    /// <summary>
    /// Releases resources.
    /// </summary>
    public void Dispose()
    {
        for (var i = 0; i < triangleQueries.Length; i++)
        {
            if (triangleQueries[i] != 0)
            {
                GL.DeleteQuery(triangleQueries[i]);
                triangleQueries[i] = 0;
            }
        }
    }
}
