using System;
using AnimeshitEquipManager.Modules;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace AnimeshitEquipManager.Patches
{
	[HarmonyPatch(
		typeof(PlayerBody.SlotView),
		MethodType.Constructor,
		new[]
		{
			typeof(PlayerBody),
			typeof(Slot),
			typeof(Transform),
			typeof(EquipmentSlot),
			typeof(Slot),
			typeof(Transform),
			typeof(bool)
		})]
	internal static class TrackSlotCtorPatch
	{
		private static void Postfix(PlayerBody.SlotView __instance)
		{
			Visibility.Track(__instance);
		}
	}
}