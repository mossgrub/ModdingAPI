using System;
using JetBrains.Annotations;

namespace Modding.Patches
{
	[UsedImplicitly]
	internal class ReplaceMethodAttribute : Attribute
	{
		public ReplaceMethodAttribute(string type1, string method1, string[] params1, string type2, string method2, string[] params2)
		{
		}
	}
}
