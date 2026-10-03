using System;
using System.Collections.Generic;
using BepInEx.Configuration;
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

			ConfigEntry<bool> entry = FindEntry(audience, slot);
			if (entry != null)
			{
				// Flip the persisted value; the runtime set is updated below.
				_settingFromCode = true;
				try { entry.Value = !entry.Value; }
				finally { _settingFromCode = false; }

				ApplySetting(audience, slot, entry.Value);
				RefreshAll();
				LogState(audience, slot, entry.Value);
				return;
			}

			// Fallback used only when the config has not been bound yet.
			HashSet<EquipmentSlot> set = SetFor(audience);
			if (set == null) return;

			if (!set.Add(slot)) set.Remove(slot);
			RefreshAll();
			LogState(audience, slot, set.Contains(slot));
		}

		/// <summary>
		/// Bind the per-audience slot visibility settings. Every slot defaults to hidden.
		/// The config entries are the source of truth; the runtime sets are kept in sync with them.
		/// </summary>
		internal static void Bind(ConfigFile config)
		{
			foreach (SlotSetting previous in SlotSettings)
			{
				previous.Entry.SettingChanged -= OnSlotSettingChanged;
			}
			SlotSettings.Clear();

			int audienceIndex = 0;
			foreach (Audience audience in ConfigAudiences)
			{
				int slotIndex = 0;
				foreach (KeyValuePair<EquipmentSlot, string> slot in ConfigSlots)
				{
					Audience capturedAudience = audience;
					EquipmentSlot capturedSlot = slot.Key;
					string displayName = slot.Value;

					string description =
						"Hide the " + audience + " " + displayName + " model. " +
						"Default value: true (hidden). " +
						"Allowed values: true (hidden) / false (visible).";

					ConfigEntry<bool> entry = config.Bind(
						ConfigSection,
						audience + " " + displayName,
						true,
						new ConfigDescription(
							description,
							null,
							new object[]
							{
								new { Order = audienceIndex * 100 + slotIndex * 10, Advanced = false }
							}));

					entry.SettingChanged += OnSlotSettingChanged;
					SlotSettings.Add(new SlotSetting(capturedAudience, capturedSlot, entry));

					ApplySetting(capturedAudience, capturedSlot, entry.Value);

					slotIndex++;
				}

				audienceIndex++;
			}
		}

		private static void OnSlotSettingChanged(object sender, EventArgs args)
		{
			if (_settingFromCode) return;

			ConfigEntry<bool> entry = sender as ConfigEntry<bool>;
			if (entry == null) return;

			SlotSetting setting = null;
			foreach (SlotSetting candidate in SlotSettings)
			{
				if (ReferenceEquals(candidate.Entry, entry)) { setting = candidate; break; }
			}
			if (setting == null) return;

			ApplySetting(setting.Audience, setting.Slot, entry.Value);
			RefreshAll();
			LogState(setting.Audience, setting.Slot, entry.Value);
		}

		private static void ApplySetting(Audience audience, EquipmentSlot slot, bool hidden)
		{
			HashSet<EquipmentSlot> set = SetFor(audience);
			if (set == null) return;
			if (hidden) set.Add(slot);
			else set.Remove(slot);
		}

		private static ConfigEntry<bool> FindEntry(Audience audience, EquipmentSlot slot)
		{
			foreach (SlotSetting setting in SlotSettings)
			{
				if (setting.Audience == audience && setting.Slot == slot) return setting.Entry;
			}
			return null;
		}

		private static void LogState(Audience audience, EquipmentSlot slot, bool hidden)
		{
			var log = EquipManagerPlugin.Log;
			if (log != null)
			{
				log.LogInfo($"{audience} {slot}: {(hidden ? "hidden" : "visible")}");
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

		private const string ConfigSection = "Slot Visibility";

		private static readonly Audience[] ConfigAudiences =
		{
			Audience.Player, Audience.AI, Audience.Teammate
		};

		// Display names are fixed English strings, so config keys stay stable regardless of
		// the game language or enum name changes.
		private static readonly KeyValuePair<EquipmentSlot, string>[] ConfigSlots =
		{
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)11, "Headwear"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)12, "Earpiece"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)10, "Face Cover"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)9,  "Eyewear"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)7,  "Armor Vest"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)6,  "Tactical Vest"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)4,  "Backpack"),
			new KeyValuePair<EquipmentSlot, string>((EquipmentSlot)14, "Armband"),
		};

		private static readonly List<SlotSetting> SlotSettings = new List<SlotSetting>();

		private static bool _settingFromCode;

		private sealed class SlotSetting
		{
			internal readonly Audience Audience;
			internal readonly EquipmentSlot Slot;
			internal readonly ConfigEntry<bool> Entry;

			internal SlotSetting(Audience audience, EquipmentSlot slot, ConfigEntry<bool> entry)
			{
				Audience = audience;
				Slot = slot;
				Entry = entry;
			}
		}
	}
}