using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Modding.Utils;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Modding
{
	internal class Preloader : MonoBehaviour
	{
		private ProgressBar progressBar;

		private static string DataPath
		{
			get
			{
				if (Application.platform == RuntimePlatform.Android)
				{
					return Path.Combine(Application.persistentDataPath, "Data");
				}
				if (Application.platform != RuntimePlatform.OSXPlayer)
				{
					return Application.dataPath;
				}
				return Path.Combine(Application.dataPath, "Resources", "Data");
			}
		}

		private void Start()
		{
			progressBar = base.gameObject.AddComponent<ProgressBar>();
		}

		public IEnumerator Preload(Dictionary<string, List<(ModLoader.ModInstance, List<string>)>> toPreload, Dictionary<ModLoader.ModInstance, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects, Dictionary<string, List<Func<IEnumerator>>> sceneHooks)
		{
			Stopwatch stopwatch = Stopwatch.StartNew();
			MuteAllAudio();

			bool flag = sceneHooks.Sum((KeyValuePair<string, List<Func<IEnumerator>>> kvp) => kvp.Value.Count) > 0;
			Logger.APILogger.Log($"Preloading using mode {ModHooks.GlobalSettings.PreloadMode}");
			PreloadMode preloadMode = ModHooks.GlobalSettings.PreloadMode;

			if (Application.platform == RuntimePlatform.Android)
			{
				Logger.APILogger.LogWarn("Android detected: Changing to full scene loads!");
				preloadMode = PreloadMode.FullScene;
			}
			else if (preloadMode != PreloadMode.FullScene)
			{
				try
				{
					Marshal.PrelinkAll(typeof(UnitySceneRepacker));
				}
				catch (DllNotFoundException)
				{
					Logger.APILogger.LogWarn("UnitySceneRepacker failed: Changing to full scene loads!");
					preloadMode = PreloadMode.FullScene;
				}
			}
			switch (preloadMode)
			{
				case PreloadMode.FullScene:
					yield return DoPreloadScenes(toPreload, preloadedObjects, sceneHooks);
					break;
				case PreloadMode.RepackScene:
					yield return DoPreloadRepackedScenes(toPreload, preloadedObjects, sceneHooks);
					break;
				case PreloadMode.RepackAssets:
					if (flag)
					{
						Logger.APILogger.LogWarn("Some mods (" + string.Join(", ", sceneHooks.Keys) + ") use scene hooks, falling back to \"RepackScene\" preload mode");
						yield return DoPreloadRepackedScenes(toPreload, preloadedObjects, sceneHooks);
					}
					else
					{
						yield return DoPreloadAssetBundle(toPreload, preloadedObjects, sceneHooks);
					}
					break;
				default:
					Logger.APILogger.LogError($"Unknown preload mode {ModHooks.GlobalSettings.PreloadMode}. Expected one of: full-scene, repack-scene, repack-assets");
					break;
			}
			yield return CleanUpPreloading();
			UnmuteAllAudio();
			Logger.APILogger.Log($"Finished preloading in {stopwatch.ElapsedMilliseconds / 1000:F2}s");
		}

		private static void MuteAllAudio()
		{
			AudioListener.pause = true;
		}

		private IEnumerator DoPreloadAssetBundle(Dictionary<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> toPreload, IDictionary<ModLoader.ModInstance, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects, Dictionary<string, List<Func<IEnumerator>>> sceneHooks)
		{
			string preloadsJson = JsonConvert.SerializeObject(toPreload.ToDictionary((KeyValuePair<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> k) => k.Key, (KeyValuePair<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> v) => v.Value.SelectMany(((ModLoader.ModInstance Mod, List<string> Preloads) x) => x.Preloads).Distinct()));
			byte[] array = null;
			try
			{
				RepackStats repackStats;
				(array, repackStats) = UnitySceneRepacker.Repack("modding_api_asset_bundle", DataPath, preloadsJson, UnitySceneRepacker.Mode.AssetBundle);
				Logger.APILogger.Log($"Repacked {toPreload.Count} preload scenes from {repackStats.ObjectsBefore} to {repackStats.ObjectsAfter} objects ({(float)array.Length / 1024f / 1024f:F2}MB)");
			}
			catch (Exception arg)
			{
				Logger.APILogger.LogError($"Error trying to repack preloads into asset bundle: {arg}");
			}
			if (array == null)
			{
				yield return DoPreloadScenes(toPreload, preloadedObjects, sceneHooks);
				yield break;
			}
			AssetBundleCreateRequest op = AssetBundle.LoadFromMemoryAsync(array);
			if (op == null)
			{
				progressBar.Progress = 1f;
				yield break;
			}
			yield return op;
			AssetBundle assetBundle = op.assetBundle;
			HashSet<AssetBundleRequest> queue = new HashSet<AssetBundleRequest>();
			foreach (var (sceneName, list2) in toPreload)
			{
				foreach (var (mod, list3) in list2)
				{
					if (!preloadedObjects.TryGetValue(mod, out var value))
					{
						value = new Dictionary<string, Dictionary<string, GameObject>>();
						preloadedObjects[mod] = value;
					}
					if (!value.TryGetValue(sceneName, out var modScenePreloads))
					{
						modScenePreloads = new Dictionary<string, GameObject>();
						value[sceneName] = modScenePreloads;
					}
					foreach (string path in list3)
					{
						if (modScenePreloads.ContainsKey(path))
						{
							continue;
						}
						string assetName = sceneName + "/" + path + ".prefab";
						AssetBundleRequest request = assetBundle.LoadAssetAsync<GameObject>(assetName);
						request.completed += delegate
						{
							queue.Remove(request);
							GameObject gameObject = (GameObject)request.asset;
							if (!gameObject)
							{
								Logger.APILogger.LogError("    could not load '" + assetName + "'");
							}
							else if (modScenePreloads.ContainsKey(path))
							{
								Logger.APILogger.LogWarn("Duplicate preload by " + mod.Name + ": '" + path + "' in '" + sceneName + "'");
							}
							else
							{
								GameObject modGo = UnityEngine.Object.Instantiate(gameObject);
								UnityEngine.Object.DontDestroyOnLoad(modGo);
								modGo.SetActive(value: false);
								modScenePreloads.Add(path, modGo);
							}
						};
						queue.Add(request);
					}
				}
			}
			int total = queue.Count;
			while (queue.Count > 0)
			{
				float progress = (float)(total - queue.Count) / (float)total;
				progressBar.Progress = progress;
				yield return null;
			}
		}

		private IEnumerator DoPreloadRepackedScenes(Dictionary<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> toPreload, IDictionary<ModLoader.ModInstance, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects, Dictionary<string, List<Func<IEnumerator>>> sceneHooks)
		{
			string preloadJson = JsonConvert.SerializeObject(toPreload.ToDictionary((KeyValuePair<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> k) => k.Key, (KeyValuePair<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> v) => v.Value.SelectMany(((ModLoader.ModInstance Mod, List<string> Preloads) x) => x.Preloads).Distinct()));
			byte[] bundleData = null;
			Task task = Task.Run(delegate
			{
				try
				{
					RepackStats repackStats;
					(bundleData, repackStats) = UnitySceneRepacker.Repack("modding_api_scene_bundle", DataPath, preloadJson, UnitySceneRepacker.Mode.SceneBundle);
					Logger.APILogger.Log($"Repacked {toPreload.Count} preload scenes from {repackStats.ObjectsBefore} to {repackStats.ObjectsAfter} objects ({(float)bundleData.Length / 1024f / 1024f:F2}MB)");
				}
				catch (Exception arg)
				{
					Logger.APILogger.LogError($"Error trying to repack preloads into assetbundle: {arg}");
				}
			});
			yield return new WaitUntil(() => task.IsCompleted);
			if (bundleData == null)
			{
				yield return DoPreloadScenes(toPreload, preloadedObjects, sceneHooks);
				yield break;
			}
			AssetBundle repackBundle = AssetBundle.LoadFromMemory(bundleData);
			if (repackBundle == null)
			{
				Logger.APILogger.LogWarn("Scene repacking during preloading produced an unloadable asset bundle");
				yield return DoPreloadScenes(toPreload, preloadedObjects, sceneHooks);
				yield break;
			}
			HashSet<string> hashSet = new HashSet<string>(sceneHooks.Select((KeyValuePair<string, List<Func<IEnumerator>>> x) => x.Key));
			Dictionary<string, List<(ModLoader.ModInstance, List<string>)>> shared = new Dictionary<string, List<(ModLoader.ModInstance, List<string>)>>();
			Dictionary<string, List<(ModLoader.ModInstance, List<string>)>> dictionary = new Dictionary<string, List<(ModLoader.ModInstance, List<string>)>>();
			foreach (var (text2, value) in toPreload)
			{
				(hashSet.Contains(text2) ? shared : dictionary)[text2] = value;
			}
			yield return DoPreloadScenes(dictionary, preloadedObjects, new Dictionary<string, List<Func<IEnumerator>>>(), "modding_api_scene_bundle_", 0.5f, 0f);
			yield return DoPreloadScenes(shared, preloadedObjects, sceneHooks, "", 0.5f, 0.5f);
			repackBundle.Unload(unloadAllLoadedObjects: true);
		}

		// Full Scene Mode
		private IEnumerator DoPreloadScenes(
		Dictionary<string, List<(ModLoader.ModInstance Mod, List<string> Preloads)>> toPreload,
		IDictionary<ModLoader.ModInstance, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects,
		Dictionary<string, List<Func<IEnumerator>>> sceneHooks,
		string scenePrefix = "",
		float progressAlpha = 1f,
		float progressBeta = 0f)
		{
			List<string> sceneNames = toPreload.Keys.Union(sceneHooks.Keys).ToList();
			int totalScenes = sceneNames.Count;

			for (int i = 0; i < totalScenes; i++)
			{
				string sceneName = sceneNames[i];

				AsyncOperation loadOp = USceneManager.LoadSceneAsync(scenePrefix + sceneName, LoadSceneMode.Additive);
				yield return loadOp;

				yield return GetPreloadObjectsOperation(sceneName);

				AsyncOperation unloadOp = USceneManager.UnloadSceneAsync(scenePrefix + sceneName);
				yield return unloadOp;

				progressBar.Progress = (((float)(i + 1) / totalScenes) * progressAlpha) + progressBeta;

				if (Application.platform == RuntimePlatform.Android)
				{
					yield return Resources.UnloadUnusedAssets();

					GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
					GC.WaitForPendingFinalizers();
					GC.Collect();

					float preloadDelay = ModManagerSettings.PreloadingRate switch
					{
						0 => 1.0f,
						1 => 0.5f,
						2 => 0.0f,
						_ => 0.0f
					};

					if (preloadDelay > 0)
					{
						yield return new WaitForSecondsRealtime(preloadDelay);
					}
				}
			}

			progressBar.Progress = 1f * progressAlpha + progressBeta;

			Dictionary<string, GameObject> GetModScenePreloadedObjects(ModLoader.ModInstance mod, string sName)
			{
				if (!preloadedObjects.TryGetValue(mod, out var value2))
				{
					value2 = (preloadedObjects[mod] = new Dictionary<string, Dictionary<string, GameObject>>());
				}
				if (!value2.TryGetValue(sName, out var value3))
				{
					value3 = (value2[sName] = new Dictionary<string, GameObject>());
				}
				return value3;
			}

			IEnumerator GetPreloadObjectsOperation(string sName)
			{
				Scene sceneByName = USceneManager.GetSceneByName(scenePrefix + sName);
				GameObject[] rootObjects = sceneByName.GetRootGameObjects();
				for (int j = 0; j < rootObjects.Length; j++)
				{
					rootObjects[j].SetActive(value: false);
				}

				if (sceneHooks.TryGetValue(sceneByName.name, out var value2))
				{
					IEnumerator[] array2 = value2.Select((Func<IEnumerator> x) => x()).ToArray();
					for (int num3 = 0; num3 < array2.Length; num3++)
					{
						yield return array2[num3];
					}
				}

				if (!toPreload.TryGetValue(sName, out List<(ModLoader.ModInstance, List<string>)> value3))
				{
					yield break;
				}

				foreach (var (modInstance, list) in value3)
				{
					Logger.APILogger.LogFine("Fetching objects for mod \"" + modInstance.Mod.GetName() + "\"");
					Dictionary<string, GameObject> dictionary = GetModScenePreloadedObjects(modInstance, sName);

					foreach (string item2 in list)
					{
						Logger.APILogger.LogFine("Fetching object \"" + item2 + "\"");
						GameObject gameObjectFromArray;
						try
						{
							gameObjectFromArray = UnityExtensions.GetGameObjectFromArray(rootObjects, item2);
						}
						catch (ArgumentException)
						{
							Logger.APILogger.LogWarn("Invalid GameObject name " + item2);
							continue;
						}

						if (gameObjectFromArray == null)
						{
							Logger.APILogger.LogWarn("Could not find object \"" + item2 + "\" in scene \"" + sName + "\", requested by mod `" + modInstance.Mod.GetName() + "`");
						}
						else
						{
							gameObjectFromArray = UnityEngine.Object.Instantiate(gameObjectFromArray);
							UnityEngine.Object.DontDestroyOnLoad(gameObjectFromArray);
							gameObjectFromArray.SetActive(value: false);
							dictionary[item2] = gameObjectFromArray;
						}
					}
				}
			}
		}

		private IEnumerator CleanUpPreloading()
		{
			Logger.APILogger.LogDebug("Preload done: Returning to main menu!");
			ModLoader.LoadState |= ModLoader.ModLoadState.Preloaded;
			yield return USceneManager.LoadSceneAsync("Quit_To_Menu");
			while (USceneManager.GetActiveScene().name != "Menu_Title")
			{
				yield return new WaitForEndOfFrame();
			}
			UnityEngine.Object.Destroy(progressBar);
		}

		private static void UnmuteAllAudio()
		{
			AudioListener.pause = false;
		}
	}
}