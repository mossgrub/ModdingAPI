using System;
using JetBrains.Annotations;

namespace Modding.Patches
{
	[UsedImplicitly]
	public class RemoveMethodCall : Attribute
	{
		public RemoveMethodCall(string type, string method)
		{
		}
	}
}
