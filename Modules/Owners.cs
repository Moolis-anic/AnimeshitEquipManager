using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;

namespace AnimeshitEquipManager.Modules
{
	internal static class Owners
	{
		internal static void Register(Player player, bool aiControlled)
		{
			Players.Remove(player);
			Players.Add(player, new Owner
			{
				Audience = aiControlled ? Audience.AI : Audience.Player
			});
		}

		internal static Audience Resolve(PlayerBody.SlotView view)
		{
			Player player = ((view._playerBody != null) ? view._playerBody.GetComponentInParent<Player>() : null);
			Slot slot = view._slot;
			InventoryEquipment inventoryEquipment = ((slot != null) ? slot.ParentItem : null) as InventoryEquipment;
			string text = ((inventoryEquipment != null) ? inventoryEquipment.Id : null);

			if (player != null)
			{
				Audience audience;
				if (player.IsYourPlayer)
				{
					audience = Audience.Player;
				}
				else if (PitTeamInterop.IsTeammate(player))
				{
					audience = Audience.Teammate;
				}
				else if (Owners.Players.TryGetValue(player, out Owners.Owner owner))
				{
					audience = owner.Audience;
				}
				else if (player.IsAI)
				{
					audience = Audience.AI;
				}
				else
				{
					audience = Audience.Unknown;
				}

				if (!string.IsNullOrEmpty(text) && audience != Audience.Unknown)
				{
					Owners.EquipmentOwners[text] = audience;
				}
				return audience;
			}

			// player == null (view._playerBody == null)
			ItemUiContext instance = ItemUiContext.Instance;
			InventoryEquipment inventoryEquipment2;
			if (instance == null)
			{
				inventoryEquipment2 = null;
			}
			else
			{
				IClientSession clientSession = instance.ClientSession;
				if (clientSession == null)
				{
					inventoryEquipment2 = null;
				}
				else
				{
					Profile profile = clientSession.Profile;
					if (profile == null)
					{
						inventoryEquipment2 = null;
					}
					else
					{
						Inventory inventory = profile.Inventory;
						inventoryEquipment2 = ((inventory != null) ? inventory.Equipment : null);
					}
				}
			}
			InventoryEquipment inventoryEquipment3 = inventoryEquipment2;
			if (!string.IsNullOrEmpty(text) && inventoryEquipment3 != null && text == inventoryEquipment3.Id)
			{
				return Audience.Player;
			}
			Audience audience2;
			if (string.IsNullOrEmpty(text) || !Owners.EquipmentOwners.TryGetValue(text, out audience2))
			{
				return Audience.Unknown;
			}
			return audience2;
		}

		internal static void Reset()
		{
			Players = new ConditionalWeakTable<Player, Owner>();
			EquipmentOwners.Clear();
		}

		private static ConditionalWeakTable<Player, Owner> Players = new ConditionalWeakTable<Player, Owner>();

		private static readonly Dictionary<string, Audience> EquipmentOwners = new Dictionary<string, Audience>();

		private sealed class Owner
		{
			internal Audience Audience;
		}
	}
}