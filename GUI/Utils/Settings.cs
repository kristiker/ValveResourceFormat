using System.IO;
using ValveKeyValue;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.Utils;

namespace GUI.Utils
{
    /// <summary>
    /// Manages application settings.
    /// </summary>
    static class Settings
    {
        private const int SettingsFileCurrentVersion = 16;
        private const int RecentFilesLimit = 20;

        /// <summary>
        /// Flags that control quick file preview behavior in the file explorer.
        /// </summary>
        [Flags]
        public enum QuickPreviewFlags : int
        {
            /// <summary>Quick preview is enabled.</summary>
            Enabled = 1 << 0,
            /// <summary>Sounds are automatically played when previewing audio files.</summary>
            AutoPlaySounds = 1 << 1,
        }

        /// <summary>
        /// Holds state related to automatic application update checks.
        /// </summary>
        public class AppUpdateState
        {
            /// <summary>Gets or sets whether to automatically check for updates on startup.</summary>
            public bool CheckAutomatically { get; set; }
            /// <summary>Gets or sets whether a newer version of the application is available.</summary>
            public bool UpdateAvailable { get; set; }
            /// <summary>Gets or sets the timestamp of the last update check.</summary>
            public string LastCheck { get; set; } = string.Empty;
            /// <summary>Gets or sets the application version recorded the last time settings were loaded, used to detect version changes and reset update state.</summary>
            public string Version { get; set; } = string.Empty;
        }

        /// <summary>
        /// Represents the full set of persisted application configuration values.
        /// </summary>
        public class AppConfig
        {
            /// <summary>Gets or sets the list of game content search paths.</summary>
            public List<string> GameSearchPaths { get; set; } = [];
            /// <summary>Gets or sets the last directory used when opening files.</summary>
            public string OpenDirectory { get; set; } = string.Empty;
            /// <summary>Gets or sets the last directory used when saving files.</summary>
            public string SaveDirectory { get; set; } = string.Empty;
            /// <summary>Gets or sets the list of bookmarked file paths.</summary>
            public List<string> BookmarkedFiles { get; set; } = [];
            /// <summary>Gets or sets the list of recently opened file paths.</summary>
            public List<string> RecentFiles { get; set; } = [];
            /// <summary>Gets or sets saved camera positions keyed by name.</summary>
            public Dictionary<string, float[]> SavedCameras { get; set; } = [];
            /// <summary>Gets or sets the selected UI theme index.</summary>
            public int Theme { get; set; }
            /// <summary>Gets or sets the maximum texture resolution loaded by the renderer.</summary>
            public int MaxTextureSize { get; set; }
            /// <summary>Gets or sets the shadow map resolution.</summary>
            public int ShadowResolution { get; set; }
            /// <summary>Gets or sets the camera field of view in degrees.</summary>
            public float FieldOfView { get; set; }
            /// <summary>Gets or sets the first-person viewmodel field of view in degrees.</summary>
            public float ViewmodelFieldOfView { get; set; }
            /// <summary>Gets or sets the mouse look sensitivity.</summary>
            public float MouseSensitivity { get; set; }
            /// <summary>Gets or sets whether the viewport camera should have acceleration/deceleration when starting or stopping to move</summary>
            public bool SmoothCameraEnabled { get; set; }
            /// <summary>Gets or sets the number of MSAA samples used for anti-aliasing.</summary>
            public int AntiAliasingSamples { get; set; }
            /// <summary>Gets or sets the top edge position of the main window.</summary>
            public int WindowTop { get; set; }
            /// <summary>Gets or sets the left edge position of the main window.</summary>
            public int WindowLeft { get; set; }
            /// <summary>Gets or sets the width of the main window.</summary>
            public int WindowWidth { get; set; }
            /// <summary>Gets or sets the height of the main window.</summary>
            public int WindowHeight { get; set; }
            /// <summary>Gets or sets the window state (normal, minimized, maximized).</summary>
            public int WindowState { get; set; }
            /// <summary>Gets or sets the normalized audio playback volume.</summary>
            public float Volume { get; set; }
            /// <summary>Gets or sets the swap interval (the number of screen updates to wait between swapping front and back buffers).</summary>
            public int Vsync { get; set; }
            /// <summary>Gets or sets whether the FPS counter is shown in the viewport.</summary>
            public int DisplayFps { get; set; }
            /// <summary>Gets or sets the <see cref="QuickPreviewFlags"/> bitmask for quick file preview behavior.</summary>
            public int QuickFilePreview { get; set; }
            /// <summary>Gets or sets whether the file explorer panel is opened automatically on start (suppressed on first startup or when command-line files are provided).</summary>
            public int OpenExplorerOnStart { get; set; }
            /// <summary>Gets or sets the font size used in the text viewer.</summary>
            public int TextViewerFontSize { get; set; }
            /// <summary>Gets or sets whether the package file list uses grid view (1) or list view (0).</summary>
            public int PackageGridView { get; set; }
            /// <summary>Gets or sets the grid thumbnail size index (0-4, mapping to <see cref="GUI.Types.PackageViewer.ThumbnailRenderers.ThumbnailSizes"/> enum).</summary>
            public int PackageGridSize { get; set; }
            /// <summary>Internal settings file version used to apply migrations when upgrading from older versions. Do not modify manually.</summary>
            public int _VERSION_DO_NOT_MODIFY { get; set; }
            /// <summary>Gets or sets the application update check state.</summary>
            public AppUpdateState Update { get; set; } = new();
            /// <summary>
            /// Gets or sets remembered user interface state, as a free-form block of keys and values.
            /// Deliberately untyped and not covered by <see cref="SettingsFileCurrentVersion"/> migrations:
            /// a key that is absent is simply not applied, leaving whatever the UI set up in code, so new
            /// state can be added and removed without ever needing an upgrader. Read and write it through
            /// <see cref="GetUiState(string, bool)"/> and <see cref="SetUiState(string, bool)"/>.
            /// This block is read and written by hand in <see cref="Load"/> and <see cref="Save"/>,
            /// because the typed serializer cannot map a <see cref="KVObject"/> property.
            /// </summary>
            [KVIgnore]
            public KVObject UiState { get; set; } = KVObject.Collection();
        }

