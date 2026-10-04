using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.InventoryLogic;
using EFT.Visual;
using UnityEngine;
using AnimeshitEquipManager.Localization;

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
			Audience audience = Owners.Resolve(view);
			bool hidden = Visibility.IsHidden(audience, view.EquipmentSlot);
			LogTarget(view, audience, hidden);

			if (hidden)
			{
				Visibility.Apply(view);
				return;
			}
			Visibility.Restore(view);
		}

		/// <summary>
		/// Bounded diagnostic that shows which render targets are tracked and how they resolve.
		/// It separates "no target tracked" from "target tracked but resolved as Unknown",
		/// reports when a previously unknown target starts resolving, and dumps the ancestor chain
		/// of preview models so their host component can be identified from the log alone.
		/// </summary>
		private static void LogTarget(PlayerBody.SlotView view, Audience audience, bool hidden)
		{
			if (audience == Audience.Unknown)
			{
				// Previews of unrelated players stay unknown on purpose, so they are not logged.
				Unknown.Add(view);
				return;
			}

			if (Unknown.Remove(view))
			{
				EquipManagerPlugin.Log?.LogInfo("[Visibility] resolved slot=" + view.EquipmentSlot +
					" audience=" + audience + " hidden=" + hidden);
			}

			LogOnce(view, audience, hidden);
		}

		private static void LogOnce(PlayerBody.SlotView view, Audience audience, bool hidden)
		{
			if (_targetLogs >= MaxTargetLogs) return;

			// One line per (slot, audience, equipment): the same gear resolves the same way for every
			// model that renders it.
			string equipmentId = Owners.EquipmentIdOf(view);
			if (!TargetKeys.Add(view.EquipmentSlot + "|" + audience + "|" + equipmentId)) return;

			_targetLogs++;
			EquipManagerPlugin.Log?.LogInfo("[Visibility] slot=" + view.EquipmentSlot +
				" audience=" + audience + " hidden=" + hidden +
				" equipment=" + (equipmentId ?? "(none)"));
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

		private static void HideRenderers(Renderer[] renderers, Dictionary<Renderer, bool> states)
		{
			if (renderers == null) return;

			for (int i = 0; i < renderers.Length; i++)
			{
				HideRenderer(renderers[i], states);
			}
		}

		private static void Apply(PlayerBody.SlotView view)
		{
			Dictionary<Renderer, bool> dictionary;
			if (!Visibility.Saved.TryGetValue(view, out dictionary))
			{
				Visibility.Saved.Add(view, dictionary = new Dictionary<Renderer, bool>());
			}

			HideRenderers(view.Renderers, dictionary);

			if (view.Dresses != null)
			{
				foreach (Dress dress in view.Dresses)
				{
					if (dress != null) HideRenderers(dress.Renderers, dictionary);
				}
			}

			// The renderer list of a slot can be rebuilt (another LOD, a dress swap, a model reuse
			// between screens) without the view being recreated, so the whole model of the slot is
			// hidden as well.
			GameObject model = view.Model;
			if (model != null)
			{
				HideRenderers(model.GetComponentsInChildren<Renderer>(true), dictionary);
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
			Unknown.Remove(view);
		}

		/// <summary>
		/// The flag a single audience owns. A set flag means the model is hidden for it.
		/// </summary>
		private static AudienceMask BitOf(Audience audience)
		{
			switch (audience)
			{
				case Audience.Player: return AudienceMask.Player;
				case Audience.AI: return AudienceMask.AI;
				case Audience.Teammate: return AudienceMask.Teammate;
				default: return 0;
			}
		}

		internal static AudienceMask HiddenMask(EquipmentSlot slot)
		{
			AudienceMask mask;
			return Hidden.TryGetValue(slot, out mask) ? mask : DefaultMask;
		}

		internal static bool IsHidden(Audience audience, EquipmentSlot slot)
		{
			AudienceMask bit = BitOf(audience);
			return bit != 0 && (HiddenMask(slot) & bit) != 0;
		}

		/// <summary>
		/// Flip the flag of one audience, persist it and refresh every tracked view.
		/// </summary>
		internal static void Toggle(Audience audience, EquipmentSlot slot)
		{
			if (!Slots.Contains(slot)) return;

			AudienceMask bit = BitOf(audience);
			if (bit == 0) return;

			AudienceMask value = HiddenMask(slot) ^ bit;

			ConfigEntry<AudienceMask> entry = FindEntry(slot);
			if (entry != null)
			{
				// Flip the persisted value; the runtime state is updated below.
				_settingFromCode = true;
				try { entry.Value = value; }
				finally { _settingFromCode = false; }
			}

			ApplySetting(slot, value);
			RefreshAll();
			LogState(slot, value);
		}

		/// <summary>
		/// Bind the slot visibility settings: one localized category with a single multi-select
		/// entry per slot. Every entry is a [Flags] mask, so the ConfigurationManager draws one
		/// checkbox per audience and the Teammate checkbox is always available, whether
		/// PitFireTeam is installed or not. Values are migrated from the legacy flat section or
		/// from any previously shipped layout and language, and every slot defaults to hidden.
		/// </summary>
		internal static void Bind(ConfigFile config)
		{
			_config = config;
			_fileValues = ReadFileValues(config);

			foreach (SlotSetting previous in SlotSettings)
			{
				previous.Entry.SettingChanged -= OnSlotSettingChanged;
			}
			SlotSettings.Clear();
			Hidden.Clear();

			BindSlots();

			PruneForeignSections();
			LogConfigSummary();
		}

		/// <summary>
		/// Re-assert the visibility of every tracked view. Ownership information can arrive without a
		/// user action (Hideout load, raid start, a profile screen filling its source), and the game
		/// re-enables renderers whenever it rebuilds or re-syncs a model, so the hidden state is
		/// verified at a fast cadence instead of being written once.
		/// </summary>
		internal static void Reconcile()
		{
			if (Views.Count == 0) return;

			int now = Environment.TickCount;

			// Fast while something is still unknown (a preview that has to catch up with its source)
			// or while something is hidden (the game may have re-enabled those renderers).
			int interval = (Unknown.Count > 0 || Saved.Count > 0)
				? VerifyingReconcileMs
				: ReconcileMs;

			if (unchecked(now - _lastReconcileTick) < interval) return;
			_lastReconcileTick = now;

			RefreshAll();
		}

		/// <summary>
		/// Rebind the slot entries under the names of the current language, keeping values.
		/// Called when the detected game language changes.
		/// </summary>
		internal static void RebindForLanguage()
		{
			if (_config == null) return;

			try
			{
				string section = SectionForConfig();
				var rebound = new List<SlotSetting>();

				foreach (SlotSetting setting in SlotSettings)
				{
					string key = KeyFor(setting.Slot);
					ConfigDefinition current = setting.Entry.Definition;

					if (section == current.Section && key == current.Key)
					{
						rebound.Add(setting);
						continue;
					}

					AudienceMask value = setting.Entry.Value;
					setting.Entry.SettingChanged -= OnSlotSettingChanged;
					_config.Remove(current);

					ConfigEntry<AudienceMask> entry = _config.Bind(
						section, key, value, BuildDescription(setting.Slot));
					entry.SettingChanged += OnSlotSettingChanged;
					rebound.Add(new SlotSetting(setting.Slot, entry));
				}

				SlotSettings.Clear();
				SlotSettings.AddRange(rebound);
				_config.Save();
			}
			catch (Exception ex)
			{
				WarnConfigOnce("could not relabel the slot config after a language change: " + ex.Message);
			}
		}

		private static void BindSlots()
		{
			string section = SectionForConfig();

			for (int i = 0; i < ConfigSlots.Length; i++)
			{
				EquipmentSlot slot = ConfigSlots[i].Key;
				string englishName = ConfigSlots[i].Value;
				string key = KeyFor(slot);

				AudienceMask value = ResolveValue(section, key, slot, englishName);

				ConfigEntry<AudienceMask> entry = _config.Bind(
					section,
					key,
					value,
					BuildDescription(slot));

				entry.SettingChanged += OnSlotSettingChanged;
				SlotSettings.Add(new SlotSetting(slot, entry));
				ApplySetting(slot, entry.Value);
			}
		}

		private static ConfigDescription BuildDescription(EquipmentSlot slot)
		{
			string description =
				"Audiences the " + EnglishSlotName(slot) + " model is hidden from. " +
				"Tick an audience to hide the model for it; tick all three to hide it everywhere. " +
				"Default value: Player, AI, Teammate (hidden for everyone). " +
				"Allowed values: none, or any combination of Player / AI / Teammate.";

			return new ConfigDescription(
				description,
				null,
				new object[] { new { Order = OrderOf(slot), Advanced = false } });
		}

		/// <summary>
		/// Resolve the initial value for a slot: the current localized entry, then the legacy
		/// flat section, then the boolean entries of any previously shipped layout or language,
		/// then the hidden-by-default mask.
		/// </summary>
		private static AudienceMask ResolveValue(
			string section, string key, EquipmentSlot slot, string englishName)
		{
			string raw;
			AudienceMask parsed;

			if (TryGetFileValue(section, key, out raw) && TryParseMask(raw, out parsed)) return parsed;

			string slotId = ((int)slot).ToString();

			// Legacy flat section: "<Audience> <English slot name>" = bool.
			AudienceMask legacy = LegacyMask(
				audience => LegacySection,
				audience => audience + " " + englishName);

			foreach (LanguageOptions options in L10n.GetAllLanguageOptions())
		{
				LanguageOptions captured = options;

				// Previously shipped layout: one section per audience, keyed by localized names.
				legacy |= LegacyMask(
					audience => AudienceNameOf(captured, audience),
					audience => SlotNameOf(captured, slotId, EnglishSlotName(slot)));

				// The current layout under another language, written by an earlier run.
				string otherSection = SlotGroupNameOf(captured);
				string otherKey = SlotNameOf(captured, slotId, englishName);
				if (otherSection == section && otherKey == key) continue;

				if (TryGetFileValue(otherSection, otherKey, out raw) && TryParseMask(raw, out parsed))
				{
					return parsed;
				}
			}

			return legacy != 0 ? legacy : DefaultMask;
		}

		/// <summary>
		/// Compose a mask from the boolean entries of one old-style layout, where every audience
		/// had its own section and every slot its own key.
		/// </summary>
		private static AudienceMask LegacyMask(
			Func<Audience, string> sectionName, Func<Audience, string> keyName)
		{
			AudienceMask mask = 0;

			foreach (Audience audience in AllAudiences)
			{
				bool hidden;
				if (TryGetLegacyBool(sectionName(audience), keyName(audience), out hidden) && hidden)
				{
					mask |= BitOf(audience);
				}
			}

			return mask;
		}

		private static bool TryGetLegacyBool(string section, string key, out bool hidden)
		{
			hidden = false;
			string raw;
			return TryGetFileValue(section, key, out raw) && bool.TryParse(raw, out hidden);
		}

		/// <summary>
		/// Parse a mask out of a raw config value. Values written by older versions are plain
		/// booleans, which map onto "hidden for everyone" / "hidden for nobody".
		/// </summary>
		private static bool TryParseMask(string raw, out AudienceMask mask)
		{
			mask = 0;
			if (string.IsNullOrEmpty(raw)) return false;

			raw = raw.Trim();
			if (raw.Length == 0) return false;

			try
			{
				mask = (AudienceMask)Enum.Parse(typeof(AudienceMask), raw, true);
				return true;
			}
			catch { }

			bool hidden;
			if (bool.TryParse(raw, out hidden))
			{
				mask = hidden ? DefaultMask : 0;
				return true;
			}

			return false;
		}

		private static bool TryGetFileValue(string section, string key, out string value)
		{
			value = null;
			return _fileValues != null && _fileValues.TryGetValue(section + "\n" + key, out value);
		}

		private static string SectionForConfig()
		{
			return SlotGroupNameOf(L10n.Current);
		}

		private static string SlotGroupNameOf(LanguageOptions options)
		{
			if (options != null && !string.IsNullOrEmpty(options.ConfigSlotGroup))
			{
				return options.ConfigSlotGroup.Trim();
			}
			return "Equipment Slot";
		}

		private static string KeyFor(EquipmentSlot slot)
		{
			return SlotNameOf(L10n.Current, ((int)slot).ToString(), EnglishSlotName(slot));
		}

		private static string AudienceNameOf(LanguageOptions options, Audience audience)
		{
			if (options != null)
			{
				string name = null;
				switch (audience)
				{
					case Audience.Player: name = options.AudiencePlayer; break;
					case Audience.AI: name = options.AudienceAI; break;
					case Audience.Teammate: name = options.AudienceTeammate; break;
				}
				if (!string.IsNullOrEmpty(name)) return name.Trim();
			}
			return audience.ToString();
		}

		private static string SlotNameOf(LanguageOptions options, string slotId, string englishFallback)
		{
			if (options != null && options.Slots != null)
			{
				string name;
				if (options.Slots.TryGetValue(slotId, out name) && !string.IsNullOrEmpty(name))
				{
					return name.Trim();
				}
			}
			return englishFallback;
		}

		private static string EnglishSlotName(EquipmentSlot slot)
		{
			for (int i = 0; i < ConfigSlots.Length; i++)
			{
				if (ConfigSlots[i].Key == slot) return ConfigSlots[i].Value;
			}
			return slot.ToString();
		}

		private static int OrderOf(EquipmentSlot slot)
		{
			for (int i = 0; i < ConfigSlots.Length; i++)
			{
				if (ConfigSlots[i].Key == slot) return i * 10;
			}
			return 0;
		}

		/// <summary>
		/// Read the raw config file so values from the legacy layout or from another language can
		/// be migrated. Unbound entries are not reachable through the ConfigFile API.
		/// </summary>
		private static Dictionary<string, string> ReadFileValues(ConfigFile config)
		{
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			try
			{
				string path = config.ConfigFilePath;
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return values;

				string section = string.Empty;
				foreach (string rawLine in File.ReadAllLines(path))
				{
					string line = rawLine.Trim();
					if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

					if (line.StartsWith("[") && line.EndsWith("]"))
					{
						section = line.Substring(1, line.Length - 2).Trim();
						continue;
					}

					int separator = line.IndexOf('=');
					if (separator <= 0) continue;

					string key = line.Substring(0, separator).Trim();
					string raw = line.Substring(separator + 1).Trim();
					if (key.Length == 0 || raw.Length == 0) continue;

					values[section + "\n" + key] = raw;
				}
			}
			catch { }
			return values;
		}

		/// <summary>
		/// Remove slot entries left over from other languages, from the old per-audience layout or
		/// from the legacy flat section, so the config file only keeps the single category of the
		/// current language while the values have already been migrated.
		/// </summary>
		private static void PruneForeignSections()
		{
			try
			{
				string keep = SectionForConfig();
				var stale = new List<ConfigDefinition>();

				foreach (ConfigDefinition definition in _config.Keys)
				{
					if (IsSlotEntry(definition) && definition.Section != keep)
					{
						stale.Add(definition);
					}
				}

				bool changed = stale.Count > 0;
				foreach (ConfigDefinition definition in stale)
				{
					_config.Remove(definition);
				}

				changed |= ClearOrphanedEntries(_config);
				if (changed) _config.Save();
			}
			catch { }
		}

		/// <summary>
		/// True for every definition that belongs to a slot visibility layout: the single group of
		/// any shipped language, the old per-audience groups of any language, and the legacy flat
		/// section.
		/// </summary>
		private static bool IsSlotEntry(ConfigDefinition definition)
		{
			if (definition.Section == LegacySection) return true;

			foreach (LanguageOptions options in L10n.GetAllLanguageOptions())
			{
				bool slotKey = false;
				for (int i = 0; i < ConfigSlots.Length; i++)
				{
					string key = SlotNameOf(options, ((int)ConfigSlots[i].Key).ToString(),
						ConfigSlots[i].Value);
					if (definition.Key == key) { slotKey = true; break; }
				}
				if (!slotKey) continue;

				if (definition.Section == SlotGroupNameOf(options)) return true;

				foreach (Audience audience in AllAudiences)
				{
					if (definition.Section == AudienceNameOf(options, audience)) return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Clear entries that are on disk but not bound, so a migrated config file does not keep
		/// duplicated legacy or foreign-language sections.
		/// </summary>
		private static bool ClearOrphanedEntries(ConfigFile config)
		{
			try
			{
				PropertyInfo property = typeof(ConfigFile).GetProperty("OrphanedEntries",
					BindingFlags.NonPublic | BindingFlags.Instance);
				if (property == null) return false;

				var dict = property.GetValue(config) as IDictionary;
				if (dict == null || dict.Count == 0) return false;

				dict.Clear();
				return true;
			}
			catch { return false; }
		}

		private static void LogConfigSummary()
		{
			EquipManagerPlugin.Log?.LogInfo("[Config] file: " +
				(_config != null ? _config.ConfigFilePath : "(none)") +
				", section=" + SectionForConfig() +
				", entries=" + SlotSettings.Count);
		}

		private static string DescribeMask(AudienceMask mask)
		{
			if (mask == 0) return "visible for everyone";

			var parts = new List<string>(3);
			foreach (Audience audience in AllAudiences)
			{
				if ((mask & BitOf(audience)) != 0) parts.Add(audience.ToString());
			}
			return "hidden for " + string.Join("/", parts.ToArray());
		}

		private static void WarnConfigOnce(string message)
		{
			if (_warnedConfig) return;
			_warnedConfig = true;
			EquipManagerPlugin.Log?.LogWarning("[Config] " + message);
		}

		private static void OnSlotSettingChanged(object sender, EventArgs args)
		{
			if (_settingFromCode) return;

			ConfigEntry<AudienceMask> entry = sender as ConfigEntry<AudienceMask>;
			if (entry == null) return;

			SlotSetting setting = null;
			foreach (SlotSetting candidate in SlotSettings)
			{
				if (ReferenceEquals(candidate.Entry, entry)) { setting = candidate; break; }
			}
			if (setting == null) return;

			ApplySetting(setting.Slot, entry.Value);
			RefreshAll();
			LogState(setting.Slot, entry.Value);
		}

		private static void ApplySetting(EquipmentSlot slot, AudienceMask mask)
		{
			Hidden[slot] = mask;
		}

		private static ConfigEntry<AudienceMask> FindEntry(EquipmentSlot slot)
		{
			foreach (SlotSetting setting in SlotSettings)
			{
				if (setting.Slot == slot) return setting.Entry;
			}
			return null;
		}

		private static void LogState(EquipmentSlot slot, AudienceMask mask)
		{
			EquipManagerPlugin.Log?.LogInfo(
				"[" + SectionForConfig() + "] " + slot + ": " + DescribeMask(mask));
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
			Unknown.Clear();
			TargetKeys.Clear();
			_targetLogs = 0;
			Owners.Reset();
		}

		/// <summary>Slot -> audiences the model is hidden from. Mirrors the bound entries.</summary>
		private static readonly Dictionary<EquipmentSlot, AudienceMask> Hidden =
			new Dictionary<EquipmentSlot, AudienceMask>();

		/// <summary>Default of a slot without a stored value: hidden for every audience.</summary>
		private const AudienceMask DefaultMask =
			AudienceMask.Player | AudienceMask.AI | AudienceMask.Teammate;

		internal static readonly HashSet<EquipmentSlot> Slots = new HashSet<EquipmentSlot>
		{
			(EquipmentSlot)12, (EquipmentSlot)11, (EquipmentSlot)14,
			(EquipmentSlot)6,  (EquipmentSlot)7,  (EquipmentSlot)10,
			(EquipmentSlot)9,  (EquipmentSlot)4
		};

		private static readonly HashSet<PlayerBody.SlotView> Views = new HashSet<PlayerBody.SlotView>();
		private static readonly HashSet<PlayerBody.SlotView> Unknown = new HashSet<PlayerBody.SlotView>();
		private static readonly Dictionary<PlayerBody.SlotView, Dictionary<Renderer, bool>> Saved =
			new Dictionary<PlayerBody.SlotView, Dictionary<Renderer, bool>>();

		private const string LegacySection = "Slot Visibility";

		private static readonly Audience[] AllAudiences =
		{
			Audience.Player, Audience.AI, Audience.Teammate
		};

		// English display names used in descriptions and as a stable fallback. The actual
		// config keys come from the localized slot names.
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

		private static ConfigFile _config;
		private static Dictionary<string, string> _fileValues;
		private static bool _settingFromCode;
		private static bool _warnedConfig;

		private const int MaxTargetLogs = 80;

		/// <summary>Poll interval once everything is resolved and nothing is hidden.</summary>
		private const int ReconcileMs = 2000;

		/// <summary>
		/// Verification interval while a view is unknown or hidden: the game re-enables renderers when
		/// it rebuilds a model, so the hidden state has to be re-asserted quickly.
		/// </summary>
		private const int VerifyingReconcileMs = 100;

		private static int _lastReconcileTick;
		private static int _targetLogs;
		private static readonly HashSet<string> TargetKeys = new HashSet<string>(StringComparer.Ordinal);

		private sealed class SlotSetting
		{
			internal readonly EquipmentSlot Slot;
			internal readonly ConfigEntry<AudienceMask> Entry;

			internal SlotSetting(EquipmentSlot slot, ConfigEntry<AudienceMask> entry)
			{
				Slot = slot;
				Entry = entry;
			}
		}
	}
}