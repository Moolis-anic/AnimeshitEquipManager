using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using EFT.Visual;
using UnityEngine;

namespace AnimeshitEquipManager.Modules
{
	internal static class Visibility
	{
		internal static void Track(PlayerBody.SlotView view)
		{
			if (view == null || !Visibility.Slots.Contains(view.EquipmentSlot))
			{
				return;
			}
			Visibility.Views.Add(view);
			Visibility.Refresh(view);
		}

		private static void Refresh(PlayerBody.SlotView view)
		{
			if (Visibility.IsHidden(Owners.Resolve(view), view.EquipmentSlot))
			{
				Visibility.Apply(view);
				return;
			}
			Visibility.Restore(view);
		}

		private static void HideRenderer(Renderer renderer, Dictionary<Renderer, bool> states)
		{
			if (renderer == null) return;
			if (!states.ContainsKey(renderer))
			{
				states.Add(renderer, renderer.forceRenderingOff);
			}
			renderer.forceRenderingOff = true;
		}

		private static void Apply(PlayerBody.SlotView view)
		{
			Dictionary<Renderer, bool> dictionary;
			if (!Visibility.Saved.TryGetValue(view, out dictionary))
			{
				Visibility.Saved.Add(view, dictionary = new Dictionary<Renderer, bool>());
			}
			if (view.Renderers != null)
			{
				Renderer[] array = view.Renderers;
				for (int i = 0; i < array.Length; i++)
				{
					Visibility.HideRenderer(array[i], dictionary);
				}
			}
			if (view.Dresses != null)
			{
				foreach (Dress dress in view.Dresses)
				{
					if (dress != null && dress.Renderers != null)
					{
						Renderer[] array = dress.Renderers;
						for (int j = 0; j < array.Length; j++)
						{
							Visibility.HideRenderer(array[j], dictionary);
						}
					}
				}
			}
		}

		internal static void Restore(PlayerBody.SlotView view)
		{
			Dictionary<Renderer, bool> dictionary;
			if (!Visibility.Saved.TryGetValue(view, out dictionary))
			{
				return;
			}
			foreach (KeyValuePair<Renderer, bool> keyValuePair in dictionary)
			{
				if (keyValuePair.Key != null)
				{
					keyValuePair.Key.forceRenderingOff = keyValuePair.Value;
				}
			}
			Visibility.Saved.Remove(view);
		}

		internal static void Forget(PlayerBody.SlotView view)
		{
			Visibility.Restore(view);
			Visibility.Views.Remove(view);
		}

		private static HashSet<EquipmentSlot> SetFor(Audience audience)
		{
			switch (audience)
			{
				case Audience.Player: return PlayerHidden;
				case Audience.AI: return AIHidden;
				case Audience.Teammate: return TeammateHidden;
				default: return null;
			}
		}

		internal static bool IsHidden(Audience audience, EquipmentSlot slot)
		{
			var set = SetFor(audience);
			return set != null && set.Contains(slot);
		}

		internal static void Toggle(Audience audience, EquipmentSlot slot)
		{
			if (!Slots.Contains(slot)) return;
			var set = SetFor(audience);
			if (set == null) return;

			if (!set.Add(slot)) set.Remove(slot);

			foreach (var view in Views) Refresh(view);

			var log = EquipManagerPlugin.Log;
			if (log != null)
			{
				log.LogInfo($"{audience} {slot}: {(set.Contains(slot) ? "hidden" : "visible")}");
			}
		}

		/// <summary>
		/// Re-resolve every tracked SlotView. Cheap; call it whenever owner
		/// information may have become available (Player.Init, toggle, menu open).
		/// </summary>
		internal static void RefreshAll()
		{
			foreach (PlayerBody.SlotView view in Views)
			{
				Refresh(view);
			}
		}

		internal static void Reset()
		{
			Visibility.PlayerHidden.Clear();
			Visibility.AIHidden.Clear();
			Visibility.TeammateHidden.Clear();
			foreach (PlayerBody.SlotView slotView in Visibility.Views)
			{
				Visibility.Restore(slotView);
			}
			Visibility.Views.Clear();
			Visibility.Saved.Clear();
			Owners.Reset();
		}

		private static readonly HashSet<EquipmentSlot> PlayerHidden = new HashSet<EquipmentSlot>();
		private static readonly HashSet<EquipmentSlot> AIHidden = new HashSet<EquipmentSlot>();
		private static readonly HashSet<EquipmentSlot> TeammateHidden = new HashSet<EquipmentSlot>();

		internal static readonly HashSet<EquipmentSlot> Slots = new HashSet<EquipmentSlot>
		{
			(EquipmentSlot)12, (EquipmentSlot)11, (EquipmentSlot)14,
			(EquipmentSlot)6,  (EquipmentSlot)7,  (EquipmentSlot)10,
			(EquipmentSlot)9,  (EquipmentSlot)4
		};

		private static readonly HashSet<PlayerBody.SlotView> Views = new HashSet<PlayerBody.SlotView>();
		private static readonly Dictionary<PlayerBody.SlotView, Dictionary<Renderer, bool>> Saved =
			new Dictionary<PlayerBody.SlotView, Dictionary<Renderer, bool>>();
	}
}