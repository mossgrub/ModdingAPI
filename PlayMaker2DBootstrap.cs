using System;
using System.Collections;
using System.Collections.Generic;
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

        private static BootstrapRunner _runner;

        private static readonly HashSet<int>
            _processedModPrefabFsms =
                new HashSet<int>();

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
                EnsureRunner();

                UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
                    OnSceneLoaded;

                EnsureCurrentScene();

                ScheduleModPrefabPreprocess();

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
                    UnityEngine.SceneManagement.SceneManager.sceneLoaded -=
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
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -=
                    OnSceneLoaded;
            }
            catch
            {
            }

            _installed = false;

            _processedModPrefabFsms.Clear();

            Logger.APILogger.Log(
                "PlayMaker2D bootstrap uninstalled");
        }

        private static void OnSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            try
            {
                EnsureScene(scene);

                ScheduleModPrefabPreprocess();
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
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene();

                if (!scene.IsValid())
                {
                    Logger.APILogger.LogWarn(
                        "PlayMaker2D active scene is invalid");

                    return;
                }

                EnsureScene(scene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "PlayMaker2D current scene bootstrap failed: " +
                    ex);
            }
        }

        private static void EnsureScene(
            Scene scene)
        {
            if (!scene.IsValid())
                return;

            if (!scene.isLoaded)
                return;

            if (HasExistingInstance(scene))
            {
                return;
            }

            GameObject prefab =
                LoadPrefab();

            if (prefab == null)
                return;

            try
            {
                GameObject instance =
                    UnityEngine.Object.Instantiate(
                        prefab);

                if (instance == null)
                    return;

                instance.name =
                    InstanceName;

                UnityEngine.SceneManagement.SceneManager
                    .MoveGameObjectToScene(
                        instance,
                        scene);

                PreprocessPlayMakerFsms(
                    instance);
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
                    "PlayMaker2D prefab not found: " +
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
                        root.GetComponentsInChildren<
                            Transform>(
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
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker2D existing-instance check failed: " +
                    ex.Message);
            }

            return false;
        }

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
                        fsm.Preprocess();
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn(
                            "Play Maker failed to preprocess FSM `" +
                            SafeFsmName(fsm) +
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

        private static void ScheduleModPrefabPreprocess()
        {
            EnsureRunner();

            if (_runner != null)
            {
                _runner.Schedule();
            }
        }

        private static void PreprocessLoadedModPrefabs()
        {
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

                    if (!IsPrefabObject(fsm.gameObject))
                        continue;

                    if (!IsModOwnedObject(fsm.gameObject))
                        continue;

                    int id =
                        fsm.GetInstanceID();

                    if (_processedModPrefabFsms.Contains(id))
                        continue;

                    try
                    {
                        fsm.Preprocess();

                        _processedModPrefabFsms.Add(id);

                        processed++;

                        Logger.APILogger.LogDebug(
                            "PlayMaker preprocessed mod prefab FSM `" +
                            SafeFsmName(fsm) +
                            "` on `" +
                            fsm.gameObject.name +
                            "`.");
                    }
                    catch (Exception ex)
                    {
                        Logger.APILogger.LogWarn(
                            "PlayMaker failed to preprocess mod prefab FSM `" +
                            SafeFsmName(fsm) +
                            "`: " +
                            ex.Message);
                    }
                }

                if (processed > 0)
                {
                    Logger.APILogger.Log(
                        "PlayMaker preprocessed " +
                        processed +
                        " mod prefab FSM(s).");
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker mod prefab scan failed: " +
                    ex);
            }
        }

        private static bool IsPrefabObject(
            GameObject obj)
        {
            if (obj == null)
                return false;

            try
            {
                Scene scene =
                    obj.scene;

                if (scene.IsValid() &&
                    scene.isLoaded)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsModOwnedObject(
            GameObject obj)
        {
            if (obj == null)
                return false;

            try
            {
                Transform root =
                    obj.transform.root;

                if (root == null)
                    return false;

                MonoBehaviour[] behaviours =
                    root.GetComponentsInChildren<
                        MonoBehaviour>(
                            true);

                if (behaviours == null)
                    return false;

                for (int i = 0;
                     i < behaviours.Length;
                     i++)
                {
                    MonoBehaviour behaviour =
                        behaviours[i];

                    if (behaviour == null)
                        continue;

                    Type type =
                        behaviour.GetType();

                    if (type == null)
                        continue;

                    string fullName =
                        type.FullName;

                    if (!string.IsNullOrEmpty(
                            fullName) &&
                        fullName.StartsWith(
                            "HollowPoint.",
                            StringComparison.Ordinal))
                    {
                        return true;
                    }

                    Assembly assembly =
                        type.Assembly;

                    if (assembly == null)
                        continue;

                    string location = null;

                    try
                    {
                        location =
                            assembly.Location;
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrEmpty(location))
                        continue;

                    if (location.IndexOf(
                            "/Mods/",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        location.IndexOf(
                            "\\Mods\\",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static string SafeFsmName(
            PlayMakerFSM fsm)
        {
            try
            {
                return fsm != null
                    ? fsm.FsmName
                    : "<null>";
            }
            catch
            {
                return "<unknown>";
            }
        }

        private static void EnsureRunner()
        {
            if (_runner != null)
                return;

            try
            {
                GameObject go =
                    GameObject.Find(
                        "__Modding_PlayMaker2DRunner");

                if (go == null)
                {
                    go =
                        new GameObject(
                            "__Modding_PlayMaker2DRunner");

                    UnityEngine.Object.DontDestroyOnLoad(
                        go);
                }

                _runner =
                    go.GetComponent<
                        BootstrapRunner>();

                if (_runner == null)
                {
                    _runner =
                        go.AddComponent<
                            BootstrapRunner>();
                }
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogWarn(
                    "PlayMaker2D runner creation failed: " +
                    ex.Message);

                _runner = null;
            }
        }

        private sealed class BootstrapRunner
            : MonoBehaviour
        {
            private bool _scheduled;

            internal void Schedule()
            {
                if (_scheduled)
                    return;

                _scheduled = true;

                StartCoroutine(
                    DeferredScan());
            }

            private IEnumerator DeferredScan()
            {
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        yield return null;

                        PreprocessLoadedModPrefabs();
                    }
                }
                finally
                {
                    _scheduled = false;
                }
            }
        }
    }
}