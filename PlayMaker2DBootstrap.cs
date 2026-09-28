using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using HutongGames.PlayMaker;

namespace Modding
{
    public static class PlayMaker2DBootstrap
    {
        private const string PrefabResourcePath =
            "PlayMaker Unity 2D";

        private const string InstanceName =
            "PlayMaker Unity 2D";

        private static bool _installed;
        private static bool _awakeHookInstalled;

        private delegate void OrigPlayMakerAwake(
            PlayMakerFSM self);

        public static bool IsInstalled
        {
            get
            {
                return _installed;
            }
        }

        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            try
            {
                InstallPlayMakerAwakeHook();

                SceneManager.sceneLoaded +=
                    OnSceneLoaded;

                EnsureCurrentScene();

                EnsureCurrentSceneFsms();

                Logger.APILogger.Log(
                    "PlayMaker2D bootstrap installed");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMaker2D bootstrap installation failed: " +
                    ex);

                try
                {
                    SceneManager.sceneLoaded -=
                        OnSceneLoaded;
                }
                catch
                {
                }

                _installed = false;
            }
        }

        public static void Uninstall()
        {
            if (!_installed)
                return;

            try
            {
                SceneManager.sceneLoaded -=
                    OnSceneLoaded;
            }
            catch
            {
            }

            _installed = false;

            Logger.APILogger.Log(
                "PlayMaker2D bootstrap uninstalled");
        }

        // PlayMakerFSM.Awake hook

        private static void InstallPlayMakerAwakeHook()
        {
            if (_awakeHookInstalled)
                return;

            try
            {
                MethodInfo awake =
                    typeof(PlayMakerFSM).GetMethod(
                        "Awake",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (awake == null)
                {
                    Logger.APILogger.LogWarn(
                        "PlayMakerFSM.Awake not found. " +
                        "Global FSM initialization hook was not installed.");

                    return;
                }

                Delegate replacement =
                    (OrigPlayMakerAwake)PlayMakerAwakeHook;

                Delegate trampoline;

                string error;

                bool installed =
                    DetourBridge.TryCreateOrigDetour(
                        awake,
                        replacement,
                        out trampoline,
                        out error);

                if (!installed)
                {
                    Logger.APILogger.LogWarn(
                        "PlayMakerFSM.Awake hook failed: " +
                        error);

                    return;
                }

                _awakeHookInstalled = true;

                Logger.APILogger.Log(
                    "PlayMakerFSM.Awake initialization hook installed");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMakerFSM.Awake hook installation failed: " +
                    ex);
            }
        }

        private static void PlayMakerAwakeHook(
            OrigPlayMakerAwake orig,
            PlayMakerFSM self)
        {
            try
            {
                if (orig != null)
                {
                    orig(self);
                }
            }
            finally
            {
                EnsureInitialized(
                    self,
                    "Awake");
            }
        }

        // Scene handling

        private static void OnSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            try
            {
                EnsureScene(scene);
                EnsureSceneFsms(scene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMaker2D scene bootstrap failed: " +
                    ex);
            }
        }

        private static void EnsureCurrentScene()
        {
            try
            {
                Scene scene =
                    SceneManager.GetActiveScene();

                if (!scene.IsValid() ||
                    !scene.isLoaded)
                {
                    return;
                }

                EnsureScene(scene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker2D current-scene bootstrap failed: " +
                    ex.Message);
            }
        }

        private static void EnsureScene(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return;
            }

            try
            {
                if (HasExistingInstance(scene))
                {
                    return;
                }

                GameObject prefab =
                    LoadPrefab();

                if (prefab == null)
                {
                    return;
                }

                GameObject instance =
                    UnityEngine.Object.Instantiate(
                        prefab);

                if (instance == null)
                {
                    Logger.APILogger.LogWarn(
                        "PlayMaker2D Instantiate returned null");

                    return;
                }

                instance.name =
                    InstanceName;

                SceneManager.MoveGameObjectToScene(
                    instance,
                    scene);

                PreprocessPlayMakerFsms(instance);

                Logger.APILogger.Log(
                    "PlayMaker2D instantiated PlayMaker Unity 2D " +
                    "in scene: " +
                    scene.name);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMaker2D failed to instantiate PlayMaker Unity 2D: " +
                    ex);
            }
        }

        private static GameObject LoadPrefab()
        {
            try
            {
                GameObject prefab =
                    Resources.Load<GameObject>(
                        PrefabResourcePath);

                if (prefab != null)
                {
                    Logger.APILogger.Log(
                        "PlayMaker2D prefab loaded from resources: " +
                        PrefabResourcePath);

                    return prefab;
                }

                Logger.APILogger.LogWarn(
                    "PlayMaker2D Resources.Load returned null for: " +
                    PrefabResourcePath);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMaker2D failed to load prefab: " +
                    ex);
            }

            return null;
        }

        // Existing prefab instance detection

        private static bool HasExistingInstance(
            Scene scene)
        {
            try
            {
                GameObject[] roots =
                    scene.GetRootGameObjects();

                for (int i = 0;
                     i < roots.Length;
                     i++)
                {
                    GameObject root =
                        roots[i];

                    if (root == null)
                        continue;

                    Transform[] transforms =
                        root.GetComponentsInChildren<Transform>(
                            true);

                    for (int j = 0;
                         j < transforms.Length;
                         j++)
                    {
                        Transform transform =
                            transforms[j];

                        if (transform == null)
                            continue;

                        if (transform.name ==
                            InstanceName)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        // Global FSM scan

        private static void EnsureCurrentSceneFsms()
        {
            try
            {
                Scene activeScene =
                    SceneManager.GetActiveScene();

                if (!activeScene.IsValid() ||
                    !activeScene.isLoaded)
                {
                    return;
                }

                EnsureSceneFsms(activeScene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker current-scene FSM scan failed: " +
                    ex.Message);
            }
        }

        private static void EnsureSceneFsms(
            Scene scene)
        {
            if (!scene.IsValid() ||
                !scene.isLoaded)
            {
                return;
            }

            try
            {
                PlayMakerFSM[] fsms =
                    Resources.FindObjectsOfTypeAll<
                        PlayMakerFSM>();

                if (fsms == null)
                    return;

                int processed = 0;

                for (int i = 0;
                     i < fsms.Length;
                     i++)
                {
                    PlayMakerFSM fsm =
                        fsms[i];

                    if (fsm == null)
                        continue;

                    try
                    {
                        GameObject go =
                            fsm.gameObject;

                        if (go == null)
                            continue;

                        Scene objectScene =
                            go.scene;

                        if (!objectScene.IsValid() ||
                            !objectScene.isLoaded)
                        {
                            continue;
                        }

                        if (objectScene != scene)
                        {
                            continue;
                        }

                        EnsureInitialized(
                            fsm,
                            "scene scan");

                        processed++;
                    }
                    catch
                    {
                    }
                }

                Logger.APILogger.LogDebug(
                    "PlayMaker scene scan initialized " +
                    processed +
                    " FSM(s) in `" +
                    scene.name +
                    "`.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker FSM scene scan failed: " +
                    ex.Message);
            }
        }

        // FSM initialization

        private static void PreprocessPlayMakerFsms(
            GameObject root)
        {
            if (root == null)
                return;

            try
            {
                PlayMakerFSM[] fsms =
                    root.GetComponentsInChildren<
                        PlayMakerFSM>(
                            true);

                if (fsms == null)
                    return;

                int processed = 0;

                for (int i = 0;
                     i < fsms.Length;
                     i++)
                {
                    PlayMakerFSM fsm =
                        fsms[i];

                    if (fsm == null)
                        continue;

                    try
                    {
                        EnsureInitialized(
                            fsm,
                            "prefab");

                        processed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn(
                            "Play Maker failed to preprocess FSM `" +
                            fsm.FsmName +
                            "`: " +
                            ex.Message);
                    }
                }

                Logger.APILogger.Log(
                    "Play Maker preprocessed " +
                    processed +
                    "/" +
                    fsms.Length +
                    " FSM(s) on `" +
                    root.name +
                    "`.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "Play Maker FSM preprocessing failed: " +
                    ex);
            }
        }

        private static void EnsureInitialized(
            PlayMakerFSM fsm,
            string reason)
        {
            if (fsm == null)
                return;

            try
            {
                fsm.Preprocess();
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker FSM initialization failed for `" +
                    fsm.FsmName +
                    "` during " +
                    reason +
                    ": " +
                    ex.Message);
            }
        }
    }
}