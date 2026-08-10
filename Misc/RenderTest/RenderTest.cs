using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Audio;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.Renderer.Input;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.Renderer.Utils;
using ValveResourceFormat.Renderer.World;
using Vector2 = System.Numerics.Vector2;
using Vector2i = OpenTK.Mathematics.Vector2i;

/// <summary>
/// Minimal standalone map viewer, used to test the renderer without the WinForms GUI around it.
/// It mirrors what <c>GLWorldViewer</c> sets up: world physics, the first person viewmodel,
/// map triggers, and the same set of layers enabled by default.
/// </summary>
internal sealed class RenderTestWindow : GameWindow
{
    private const int RequestedMsaaSamples = 4;

    /// <summary>Keys forwarded to <see cref="UserInput"/>, matching the GUI's key map.</summary>
    private static readonly (Keys Key, TrackedKeys Tracked)[] KeyMap =
    [
        (Keys.W, TrackedKeys.W),
        (Keys.A, TrackedKeys.A),
        (Keys.S, TrackedKeys.S),
        (Keys.D, TrackedKeys.D),
        (Keys.Up, TrackedKeys.W),
        (Keys.Down, TrackedKeys.S),
        (Keys.Left, TrackedKeys.A),
        (Keys.Right, TrackedKeys.D),
        (Keys.Q, TrackedKeys.Q),
        (Keys.Z, TrackedKeys.Z),
        (Keys.X, TrackedKeys.X),
        (Keys.F, TrackedKeys.F),
        (Keys.Space, TrackedKeys.Space),
        (Keys.D1, TrackedKeys.Slot1),
        (Keys.D2, TrackedKeys.Slot2),
        (Keys.D3, TrackedKeys.Slot3),
        (Keys.LeftShift, TrackedKeys.Shift),
        (Keys.RightShift, TrackedKeys.Shift),
        (Keys.LeftAlt, TrackedKeys.Alt),
        (Keys.RightAlt, TrackedKeys.Alt),
        (Keys.LeftControl, TrackedKeys.Control),
        (Keys.RightControl, TrackedKeys.Control),
    ];

    private readonly RendererContext rendererContext;
    private readonly Renderer sceneRenderer;
    private readonly UserInput input;
    private readonly TextRenderer textRenderer;
    private readonly ILogger logger;

    private Framebuffer? mainFramebuffer;
    private Framebuffer? defaultFramebuffer;
    private SoundEventPlayer? soundPlayer;
    private int numSamples;

    private Scene Scene => sceneRenderer.Scene;

    private bool isFullscreen;
    private bool isCursorLocked;
    /// <summary>Set when the cursor is grabbed, the first delta after that is the jump to the window center.</summary>
    private bool ignoreNextMouseDelta;
    private Vector2i windowedSize;
    private Vector2i windowedPosition;

    /// <summary>Wheel flags are one shot events, collected here until the next input tick consumes them.</summary>
    private TrackedKeys pendingWheelKeys;

    private readonly TextRenderer.TextBuffer fpsText = new("FPS: 10000  Frame: 10000.0ms");
    private readonly TextRenderer.TextBuffer speedText = new("Speed: 100000.0 u/s");
    private double fpsUpdateTimer;
    private int framesSinceFpsUpdate;

