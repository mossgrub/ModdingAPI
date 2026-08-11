using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using HybridCLR;
using UnityEngine;
using UnityEngine.Networking;

namespace Modding
{
    internal static class HybridCLRBoot
    {
        private static bool _bootStarted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
#if ENABLE_IL2CPP && UNITY_ANDROID
            if (_bootStarted) return;
            _bootStarted = true;
            try
            {
                Debug.Log("Boot started (async).");
                GameObject go = new GameObject("HybridCLRLoader");
                                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<BootComponent>();
            }
            catch (Exception e) { Debug.LogError("Boot failed: " + e); }
#endif
        }

        private sealed class BootComponent : MonoBehaviour
        {
            private void Start() { StartCoroutine(LoadRoutine()); }

            private IEnumerator LoadRoutine()
            {
                yield return LoadMetadata();
                yield return LoadHotUpdate();
                HybridCLRInitializer.Initialize();
                yield return null;
                GameObject mlGO = new GameObject("ModLoader");
                                UnityEngine.Object.DontDestroyOnLoad(mlGO);
                mlGO.AddComponent<ModLoaderComponent>();
            }

            private IEnumerator LoadMetadata()
            {
                string dir = Path.Combine(Application.streamingAssetsPath, "HybridCLRData", "il2cpp_data", "Managed");
                string[] files = null;
                yield return ReadManifestCoroutine(Path.Combine(dir, "manifest.txt"), n => files = n);
                if (files == null || files.Length == 0) { Debug.LogWarning("No metadata manifest."); yield break; }
                int loaded = 0;
                foreach (string name in files)
                {
                    byte[] bytes = null;
                    yield return ReadBytesCoroutine(Path.Combine(dir, name), b => bytes = b);
                    if (bytes == null || bytes.Length == 0) continue;
                    try
                    {
                        LoadImageErrorCode err = RuntimeApi.LoadMetadataForAOTAssembly(bytes, HomologousImageMode.Consistent);
                        if (err == LoadImageErrorCode.OK) loaded++;
                    }
                    catch (Exception e) { Debug.LogError("Metadata error " + name + ": " + e.Message); }
                }
                Debug.Log("Metadata loaded: " + loaded + "/" + files.Length);
            }

            private IEnumerator LoadHotUpdate()
            {
                string dir = Path.Combine(Application.streamingAssetsPath, "HybridCLRData", "hot_update_dlls");
                string[] files = null;
                yield return ReadManifestCoroutine(Path.Combine(dir, "manifest.txt"), n => files = n);
                if (files == null || files.Length == 0) { Debug.LogWarning("No hot-update manifest."); yield break; }
                foreach (string name in files)
                {
                    string fileName = Path.GetFileName(name);
                    if (fileName == "Assembly-CSharp.dll")
                    {
                        Debug.Log($"Skipping Assembly-CSharp.dll (AOT only)");
                        continue;
                    }

                    byte[] bytes = null;
                    yield return ReadBytesCoroutine(Path.Combine(dir, fileName), b => bytes = b);
                    if (bytes == null || bytes.Length == 0) { Debug.LogError("Missing: " + fileName); continue; }
                    try
                    {
                        var asm = Assembly.Load(bytes);
                        Debug.Log("Loaded: " + asm.FullName);
                    }
                    catch (Exception e) { Debug.LogError("Failed " + fileName + ": " + e.Message); }
                }
            }

            private IEnumerator ReadManifestCoroutine(string path, System.Action<string[]> onDone)
            {
                byte[] bytes = null;
                yield return ReadBytesCoroutine(path, b => bytes = b);
                if (bytes == null || bytes.Length == 0) { onDone(null); yield break; }
                var list = new System.Collections.Generic.List<string>();
                foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length > 0 && !s.StartsWith("#")) list.Add(s);
                }
                onDone(list.Count == 0 ? null : list.ToArray());
            }

            private IEnumerator ReadBytesCoroutine(string url, System.Action<byte[]> onDone)
            {
                byte[] result = null;
                using (UnityWebRequest uwr = UnityWebRequest.Get(url))
                {
                    yield return uwr.SendWebRequest();
                    if (uwr.isNetworkError || uwr.isHttpError)
                    {
                        Debug.LogWarning("Read failed: " + uwr.error + " @ " + url);
                    }
                    else { result = uwr.downloadHandler?.data; }
                }
                onDone(result);
            }
        }

        private sealed class ModLoaderComponent : MonoBehaviour
        {
            private void Start() { StartCoroutine(LoadRoutine()); }
            private IEnumerator LoadRoutine()
            {
                Debug.Log("Starting mod initialization.");
                yield return ModLoader.LoadModsInit(gameObject);
                Debug.Log("Mod initialization complete.");
            }
        }
    }
}