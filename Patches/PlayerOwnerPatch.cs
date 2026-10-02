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
			Visibility.RefreshAll();
		}
	}
}