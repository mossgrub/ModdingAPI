using System.Collections.Generic;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace Modding
{

    /// <summary>
    ///     Strategy preloading game objects
    /// </summary>
    [PublicAPI]
    public enum PreloadMode
    {
        /// <summary>
        ///     Load the entire scene unmodified into memory
        /// </summary>
        FullScene,
        /// <summary>
        ///     Preprocess the scenes into an assetbundle, containing filtered versions of the originals
        /// </summary>
        RepackScene,
        /// <summary>
        ///     Preprocess the scenes into an assetbundle that contains individual game object assets
        /// </summary>
        RepackAssets,
    }

    /// <summary>
    ///     Class to hold GlobalSettings for the Modding API
    /// </summary>
    [PublicAPI]
    public class ModHooksGlobalSettings
    {
        [JsonProperty]
        internal Dictionary<string, bool> ModEnabledSettings = new Dictionary<string, bool>();

        /// <summary>
        ///     Logging Level to use.
        /// </summary>
        public LogLevel LoggingLevel = LogLevel.Info;

        /// <summary>
        ///     Determines if the logs should have a short log level instead of the full name.
        /// </summary>
        public bool ShortLoggingLevel;

        /// <summary>
        ///     Creates a native log file in the persistent data path for logging modding native lib.
        /// </summary>
        public bool NativeLogging = false;

        /// <summary>
        ///     Determines if the logs should have a timestamp attached to each line of logging.
        /// </summary>
        public bool IncludeTimestamps;

        /// <summary>
        /// Enables the native GameObject component compatibility hooks.
        /// </summary>
        public bool ComponentHook = false;

        /// <summary>
        /// Enables the PlayMaker2D bootstrap prefab to be loaded into the scene
        /// </summary>
        public bool PlayMaker2DBootstrap = true;

        /// <summary>
        ///     All settings related to the the in game console
        /// </summary>
        public InGameConsoleSettings ConsoleSettings = new InGameConsoleSettings();

        /// <summary>
        ///     Determines if Debug Console (Which displays Messages from Logger) should be shown.
        /// </summary>
        public bool ShowDebugLogInGame;

        /// <summary>
        ///     Determines for the preloading how many different scenes should be loaded at once.
        /// </summary>
        public int PreloadBatchSize = 5;

        /// <summary>
        ///     Determines the strategy used for preloading game objects.
        /// </summary>
        public PreloadMode PreloadMode = PreloadMode.RepackAssets;

        /// <summary>
        ///     Maximum number of days to preserve modlogs for.
        /// </summary>
        public int ModlogMaxAge = 7;
    }
}
