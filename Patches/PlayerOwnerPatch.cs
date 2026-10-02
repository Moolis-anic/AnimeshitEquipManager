using AnimeshitEquipManager.Modules;
using EFT;
using HarmonyLib;

namespace AnimeshitEquipManager.Patches
{
	[HarmonyPatch(typeof(Player), "Init")]
	internal static class PlayerOwnerPatch
	{
		private static void Prefix(Player __instance, bool aiControlled)
		{
			Owners.Register(__instance, aiControlled);
		}

		private static void Postfix()
		{
			// Player.Init 完成后 owner 信息（IsAI / IsYourPlayer / 缓存）才就绪。
			// 重解析所有已追踪视图，纠正创建期被判定为 Unknown 的装备槽。
			Visibility.RefreshAll();
		}
	}
}