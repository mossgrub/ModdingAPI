using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.Annotations;
using Modding.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modding
{
	[PublicAPI]
	internal static class ModLoader
	{
		[Flags]
		public enum ModLoadState
		{
			NotStarted = 0,
			Started = 1,
			Preloaded = 2,
			Loaded = 4
		}

		public class ModInstance
		{
			public IMod Mod;

			public string Name;

			public ModErrorState? Error;

			public bool Enabled;
		}

		public enum ModErrorState
		{
			Construct = 0,
			Duplicate = 1,
			Initialize = 2,
			Unload = 3
		}

		public static ModLoadState LoadState = ModLoadState.NotStarted;

		private static ModVersionDraw modVersionDraw;

		public static Dictionary<Type, ModInstance> ModInstanceTypeMap { get; private set; } = new Dictionary<Type, ModInstance>();

		public static Dictionary<string, ModInstance> ModInstanceNameMap { get; private set; } = new Dictionary<string, ModInstance>();

		public static HashSet<ModInstance> ModInstances { get; private set; } = new HashSet<ModInstance>();

		private static bool TryAddModInstance(Type ty, ModInstance mod)
		{
			if (ModInstanceNameMap.ContainsKey(mod.Name))
			{
				Logger.APILogger.LogWarn("Found multiple mods with name " + mod.Name + ".");
				mod.Error = ModErrorState.Duplicate;
				ModInstanceNameMap[mod.Name].Error = ModErrorState.Duplicate;
				ModInstanceTypeMap[ty] = mod;
				ModInstances.Add(mod);
				return false;
			}
			ModInstanceTypeMap[ty] = mod;
			ModInstanceNameMap[mod.Name] = mod;
			ModInstances.Add(mod);
			return true;
		}

		private static bool IsIl2CppAotRuntime()
		{
		#if ENABLE_IL2CPP
			return true;
		#else
			return false;
		#endif
		}

		private static Assembly LoadAssemblySafely(string path)
		{
			try
			{
				return AssemblyLoader.LoadAssembly(path);
			}
			catch (Exception ex)
			{
				Logger.APILogger.LogError($"Failed to load assembly from {path}: {ex.Message}");
				return null;
			}
		}

		public static IEnumerator LoadModsInit(GameObject coroutineHolder)
        {
            try
            {
                Logger.InitializeFileStream();
            }
            catch (Exception message)
            {
                Logger.APILogger.LogError(message);
            }

            HybridCLRInitializer.Initialize();
            AssemblyLoader.Initialize();
            DetourBridge.Initialize();

            global::ModManagerSettings.Load();
            if (global::ModManagerSettings.GameVanillaMode)
            {
                Logger.APILogger.Log("Game vanilla mode enabled.");
                LoadState |= ModLoadState.Loaded;
                UnityEngine.Object.Destroy(coroutineHolder);
                yield break;
            }

            Logger.APILogger.Log("Starting mod loading");

            string text2 = null;

            #if UNITY_EDITOR
                text2 = @"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed\Mods";
                Logger.APILogger.Log("Loading mods from: " + text2);
            #elif UNITY_ANDROID
                text2 = Path.Combine(Application.persistentDataPath, "Mods");
                Logger.APILogger.Log("Loading mods from: " + text2);
            #else
                string text = SystemInfo.operatingSystemFamily switch
                {
                    OperatingSystemFamily.Windows => Path.Combine(Application.dataPath, "Managed"), 
                    OperatingSystemFamily.MacOSX => Path.Combine(Application.dataPath, "Resources", "Data", "Managed"), 
                    OperatingSystemFamily.Linux => Path.Combine(Application.dataPath, "Managed"), 
                    OperatingSystemFamily.Other => null, 
                    _ => throw new ArgumentOutOfRangeException(), 
                };

                if (text != null)
                {
                    text2 = Path.Combine(text, "Mods");
                }
            #endif

            if (string.IsNullOrEmpty(text2))
            {
                LoadState |= ModLoadState.Loaded;
                UnityEngine.Object.Destroy(coroutineHolder);
                yield break;
            }

            if (!Directory.Exists(text2))
            {
                Directory.CreateDirectory(text2);
            }

            try
        {
            ModHooks.LoadGlobalSettings();
        }
        catch (Exception ex)
        {
            Logger.APILogger.LogError($"Failed to load global settings: {ex.Message}");
        }

            Logger.ClearOldModlogs();
            Logger.APILogger.LogDebug("Loading assemblies and constructing mods");
			string[] files = Directory.GetDirectories(text2).Except(new string[1] { Path.Combine(text2, "Disabled") }).SelectMany((string d) => Directory.GetFiles(d, "*.dll"))
                .ToArray();
            Logger.APILogger.LogDebug(string.Join(",\n", files));
            List<Assembly> list = new List<Assembly>(files.Length);
            string[] array = files;
            foreach (string text3 in array)
            {
                Logger.APILogger.LogDebug("Loading assembly `" + text3 + "`");
                try
                {
                    list.Add(LoadAssemblySafely(text3));
                }
                catch (FileLoadException arg)
                {
                    Logger.APILogger.LogError($"Unable to load assembly - {arg}");
                }
                catch (BadImageFormatException arg2)
                {
                    Logger.APILogger.LogError($"Assembly is bad image. {arg2}");
                }
                catch (PathTooLongException)
                {
                    Logger.APILogger.LogError("Unable to load, path to dll is too long!");
                }
            }
            foreach (Assembly item in list)
            {
                Logger.APILogger.LogDebug("Loading mods in assembly `" + item.FullName + "`");
                bool flag = false;
                try
                {
                    foreach (Type item2 in item.GetTypesSafely())
                    {
                        if (!item2.IsClass || item2.IsAbstract || !item2.IsSubclassOf(typeof(Mod)))
                        {
                            continue;
                        }
                        flag = true;
                        Logger.APILogger.LogDebug("Constructing mod `" + item2.FullName + "`");
                        try
                        {
                            if (item2.GetConstructor(Type.EmptyTypes)?.Invoke(Array.Empty<object>()) is Mod mod)
                            {
                                TryAddModInstance(item2, new ModInstance
                                {
                                    Mod = mod,
                                    Enabled = false,
                                    Error = null,
                                    Name = mod.GetName()
                                });
                            }
                        }
                        catch (Exception message2)
                        {
                            Logger.APILogger.LogError(message2);
                            TryAddModInstance(item2, new ModInstance
                            {
                                Mod = null,
                                Enabled = false,
                                Error = ModErrorState.Construct,
                                Name = item2.Name
                            });
                        }
                    }
                }
                catch (Exception message3)
                {
                    Logger.APILogger.LogError(message3);
                }
                if (!flag)
                {
                    AssemblyName name = item.GetName();
                    Logger.APILogger.Log($"Assembly {name.Name} ({name.Version}) loaded with 0 mods");
                }
            }
            List<string> list2 = new List<string>();
            for (int num2 = 0; num2 < UnityEngine.SceneManagement.SceneManager.sceneCountInBuildSettings; num2++)
            {
                string scenePathByBuildIndex = SceneUtility.GetScenePathByBuildIndex(num2);
                list2.Add(Path.GetFileNameWithoutExtension(scenePathByBuildIndex));
            }
            ModInstance[] orderedMods = ModInstanceTypeMap.Values.OrderBy((ModInstance x) => x.Mod?.LoadPriority() ?? 0).ToArray();
            Dictionary<string, List<(ModInstance, List<string>)>> dictionary = new Dictionary<string, List<(ModInstance, List<string>)>>();
            Dictionary<ModInstance, Dictionary<string, Dictionary<string, GameObject>>> preloadedObjects = new Dictionary<ModInstance, Dictionary<string, Dictionary<string, GameObject>>>();
            Dictionary<string, List<Func<IEnumerator>>> dictionary2 = new Dictionary<string, List<Func<IEnumerator>>>();
            Logger.APILogger.Log("Creating mod preloads");
            GetPreloads(orderedMods, list2, dictionary, dictionary2);
            if (dictionary.Count > 0 || dictionary2.Count > 0)
            {
                Preloader orAddComponent = coroutineHolder.GetOrAddComponent<Preloader>();
                yield return orAddComponent.Preload(dictionary, preloadedObjects, dictionary2);
            }
            ModInstance[] array2 = orderedMods;
            foreach (ModInstance modInstance in array2)
            {
                ModErrorState? error = modInstance.Error;
                if (error.HasValue)
                {
                    Logger.APILogger.LogWarn($"Not loading mod {modInstance.Name}: error state {modInstance.Error}");
                    continue;
                }
                try
                {
                    preloadedObjects.TryGetValue(modInstance, out var value);
                    LoadMod(modInstance, updateModText: false, value);
                    if (!ModHooks.GlobalSettings.ModEnabledSettings.TryGetValue(modInstance.Name, out var value2))
                    {
                        value2 = true;
                    }
                    if (!modInstance.Error.HasValue && modInstance.Mod is ITogglableMod && !value2)
                    {
                        UnloadMod(modInstance, updateModText: false);
                    }
                }
                catch (Exception ex2)
                {
                    Logger.APILogger.LogError("Error: " + ex2);
                }
            }
            if (modVersionDraw == null)
            {
                GameObject gameObject = new GameObject();
                modVersionDraw = gameObject.AddComponent<ModVersionDraw>();
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
                UpdateModText();
                
                if (!ModManagerSettings.ModListDisplay)
                {
                    modVersionDraw.SetVisible(false, 0f);
                }
            }
            
            Logger.APILogger.Log("Finished loading mods:\n" + modVersionDraw.drawString);
            ModHooks.OnFinishedLoadingMods();
            LoadState |= ModLoadState.Loaded;
            new ModListMenu().InitMenuCreation();
            UnityEngine.Object.Destroy(coroutineHolder.gameObject);

            AssemblyLoader.SetupAssemblyResolve();
        }

		private static void GetPreloads(ModInstance[] orderedMods, List<string> scenes, Dictionary<string, List<(ModInstance, List<string> objectNames)>> toPreload, Dictionary<string, List<Func<IEnumerator>>> sceneHooks)
		{
			foreach (ModInstance modInstance in orderedMods)
			{
				if (modInstance.Error.HasValue)
				{
					continue;
				}
				Logger.APILogger.LogDebug("Checking preloads for mod \"" + modInstance.Mod.GetName() + "\"");
				List<(string, string)> list = null;
				try
				{
					list = modInstance.Mod.GetPreloadNames();
				}
				catch (Exception ex)
				{
					Logger.APILogger.LogError("Error getting preload names for mod " + modInstance.Name + "\n" + ex);
				}
				try
				{
					(string, Func<IEnumerator>)[] array = modInstance.Mod.PreloadSceneHooks();
					for (int j = 0; j < array.Length; j++)
					{
						var (key, item) = array[j];
						if (!sceneHooks.TryGetValue(key, out var value))
						{
							value = (sceneHooks[key] = new List<Func<IEnumerator>>());
						}
						value.Add(item);
					}
				}
				catch (Exception ex2)
				{
					Logger.APILogger.LogError("Error getting preload hooks for mod " + modInstance.Name + "\n" + ex2);
				}
				if (list == null)
				{
					continue;
				}
				Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
				foreach (var (text, text2) in list)
				{
					if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(text2))
					{
						Logger.APILogger.LogWarn("Mod `" + modInstance.Mod.GetName() + "` passed null values to preload");
						continue;
					}
					if (!scenes.Contains(text))
					{
						Logger.APILogger.LogWarn("Mod `" + modInstance.Mod.GetName() + "` attempted preload from non-existent scene `" + text + "`");
						continue;
					}
					if (!dictionary.TryGetValue(text, out var value2))
					{
						value2 = (dictionary[text] = new List<string>());
					}
					Logger.APILogger.LogFine("Found object `" + text + "." + text2 + "`");
					value2.Add(text2);
				}
				foreach (var (text4, list5) in dictionary)
				{
					if (!toPreload.TryGetValue(text4, out List<(ModInstance, List<string>)> value3))
					{
						value3 = (toPreload[text4] = new List<(ModInstance, List<string>)>());
					}
					Logger.APILogger.LogFine($"`{modInstance.Name}` preloads {list5.Count} objects in the `{text4}` scene");
					value3.Add((modInstance, list5));
					toPreload[text4] = value3;
				}
			}
		}

		private static void UpdateModText()
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("Modding API: " + ModHooks.ModVersion);
			foreach (ModInstance modInstance in ModInstances)
			{
				switch (modInstance.Error)
				{
				case null:
					if (modInstance.Enabled)
					{
						stringBuilder.AppendLine(modInstance.Name + " : " + modInstance.Mod.GetVersionSafe("ERROR"));
					}
					break;
				case ModErrorState.Construct:
					stringBuilder.AppendLine(modInstance.Name + " : Failed to call constructor! Check ModLog.txt");
					break;
				case ModErrorState.Duplicate:
					stringBuilder.AppendLine(modInstance.Name + " : Failed to load! Duplicate mod detected");
					break;
				case ModErrorState.Initialize:
					stringBuilder.AppendLine(modInstance.Name + " : Failed to initialize! Check ModLog.txt");
					break;
				case ModErrorState.Unload:
					stringBuilder.AppendLine(modInstance.Name + " : Failed to unload! Check ModLog.txt");
					break;
				default:
					throw new ArgumentOutOfRangeException();
				}
			}
			modVersionDraw.drawString = stringBuilder.ToString();
		}

		public static void SetModListDisplay(bool visible)
		{
			if (modVersionDraw != null)
			{
				modVersionDraw.SetVisible(visible, 0.25f);
			}
		}

		internal static void LoadMod(ModInstance mod, bool updateModText = true, Dictionary<string, Dictionary<string, GameObject>> preloadedObjects = null)
		{
			try
			{
				if (mod != null && !mod.Enabled)
				{
					ModErrorState? error = mod.Error;
					if (!error.HasValue)
					{
						mod.Enabled = true;
						mod.Mod.Initialize(preloadedObjects);
					}
				}
			}
			catch (Exception arg)
			{
				mod.Error = ModErrorState.Initialize;
				Logger.APILogger.LogError($"Failed to load Mod `{mod.Mod.GetName()}`\n{arg}");
			}
			if (updateModText)
			{
				UpdateModText();
			}
		}

		internal static void UnloadMod(ModInstance mod, bool updateModText = true)
		{
			try
			{
				if (mod != null && mod.Mod is ITogglableMod togglableMod && mod.Enabled)
				{
					ModErrorState? error = mod.Error;
					if (!error.HasValue)
					{
						mod.Enabled = false;
						togglableMod.Unload();
					}
				}
			}
			catch (Exception arg)
			{
				mod.Error = ModErrorState.Unload;
				Logger.APILogger.LogError($"Failed to unload Mod `{mod.Name}`\n{arg}");
			}
			if (updateModText)
			{
				UpdateModText();
			}
		}
	}
}
