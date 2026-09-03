using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace Modding.Patches
{
    public class AotContractResolver : DefaultContractResolver
    {
        /// <summary>
        ///     AotContractResolver with the default settings.
        /// </summary>
        public static readonly AotContractResolver Instance = new AotContractResolver();

        protected override IValueProvider CreateMemberValueProvider(MemberInfo member)
        {
            return new ReflectionValueProvider(member);
        }
    }
}
