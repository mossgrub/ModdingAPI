using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modding
{
    public static class PlayMaker2DBootstrap
    {
        private const string PrefabResourcePath =
            "PlayMaker Unity 2D";

        private const string InstanceName =
            "PlayMaker Unity 2D";

        private static bool _installed;

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
                UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
                    OnSceneLoaded;

                EnsureCurrentScene();

                Logger.APILogger.Log(
                    "[PLAYMAKER2D] Bootstrap installed.");
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Bootstrap installation failed: " +
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

            Logger.APILogger.Log(
                "[PLAYMAKER2D] Bootstrap uninstalled.");
        }

        private static void OnSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            try
            {
                EnsureScene(scene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Scene bootstrap failed: " +
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
                        "[PLAYMAKER2D] Active scene is invalid.");

                    return;
                }

                EnsureScene(scene);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Current scene bootstrap failed: " +
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
                Logger.APILogger.LogFine(
                    "[PLAYMAKER2D] Existing instance found in scene: " +
                    scene.name);

                return;
            }

            GameObject prefab =
                LoadPrefab();

            if (prefab == null)
            {
                Logger.APILogger.LogWarn(
                    "[PLAYMAKER2D] PlayMaker Unity 2D prefab not found. " +
                    "Bootstrap will not modify this scene.");

                return;
            }

            try
            {
                GameObject instance =
                    UnityEngine.Object.Instantiate(
                        prefab);

                if (instance == null)
                {
                    Logger.APILogger.LogWarn(
                        "[PLAYMAKER2D] Instantiate returned null.");

                    return;
                }

                instance.name =
                    InstanceName;

                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(
                    instance,
                    scene);

                Logger.APILogger.Log(
                    "[PLAYMAKER2D] Instantiated PlayMaker Unity 2D " +
                    "in scene: " +
                    scene.name);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Failed to instantiate PlayMaker Unity 2D: " +
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
                        "[PLAYMAKER2D] Prefab loaded from Resources: " +
                        PrefabResourcePath);

                    return prefab;
                }

                Logger.APILogger.LogWarn(
                    "[PLAYMAKER2D] Resources.Load returned null for: " +
                    PrefabResourcePath);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Failed to load prefab: " +
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
                    "[PLAYMAKER2D] Existing-instance check failed: " +
                    ex.Message);
            }

            return false;
        }
    }
}