        /// <summary>Gets whether this is the first time the application has been launched (no prior settings were found).</summary>
        public static bool IsFirstStartup { get; private set; }
        /// <summary>Gets the folder path where the settings file and other persistent application data are stored.</summary>
        public static string SettingsFolder { get; private set; } = string.Empty;
        private static string SettingsFilePath = string.Empty;

        /// <summary>Gets the active application configuration.</summary>
        public static AppConfig Config { get; private set; } = new AppConfig();

        /// <summary>Raised when <see cref="AppConfig.SavedCameras"/> is mutated, signaling subscribers to refresh their camera lists.</summary>
        public static event EventHandler? RefreshCamerasOnSave;
        /// <summary>Raises the <see cref="RefreshCamerasOnSave"/> event.</summary>
        public static void InvokeRefreshCamerasOnSave() => RefreshCamerasOnSave?.Invoke(null, EventArgs.Empty);

        /// <summary>
        /// Loads the application configuration from disk, applies defaults and migrations for older
        /// settings file versions, and populates <see cref="Config"/>.
        /// </summary>
        public static void Load()
        {
            SettingsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Source2Viewer");
            SettingsFilePath = Path.Combine(SettingsFolder, "settings.vdf");

            Directory.CreateDirectory(SettingsFolder);

            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

                    using var stream = new FileStream(SettingsFilePath, FileMode.Open, FileAccess.Read);
                    Config = serializer.Deserialize<AppConfig>(stream, KVSerializerOptions.DefaultOptions);

                    // AppConfig.UiState is [KVIgnore]d, so read it out of the document itself
                    stream.Seek(0, SeekOrigin.Begin);
                    KVObject document = serializer.Deserialize(stream, KVSerializerOptions.DefaultOptions);

                    if (document.TryGetValue(nameof(AppConfig.UiState), out var uiState) && uiState.IsCollection)
                    {
                        Config.UiState = uiState;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(nameof(Settings), $"Failed to parse '{SettingsFilePath}', is it corrupted?{Environment.NewLine}{e}");

                try
                {
                    var corruptedPath = Path.ChangeExtension(SettingsFilePath, $".corrupted-{DateTimeOffset.Now.ToUnixTimeSeconds()}.txt");
                    File.Move(SettingsFilePath, corruptedPath);

                    Log.Error(nameof(Settings), $"Corrupted '{Path.GetFileName(SettingsFilePath)}' has been renamed to '{Path.GetFileName(corruptedPath)}'.");

                    Save();
                }
                catch
                {
                    //
                }
            }

            var currentVersion = Config._VERSION_DO_NOT_MODIFY;

            if (currentVersion > SettingsFileCurrentVersion)
            {
                // Blocking on the task is only safe here because this runs at startup before
                // the UI exists, when we switch to pangui, this will need to be correctly awaited
                // to not block the UI thread
                var continueAnyway = AppMessageDialogs.ConfirmAsync(
                    $"Your current settings.vdf has a higher version ({currentVersion}) than currently supported ({SettingsFileCurrentVersion}). You likely ran an older version of Source 2 Viewer and your settings may get reset.\n\nDo you want to continue?",
                    "Source 2 Viewer downgraded",
                    buttons: ConfirmButtons.YesNo
                ).GetAwaiter().GetResult();

                if (!continueAnyway)
                {
                    Environment.Exit(1);
                    return;
                }
            }

            Config.GameSearchPaths ??= [];
            Config.SavedCameras ??= [];
            Config.BookmarkedFiles ??= [];
            Config.RecentFiles ??= new(RecentFilesLimit);
            Config.Update ??= new();
            Config.UiState ??= KVObject.Collection();

            if (string.IsNullOrEmpty(Config.OpenDirectory))
            {
                var steamPath = Path.Join(GameFolderLocator.SteamPath, "steamapps", "common");

                if (Directory.Exists(steamPath))
                {
                    Config.OpenDirectory = steamPath;
                }
            }

            if (Config.MaxTextureSize <= 0)
            {
                Config.MaxTextureSize = 1024;
            }
            else if (Config.MaxTextureSize > 10240)
            {
                Config.MaxTextureSize = 10240;
            }

            if (Config.ShadowResolution <= 0)
            {
                Config.ShadowResolution = 2048;
            }
            else if (Config.ShadowResolution > 4096)
            {
                Config.ShadowResolution = 4096;
            }

            // upgrade fov
            if (currentVersion > 0 && currentVersion < 16)
            {
                var oldVerticalRadians = float.DegreesToRadians(Config.FieldOfView);
                var horizontalAt4By3Radians = 2f * MathF.Atan(MathF.Tan(oldVerticalRadians * 0.5f) * (4f / 3f));
                Config.FieldOfView = float.RadiansToDegrees(horizontalAt4By3Radians);
            }

            Config.FieldOfView = Math.Clamp(Config.FieldOfView, 1, 170);
            Config.ViewmodelFieldOfView = Math.Clamp(Config.ViewmodelFieldOfView, 40, 80);

            Config.AntiAliasingSamples = Math.Clamp(Config.AntiAliasingSamples, 0, 64);
            Config.Volume = MathUtils.Saturate(Config.Volume);
            Config.TextViewerFontSize = Math.Clamp(Config.TextViewerFontSize, 8, 24);
            Config.PackageGridSize = Math.Clamp(Config.PackageGridSize, 0, Enum.GetValues<Types.PackageViewer.ThumbnailRenderers.ThumbnailSizes>().Length - 1);

            if (currentVersion < 2) // version 2: added anti aliasing samples
            {
                Config.AntiAliasingSamples = 8;
            }

            if (currentVersion < 3) // version 3: added volume
            {
                Config.Volume = 0.5f;
            }

            if (currentVersion < 4) // version 4: added vsync
            {
                Config.Vsync = 1;
            }

            if (currentVersion < 5) // version 5: added display fps
            {
                Config.DisplayFps = 1;
            }

            if (currentVersion < 8) // version 8: added explorer on start
            {
                Config.OpenExplorerOnStart = 1;
            }

            if (currentVersion < 10) // version 10: added startup window
            {
                IsFirstStartup = true;
            }

            if (currentVersion < 11) // version 11: added text viewer font size
            {
                Config.TextViewerFontSize = 10;
            }

            if (currentVersion < 12) // version 12: enable automatic update checks by default
            {
                Config.Update.CheckAutomatically = true;
            }

            if (currentVersion < 13) // version 13: added package grid view and grid size
            {
                Config.PackageGridView = 1;
                Config.PackageGridSize = 2;
            }

            if (currentVersion < 14) // version 14: added mouse sensitivity
            {
                Config.MouseSensitivity = 4f;
            }

            if (currentVersion < 15) // version 15: added smooth camera setting
            {
                Config.SmoothCameraEnabled = true;
            }

            if (currentVersion < 16) // version 16: added viewmodel field of view
            {
                Config.ViewmodelFieldOfView = 64;
            }

            if (currentVersion > 0 && currentVersion != SettingsFileCurrentVersion)
            {
                Log.Info(nameof(Settings), $"Settings version changed: {currentVersion} -> {SettingsFileCurrentVersion}");
            }

            // If the version changed, force an update check (if enabled)
            if (Config.Update.Version != Program.ProductVersion)
            {
                Config.Update.Version = Program.ProductVersion;
                Config.Update.UpdateAvailable = false;
                Config.Update.LastCheck = string.Empty;
            }

            Config._VERSION_DO_NOT_MODIFY = SettingsFileCurrentVersion;
        }

