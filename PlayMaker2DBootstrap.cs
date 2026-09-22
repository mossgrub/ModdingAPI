using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modding
{
    public static class PlayMaker2DBootstrap
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;

            _installed = true;

            SceneManager.sceneLoaded +=
                OnSceneLoaded;

            EnsureForScene(
                SceneManager.GetActiveScene());

            Logger.APILogger.Log(
                "[PLAYMAKER2D] Bootstrap installed.");
        }

        private static void OnSceneLoaded(
            Scene scene,
            LoadSceneMode mode)
        {
            EnsureForScene(scene);
        }

        private static void EnsureForScene(
            Scene scene)
        {
            if (!scene.IsValid())
                return;

            GameObject[] roots =
                scene.GetRootGameObjects();

            foreach (GameObject root in roots)
            {
                if (root == null)
                    continue;

                if (root.name ==
                    "PlayMaker Unity 2D")
                {
                    return;
                }
            }

            GameObject prefab =
                FindPlayMaker2DPrefab();

            if (prefab != null)
            {
                try
                {
                    GameObject instance =
                        UnityEngine.Object.Instantiate(
                            prefab);

                    instance.name =
                        "PlayMaker Unity 2D";

                    SceneManager.MoveGameObjectToScene(
                        instance,
                        scene);

                    Logger.APILogger.Log(
                        "[PLAYMAKER2D] Prefab instantiated.");

                    return;
                }
                catch (Exception ex)
                {
                    Logger.APILogger.LogWarn(
                        "[PLAYMAKER2D] Prefab instantiate failed: " +
                        ex.Message);
                }
            }

            Type componentType =
                FindPlayMaker2DType();

            if (componentType == null)
            {
                Logger.APILogger.LogWarn(
                    "[PLAYMAKER2D] PlayMakerUnity2d component type not found.");

                return;
            }

            try
            {
                GameObject go =
                    new GameObject(
                        "PlayMaker Unity 2D");

                go.AddComponent(componentType);

                Logger.APILogger.Log(
                    "[PLAYMAKER2D] Created runtime bootstrap using " +
                    componentType.FullName);
            }
            catch (Exception ex)
            {
                Logger.APILogger.LogError(
                    "[PLAYMAKER2D] Failed to create runtime bootstrap: " +
                    ex);
            }
        }

        private static GameObject FindPlayMaker2DPrefab()
        {
            GameObject[] objects =
                Resources.FindObjectsOfTypeAll<GameObject>();

            foreach (GameObject go in objects)
            {
                if (go == null)
                    continue;

                if (go.name !=
                    "PlayMaker Unity 2D")
                    continue;

                if (!go.scene.IsValid())
                    return go;
            }

            return null;
        }

        private static Type FindPlayMaker2DType()
        {
            foreach (Assembly asm
                     in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type
                             in asm.GetTypes())
                    {
                        if (type.Name ==
                                "PlayMakerUnity2d" ||
                            type.Name ==
                                "PlayMakerUnity2D")
                        {
                            return type;
                        }
                    }
                }
                catch
                {
                    // Ignore assemblies whose types cannot be enumerated.
                }
            }

            return null;
        }
    }
}