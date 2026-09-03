using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Modding.Patches
{
    public class ShouldSerializeContractResolver : AotContractResolver
    {
        /// <summary>
        /// Instance to cache reflection calls.
        /// </summary>
        public static new readonly ShouldSerializeContractResolver Instance = new ShouldSerializeContractResolver();

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty prop = base.CreateProperty(member, memberSerialization);

            if (member?.DeclaringType?.Assembly.FullName.StartsWith("UnityEngine") ?? false)
                prop.Ignored = true;

            return prop;
        }
    }
}