        /// <summary>
        /// Serializes the current <see cref="Config"/> to disk, writing atomically via a temp file.
        /// </summary>
        public static void Save()
        {
            var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);

            // The typed serializer skips the untyped UiState block, so serialize everything else,
            // read it back as a document, and graft the block on before writing it out.
            KVObject document;

            using (var memory = new MemoryStream())
            {
                serializer.Serialize(memory, Config, nameof(ValveResourceFormat));
                memory.Seek(0, SeekOrigin.Begin);
                document = serializer.Deserialize(memory, KVSerializerOptions.DefaultOptions);
            }

            // Can still be null here when saving a reset of a corrupted file, before Load normalizes it
            if (Config.UiState is { Count: > 0 })
            {
                document[nameof(AppConfig.UiState)] = Config.UiState;
            }

            var tempFile = Path.GetTempFileName();

            using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, document, nameof(ValveResourceFormat));
            }

            File.Move(tempFile, SettingsFilePath, overwrite: true);
        }

        /// <summary>
        /// Reads a remembered UI state flag out of <see cref="AppConfig.UiState"/>.
        /// </summary>
        /// <param name="key">Key the flag was stored under.</param>
        /// <param name="whenNeverSaved">
        /// Value to return when <paramref name="key"/> was never saved, or holds something that is not a
        /// flag. Pass the value the UI already set up in code, so that a missing key changes nothing.
        /// </param>
        public static bool GetUiState(string key, bool whenNeverSaved)
        {
            if (!Config.UiState.TryGetValue(key, out var value))
            {
                return whenNeverSaved;
            }

            try
            {
                return value.ToBoolean();
            }
            catch (Exception e) when (e is FormatException or NotSupportedException or InvalidCastException)
            {
                // Settings are hand editable, fall back to what the caller wanted
                return whenNeverSaved;
            }
        }

        /// <summary>
        /// Remembers a UI state flag under <paramref name="key"/> and saves, unless it is already the
        /// stored value.
        /// </summary>
        public static void SetUiState(string key, bool value)
        {
            if (Config.UiState.ContainsKey(key) && GetUiState(key, whenNeverSaved: !value) == value)
            {
                return; // Already stored, do not rewrite the file
            }

            Config.UiState[key] = new KVObject(value);
            Save();
        }

        /// <summary>
        /// Appends <paramref name="path"/> to the end of the recent files list (most recent last),
        /// removing any duplicate entry, trimming the oldest entries to <see cref="RecentFilesLimit"/>, then saves.
        /// </summary>
        /// <param name="path">The absolute file path to record as recently opened.</param>
        public static void TrackRecentFile(string path)
        {
            Config.RecentFiles.Remove(path);
            Config.RecentFiles.Add(path);

            if (Config.RecentFiles.Count > RecentFilesLimit)
            {
                Config.RecentFiles.RemoveRange(0, Config.RecentFiles.Count - RecentFilesLimit);
            }

            Save();
        }

        /// <summary>
        /// Clears the recent files list and saves.
        /// </summary>
        public static void ClearRecentFiles()
        {
            Config.RecentFiles.Clear();
            Save();
        }
    }
}
