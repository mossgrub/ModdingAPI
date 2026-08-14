using System.Reflection;
using Newtonsoft.Json.Serialization;

namespace Modding.Patches
{
    public class AotContractResolver : DefaultContractResolver
    {
        /// <summary>
        ///     AotContractResolver with the default settings, cached for reuse.
        /// </summary>
        public static readonly AotContractResolver Instance = new AotContractResolver();

        /// <inheritdoc />
        protected override IValueProvider CreateMemberValueProvider(MemberInfo member)
        {
            // Avoid Expression.Compile() on IL2CPP/AOT. Use direct reflection instead.
            return new ReflectionValueProvider(member);
        }
    }
}
