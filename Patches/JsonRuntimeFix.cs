using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace Modding.Patches
{
    internal static class JsonRuntimeFix
    {
        private static bool _applied;

        public static void ApplyAotDefaults()
        {
            if (_applied)
                return;

            _applied = true;

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                ContractResolver = ShouldSerializeContractResolver.Instance,
                TypeNameHandling = TypeNameHandling.Auto,
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore
            };

            Logger.APILogger.Log("Newtonsoft.Json AOT defaults applied (reflection-based value provider).");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RuntimeInit()
        {
            ApplyAotDefaults();
        }
    }
}
