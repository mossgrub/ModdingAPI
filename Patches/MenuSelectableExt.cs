using GlobalEnums;
using UnityEngine.UI;

namespace Modding.Patches
{
	public static class MenuSelectableExt
	{
		public static void SetDynamicMenuCancel(this MenuSelectable ms, MenuScreen to)
		{
			ms.cancelAction = CancelAction.GoToExtrasMenu;
			(ms as MenuSelectable).customCancelAction = delegate
			{
				UIManager obj = UIManager.instance;
				obj.StartMenuAnimationCoroutine(obj.GoToDynamicMenu(to));
			};
		}
	}
}
