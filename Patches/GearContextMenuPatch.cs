using System;
using AnimeshitEquipManager.Localization;
using AnimeshitEquipManager.Modules;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace AnimeshitEquipManager.Patches
{
	[HarmonyPatch(typeof(ItemUiContext), "GetItemContextInteractions")]
	internal static class GearContextMenuPatch
	{
		internal static string Key(Audience audience, EquipmentSlot slot)
		{
			return "AnimeshitEquipManager.Toggle." + audience + "." + slot;
		}

		[HarmonyPriority(0)]
		internal static void Postfix(
			ItemUiContext __instance,
			ItemContext itemContext,
			ContextInteractions<EItemInfoButton> __result)
		{
			if (__result == null || itemContext?.Item == null) return;

			SlotItemAddress slotAddress = itemContext.Item.CurrentAddress as SlotItemAddress;
			if (slotAddress == null) return;
			if (!(slotAddress.Slot.ParentItem is InventoryEquipment)) return;
			if (!Enum.TryParse(slotAddress.Slot.ID, out EquipmentSlot slot)) return;
			if (!Visibility.Slots.Contains(slot)) return;

			L10n.Refresh();

			Add(__instance, __result, Audience.Player, slot);
			Add(__instance, __result, Audience.AI, slot);
			if (PitTeamInterop.Available)
			{
				Add(__instance, __result, Audience.Teammate, slot);
			}
		}

		private static void Add(
			ItemUiContext context,
			ContextInteractions<EItemInfoButton> interactions,
			Audience audience,
			EquipmentSlot slot)
		{
			var o = L10n.Current;

			string key = Key(audience, slot);
			string label =
				(Visibility.IsHidden(audience, slot) ? o.Show : o.Hide)
				+ L10n.AudienceName(audience)
				+ L10n.SlotName(slot)
				+ o.Appearance;

			interactions._dynamicInteractions[key] = new DynamicContextInteraction(
				key, label,
				delegate
				{
					Visibility.Toggle(audience, slot);
					context.ContextMenu.Close();
				},
				null);
		}

		internal const string Id = "AnimeshitEquipManager.Toggle";
	}
}