    public RenderTestWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings, RendererContext rendererContext)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        this.rendererContext = rendererContext;
        logger = rendererContext.Logger;

        sceneRenderer = new Renderer(rendererContext);
        input = new UserInput(sceneRenderer);
        textRenderer = new TextRenderer(rendererContext, sceneRenderer.Camera);
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GLEnvironment.Initialize(logger);
        GLEnvironment.SetDefaultRenderState();

        numSamples = Math.Clamp(RequestedMsaaSamples, 1, GL.GetInteger(GetPName.MaxSamples));
        defaultFramebuffer = Framebuffer.GLDefaultFramebuffer;
        defaultFramebuffer.Resize(ClientSize.X, ClientSize.Y);

        mainFramebuffer = Framebuffer.Prepare(nameof(mainFramebuffer), ClientSize.X, ClientSize.Y, numSamples,
            new(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
            Framebuffer.DepthAttachmentFormat.Depth32FStencil8);

        var status = mainFramebuffer.Initialize();

        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"Framebuffer failed to initialize: {status}");
        }

        mainFramebuffer.ClearMask |= ClearBufferMask.StencilBufferBit;
        mainFramebuffer.Bind(FramebufferTarget.Framebuffer);

        textRenderer.Load();
        sceneRenderer.Postprocess.Load(numSamples);
        sceneRenderer.Initialize();
        sceneRenderer.MainFramebuffer = mainFramebuffer;
        sceneRenderer.LoadRendererResources();

        var timer = Stopwatch.StartNew();
        LoadScene();
        timer.Stop();

        logger.LogInformation("Loaded scene in {Elapsed}, shader variants: {Shaders}, materials: {Materials}",
            timer.Elapsed, rendererContext.ShaderLoader.ShaderCount, rendererContext.MaterialLoader.MaterialCount);

        Scene.Initialize();
        sceneRenderer.SkyboxScene?.Initialize();

        // Walk mode (toggled with X) needs the collision world the map physics were loaded into.
        input.PhysicsWorld = Scene.PhysicsWorld;

        if (GLEnvironment.SlowMultiDrawIndirect)
        {
            Scene.EnableIndirectDraws = false;
        }

        sceneRenderer.ViewBuffer!.Data!.ExperimentalLightsEnabled = true;

        PrewarmDrawCalls();

        // Only start the ambience once loading is over, otherwise the first chunks play into the
        // load stall and the fade-in is spent on a frozen window.
        soundPlayer?.StartMapSoundEvents(Scene);
        soundPlayer?.Suspended = false;

        SetCursorLocked(true);
    }

    /// <summary>
    /// Brings up sound playback. Called before any model loads so their animation clips can
    /// pre-cache sound events. Leaves <see cref="soundPlayer"/> null when there is no output device.
    /// </summary>
    private void InitializeSoundPlayer()
    {
        IAudioDevice device;

        try
        {
            // The player takes ownership of the device and disposes it in its own Dispose (called from
            // ours); CA2000 cannot see ownership transfer through the constructor, so this is not a leak.
#pragma warning disable CA2000
            device = new OpenALAudioDevice();
#pragma warning restore CA2000
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException)
        {
            // No audio hardware, or a headless/remote session: run without sound rather than failing.
            logger.LogWarning(e, "No audio device available, sound playback disabled");
            return;
        }

        soundPlayer = new SoundEventPlayer(rendererContext.FileLoader, device, logger);
        soundPlayer.LoadSoundEvents();
        soundPlayer.LoadSoundscapes();

        soundPlayer.Suspended = true; // start with a fade-in
        soundPlayer.Volume = 0.5f; // the GUI's default, this has no settings to read one from
        soundPlayer.MixGroupVolume["Weapons"] = 0.7f;
        soundPlayer.MixGroupVolume["Foley"] = 0.5f;
        soundPlayer.MixGroupVolume["Footsteps"] = 0.4f;
        soundPlayer.MixGroupVolume["PlayerDamage"] = 0.4f;
        soundPlayer.DefaultMixGroupVolume = 0.1f;
    }

    private void LoadScene()
    {
        InitializeSoundPlayer();

        var vpk = rendererContext.FileLoader.CurrentPackage;
        Debug.Assert(vpk?.Entries != null);

        if (!vpk.Entries.TryGetValue("vmap_c", out var vmaps))
        {
            throw new InvalidOperationException("This vpk has no vmap_c file");
        }

        var loadedMap = WorldLoader.LoadMap(vmaps[0].GetFullPath(), Scene);

        sceneRenderer.SkyboxScene = loadedMap.SkyboxScene;
        sceneRenderer.Skybox2D = loadedMap.Skybox2D;

        NavMeshSceneNode.AddNavNodesToScene(loadedMap.NavMesh, Scene);
        CS2BombDamageSceneNode.AddBakedBombDamageToScene(loadedMap.BombDamage, Scene);

        // Without this every layer is visible, including debug ones the GUI keeps off
        // by default such as the visibility clusters and the navigation mesh.
        Scene.SetEnabledLayers(loadedMap.DefaultEnabledLayers);
        sceneRenderer.SkyboxScene?.SetEnabledLayers(loadedMap.DefaultEnabledLayers);

        input.TryLoadViewmodel(Scene);
        input.TriggerVolumes.AddRange(TriggerTeleport.LoadAll(loadedMap, rendererContext.FileLoader));

        ConfigureMovement(loadedMap);

        if (loadedMap.SpawnCameraMatrix is { } spawn)
        {
            input.Camera.SetFromTransformMatrix(spawn);
        }
        else if (loadedMap.CameraMatrices.Count > 0)
        {
            input.Camera.SetFromTransformMatrix(loadedMap.CameraMatrices[0]);
        }
        else
        {
            input.Camera.SetLocation(new Vector3(256));
            input.Camera.LookAt(Vector3.Zero);
        }
    }

    /// <summary>
    /// Movement maps (bhop, surf, kz, deathrun) are played with the old air acceleration and
    /// auto bunny hop, competitive maps are not. Same heuristic the GUI uses.
    /// </summary>
    private void ConfigureMovement(WorldLoader loadedMap)
    {
        string[] kzMapPrefixes = ["bhop", "surf", "kz", "dr"];
        var mapName = Path.GetFileName(loadedMap.MapName);
        var isKzMap = kzMapPrefixes.Any(prefix => mapName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        input.PlayerMovement.PrestrafeEnabled = isKzMap;
        input.PlayerMovement.AutoBunnyHop = isKzMap;
        input.PlayerMovement.AirAccelerate = isKzMap
            ? PlayerMovement.AirAccelerateMovementMaps
            : PlayerMovement.AirAccelerateCompetitive;
    }

    /// <summary>
    /// Renders one frame with culling disabled so the driver specializes every shader variant
    /// once, instead of stuttering the first time each one becomes visible.
    /// </summary>
    private void PrewarmDrawCalls()
    {
        rendererContext.ShaderLoader.LinkLoadedShaders();
        sceneRenderer.DisableAllCulling = true;

        try
        {
            UpdateScene(0f);
            RenderScene();

            foreach (var particleNode in Scene.AllNodes.OfType<ParticleSceneNode>())
            {
                particleNode.Prewarm(sceneRenderer.Camera);
            }
        }
        finally
        {
            sceneRenderer.DisableAllCulling = false;
        }
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        var keyboard = KeyboardState;

        if (keyboard.IsKeyPressed(Keys.Escape))
        {
            if (isFullscreen)
            {
                SetFullscreen(false);
            }
            else if (isCursorLocked)
            {
                SetCursorLocked(false);
            }
            else
            {
                Close();
            }

            return;
        }

        if (keyboard.IsKeyPressed(Keys.F11))
        {
            SetFullscreen(!isFullscreen);
        }

        var trackedKeys = pendingWheelKeys;
        pendingWheelKeys = TrackedKeys.None;

        foreach (var (key, tracked) in KeyMap)
        {
            if (keyboard.IsKeyDown(key))
            {
                trackedKeys |= tracked;
            }
        }

        if (isCursorLocked)
        {
            if (MouseState.IsButtonDown(MouseButton.Left))
            {
                trackedKeys |= TrackedKeys.MouseLeft;
            }

            if (MouseState.IsButtonDown(MouseButton.Right))
            {
                trackedKeys |= TrackedKeys.MouseRight;
            }
        }

        var mouseDelta = isCursorLocked && !ignoreNextMouseDelta
            ? new Vector2(MouseState.Delta.X, MouseState.Delta.Y)
            : Vector2.Zero;

        ignoreNextMouseDelta = false;

        // Long frames (loading, alt-tab) would otherwise teleport the player through the world.
        var frameTime = MathF.Min(1f, (float)args.Time);

        input.Tick(frameTime, trackedKeys, mouseDelta, sceneRenderer.Camera);

        soundPlayer?.Update(sceneRenderer.Camera);

        UpdateScene(frameTime);
    }

    private void UpdateScene(float frameTime)
    {
        sceneRenderer.Update(new Scene.UpdateContext
        {
            Camera = sceneRenderer.Camera,
            TextRenderer = textRenderer,
            Timestep = frameTime,
        });
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        framesSinceFpsUpdate++;
        fpsUpdateTimer += args.Time;

        if (fpsUpdateTimer >= 0.1)
        {
            fpsText.Format($"FPS: {framesSinceFpsUpdate / fpsUpdateTimer,-3:0}  Frame: {fpsUpdateTimer / framesSinceFpsUpdate * 1000d,-4:0.0}ms");
            framesSinceFpsUpdate = 0;
            fpsUpdateTimer = 0;
        }

        RenderScene();

        SwapBuffers();
    }

    private void RenderScene()
    {
        Debug.Assert(mainFramebuffer != null);
        Debug.Assert(defaultFramebuffer != null);

        sceneRenderer.Render(mainFramebuffer);
        sceneRenderer.PostprocessRender(mainFramebuffer, defaultFramebuffer);

        textRenderer.AddText(new TextRenderer.TextRenderRequest
        {
            X = 2f,
            Y = mainFramebuffer.Height - 4f,
            Scale = 14f,
            Color = Color32.White,
            Text = fpsText,
        });

        if (!input.NoClip)
        {
            textRenderer.AddTextRelative(new TextRenderer.TextRenderRequest
            {
                X = 0.5f,
                Y = 0.85f,
                Scale = 12f,
                Color = Color32.Yellow,
                Text = speedText.Format($"Speed: {input.Velocity.AsVector2().Length():0.0} u/s"),
                CenterHorizontal = true,
            }, sceneRenderer.Camera);
        }

        textRenderer.Render(sceneRenderer.Camera, sceneRenderer.ResolvedSceneDepth);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (e.OffsetY > 0)
        {
            pendingWheelKeys |= TrackedKeys.MouseWheelUp;
        }
        else if (e.OffsetY < 0)
        {
            pendingWheelKeys |= TrackedKeys.MouseWheelDown;
        }

        // In walk mode the wheel is a jump bind instead, handled through the tracked keys above.
        if (input.NoClip)
        {
            input.OnMouseWheel(e.OffsetY);
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (!isCursorLocked)
        {
            SetCursorLocked(true);
        }
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);

        if (e.Width <= 0 || e.Height <= 0)
        {
            return;
        }

        GL.Viewport(0, 0, e.Width, e.Height);
        sceneRenderer.Camera.SetViewportSize(e.Width, e.Height);
        defaultFramebuffer?.Resize(e.Width, e.Height);
        mainFramebuffer?.Resize(e.Width, e.Height, numSamples);
    }

    private void SetCursorLocked(bool locked)
    {
        CursorState = locked ? CursorState.Grabbed : CursorState.Normal;
        isCursorLocked = locked;
        ignoreNextMouseDelta = locked;
    }

    private void SetFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            windowedSize = ClientSize;
            windowedPosition = Location;

            var monitor = Monitors.GetMonitorFromWindow(this);
            WindowBorder = WindowBorder.Hidden;
            WindowState = WindowState.Normal;
            Location = new Vector2i(monitor.ClientArea.Min.X, monitor.ClientArea.Min.Y);
            ClientSize = new Vector2i(monitor.ClientArea.Size.X, monitor.ClientArea.Size.Y);
        }
        else
        {
            WindowBorder = WindowBorder.Resizable;
            WindowState = WindowState.Normal;
            ClientSize = windowedSize;
            Location = windowedPosition;
        }

        isFullscreen = fullscreen;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stops the mixing thread before the scene it reads sound positions from goes away
            soundPlayer?.Dispose();
            sceneRenderer.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class Program
{
    private static readonly Dictionary<int, string> GamesAndMaps = new()
    {
        { 730, "game/csgo/maps/de_dust2.vpk" },
        { 570, "game/dota/maps/dota.vpk" },
        { 546560, "game/hlvr/maps/a2_train_yard.vpk" },
        { 1422450, "game/citadel/maps/dl_hideout.vpk" },
        { 1902490, "game/steampal/maps/aperture_desk_job.vpk" },
    };

    private static int Main()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options => options.SingleLine = true);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger("RenderTest");
        var mapVpk = FindMap(logger);

        if (mapVpk == null)
        {
            logger.LogError("Failed to find any supported Source 2 game. Tried AppIDs: {AppIds}", string.Join(", ", GamesAndMaps.Keys));
            return 1;
        }

        using var vpk = new Package();
        vpk.Read(mapVpk);

        using var fileLoader = new GameFileLoader(vpk, mapVpk);
        using var rendererContext = new RendererContext(fileLoader, logger);

        var gameWindowSettings = new GameWindowSettings
        {
            UpdateFrequency = 0,
        };

        var nativeWindowSettings = new NativeWindowSettings
        {
            APIVersion = GLEnvironment.RequiredVersion,
            Vsync = VSyncMode.Adaptive,
            ClientSize = new Vector2i(1280, 720),
            WindowBorder = WindowBorder.Resizable,
            WindowState = WindowState.Normal,
            Title = "S2V Render Test",
            Flags = ContextFlags.ForwardCompatible,
            Profile = ContextProfile.Core,
        };

        using var window = new RenderTestWindow(gameWindowSettings, nativeWindowSettings, rendererContext);
        window.Run();

        return 0;
    }

    private static string? FindMap(ILogger logger)
    {
        foreach (var (appId, mapPath) in GamesAndMaps)
        {
            if (GameFolderLocator.FindSteamGameByAppId(appId) is not { } gamePath)
            {
                continue;
            }

            var potentialMapPath = Path.Join(gamePath.GamePath, mapPath);

            if (!File.Exists(potentialMapPath))
            {
                continue;
            }

            logger.LogInformation("Found map: {MapPath} for {GameName} (AppID: {AppId})", potentialMapPath, gamePath.AppName, appId);
            return potentialMapPath;
        }

        return null;
    }
}
