using AnimeshitEquipManager.Modules;
using EFT;
using HarmonyLib;

namespace AnimeshitEquipManager.Patches
{
	[HarmonyPatch(typeof(PlayerBody.SlotView), "CreateAndParent")]
	internal static class ModelCreatedPatch
	{
		private static void Postfix(PlayerBody.SlotView __instance)
		{
			Visibility.Track(__instance);
		}
	}

	[HarmonyPatch(typeof(PlayerBody.SlotView), "DestroyCurrentModel")]
	internal static class ModelReturnedPatch
	{
		private static void Prefix(PlayerBody.SlotView __instance)
		{
			Visibility.Restore(__instance);
		}
	}

	[HarmonyPatch(typeof(PlayerBody.SlotView), "Dispose")]
	internal static class SlotDisposedPatch
	{
		private static void Prefix(PlayerBody.SlotView __instance)
		{
			Visibility.Forget(__instance);
		}
	}
}