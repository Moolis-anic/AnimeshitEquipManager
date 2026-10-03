using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json;

namespace AnimeshitEquipManager.Localization
{
	/// <summary>
	/// Localization entry point.
	///
	/// Lifecycle:
	///   * First access loads lang/&lt;code&gt;.json (or lang/en.json as fallback).
	///   * Refresh() re-detects the game language; the file is only re-read
	///     when the language actually changed.
	///   * Hot paths (context menu construction) only touch cached fields.
	/// </summary>
	internal static class L10n
	{
		private static string _languageCode;
		private static LanguageOptions _options;

		private static Type _localizationType;
		private static bool _localizationTypeResolved;

		internal static LanguageOptions Current
		{
			get
			{
				if (_options == null) Refresh();
				return _options;
			}
		}

		internal static string AudienceName(Audience audience)
		{
			var o = Current;
			switch (audience)
			{
				case Audience.Player: return o.AudiencePlayer;
				case Audience.AI: return o.AudienceAI;
				case Audience.Teammate: return o.AudienceTeammate;
				default: return audience.ToString();
			}
		}

		internal static string SlotName(EquipmentSlot slot)
		{
			string key = ((int)slot).ToString();
			var slots = Current.Slots;
			if (slots != null && slots.TryGetValue(key, out string name) && name != null)
			{
				return name;
			}
			return " " + slot;
		}

		/// <summary>
		/// A (section, key) pair that a language file may use for the config entry.
		/// </summary>
		internal struct ConfigNamePair
		{
			internal readonly string Section;
			internal readonly string Key;

			internal ConfigNamePair(string section, string key)
			{
				Section = section;
				Key = key;
			}
		}

		/// <summary>
		/// Every (section, key) pair the shipped language files can use for the config entry.
		/// Used to adopt an already existing config section no matter which language made it.
		/// </summary>
		internal static List<ConfigNamePair> GetConfigNameCandidates()
		{
			var result = new List<ConfigNamePair>();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			LanguageOptions fallback = EmbeddedEnglishLanguageProvider.Fallback;
			if (fallback != null)
			{
				AddCandidate(result, seen, fallback.ConfigName, fallback.ConfigEnableSub1);
			}

			try
			{
				string dir = GetLangDirectory();
				if (Directory.Exists(dir))
				{
					foreach (string file in Directory.GetFiles(dir, "*.json"))
					{
						try
						{
							var options = JsonConvert.DeserializeObject<LanguageOptions>(
								File.ReadAllText(file));
							if (options != null)
							{
								AddCandidate(result, seen, options.ConfigName, options.ConfigEnableSub1);
							}
						}
						catch { }
					}
				}
			}
			catch { }

			return result;
		}

		private static void AddCandidate(
			List<ConfigNamePair> result, HashSet<string> seen, string section, string key)
		{
			if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return;
			if (!seen.Add(section)) return;
			result.Add(new ConfigNamePair(section, key));
		}

		/// <summary>
		/// Every shipped language bundle (embedded English plus each lang/*.json file).
		/// Used to recognize config sections that were created under another language.
		/// </summary>
		internal static List<LanguageOptions> GetAllLanguageOptions()
		{
			var result = new List<LanguageOptions>();
			var seen = new HashSet<string>(StringComparer.Ordinal);

			AddOptions(result, seen, EmbeddedEnglishLanguageProvider.Fallback);

			try
			{
				string dir = GetLangDirectory();
				if (Directory.Exists(dir))
				{
					foreach (string file in Directory.GetFiles(dir, "*.json"))
					{
						try
						{
							var options = JsonConvert.DeserializeObject<LanguageOptions>(
								File.ReadAllText(file));
							AddOptions(result, seen, options);
						}
						catch { }
					}
				}
			}
			catch { }

			return result;
		}

		private static void AddOptions(
			List<LanguageOptions> result, HashSet<string> seen, LanguageOptions options)
		{
			if (options == null) return;

			string marker = (options.ConfigName ?? string.Empty) + "\u0001" +
				(options.AudiencePlayer ?? string.Empty) + "\u0001" +
				(options.AudienceAI ?? string.Empty) + "\u0001" +
				(options.AudienceTeammate ?? string.Empty);
			if (!seen.Add(marker)) return;

			result.Add(options);
		}

		/// <summary>
		/// Re-detect the game language and reload the language file if it changed.
		/// Cheap when the language has not changed.
		/// </summary>
		internal static void Refresh()
		{
			string rawCulture;
			string memberName;
			string code = DetectLanguage(out rawCulture, out memberName);

			if (code == _languageCode && _options != null) return;

			_languageCode = code;
			_options = LoadFor(code);

			var log = EquipManagerPlugin.Log;
			if (log != null)
			{
				if (rawCulture != null)
				{
					log.LogInfo("[L10n] raw culture = '" + rawCulture + "'" +
						(memberName == null ? "" : " via " + memberName) +
						" -> '" + code + "'");
				}
				log.LogInfo("[L10n] language=" + code);
			}

			var handler = LanguageChanged;
			if (handler != null)
			{
				try { handler(); }
				catch (Exception ex)
				{
					EquipManagerPlugin.Log?.LogWarning(
						"[L10n] LanguageChanged handler failed: " + ex.Message);
				}
			}
		}

		// ---------------- Loader ----------------

		private static LanguageOptions LoadFor(string code)
		{
			string dir  = GetLangDirectory();
			string path = Path.Combine(dir, code + ".json");
			if (!File.Exists(path))
			{
				path = Path.Combine(dir, "en.json");
			}

			LanguageOptions loaded = null;
			if (File.Exists(path))
			{
				try
				{
					string json = File.ReadAllText(path);
					loaded = JsonConvert.DeserializeObject<LanguageOptions>(json);
					EquipManagerPlugin.Log?.LogInfo("[L10n] loaded " + path);
				}
				catch (Exception ex)
				{
					EquipManagerPlugin.Log?.LogWarning(
						"[L10n] failed to parse " + path + ": " + ex.Message);
				}
			}
			else
			{
				EquipManagerPlugin.Log?.LogWarning(
					"[L10n] no language file found for '" + code + "'; searched '" + dir +
					"'. Using embedded English defaults.");
			}

			return Merge(EmbeddedEnglishLanguageProvider.Fallback, loaded);
		}

		/// <summary>
		/// Non-destructive merge: any field left null by the user JSON
		/// keeps its English default. Slots are merged per key.
		/// </summary>
		private static LanguageOptions Merge(LanguageOptions fallback, LanguageOptions user)
		{
			if (user == null) return fallback;

			return new LanguageOptions
			{
				Show = user.Show ?? fallback.Show,
				Hide = user.Hide ?? fallback.Hide,
				Appearance = user.Appearance ?? fallback.Appearance,
				AudiencePlayer = user.AudiencePlayer ?? fallback.AudiencePlayer,
				AudienceAI = user.AudienceAI ?? fallback.AudienceAI,
				AudienceTeammate = user.AudienceTeammate ?? fallback.AudienceTeammate,
				Slots = MergeSlots(fallback.Slots, user.Slots),
				ConfigSlotGroup = user.ConfigSlotGroup ?? fallback.ConfigSlotGroup,
				ConfigName = user.ConfigName ?? fallback.ConfigName,
				ConfigEnableSub1 = user.ConfigEnableSub1 ?? fallback.ConfigEnableSub1,
				ConfigEnableAIDesc = user.ConfigEnableAIDesc ?? fallback.ConfigEnableAIDesc,
			};
		}

		private static Dictionary<string, string> MergeSlots(
			Dictionary<string, string> fallback, Dictionary<string, string> user)
		{
			if (user == null) return fallback;
			if (fallback == null) return user;

			var merged = new Dictionary<string, string>(fallback, StringComparer.Ordinal);
			foreach (var pair in user)
			{
				if (!string.IsNullOrEmpty(pair.Value)) merged[pair.Key] = pair.Value;
			}
			return merged;
		}

		private static string GetLangDirectory()
		{
			string dllPath = Assembly.GetExecutingAssembly().Location;
			if (!string.IsNullOrEmpty(dllPath))
			{
				string dllDir = Path.GetDirectoryName(dllPath);
				if (!string.IsNullOrEmpty(dllDir))
				{
					string candidate = Path.Combine(dllDir, "lang");
					if (Directory.Exists(candidate)) return candidate;
				}
			}

			try
			{
				string pluginsRoot = BepInEx.Paths.PluginPath;
				if (!string.IsNullOrEmpty(pluginsRoot))
				{
					string byName = Path.Combine(pluginsRoot, "AnimeshitEquipManager", "lang");
					if (Directory.Exists(byName)) return byName;

					string direct = Path.Combine(pluginsRoot, "lang");
					if (Directory.Exists(direct)) return direct;
				}
			}
			catch { }

			string fallbackDir = string.IsNullOrEmpty(dllPath)
				? "."
				: (Path.GetDirectoryName(dllPath) ?? ".");
			return Path.Combine(fallbackDir, "lang");
		}

		// ---------------- Language detection ----------------

		private static string DetectLanguage(out string rawCulture, out string memberName)
		{
			rawCulture = null;
			memberName = null;
			try
			{
				Type t = _localizationType;
				if (t == null && !_localizationTypeResolved)
				{
					t = AccessTools.TypeByName("EFT.LocalizationManager");
					_localizationTypeResolved = true;
					_localizationType = t;
				}
				if (t == null) return "en";

				object instance = GetInstance(t);
				if (instance == null) return "en";

				object raw = GetLanguageValue(t, instance, out memberName);
				if (raw == null)
				{
					memberName = null;
					return "en";
				}

				rawCulture = raw.ToString();
				return MapLocale(rawCulture);
			}
			catch (Exception ex)
			{
				EquipManagerPlugin.Log?.LogWarning("[L10n] detect failed: " + ex.Message);
				return "en";
			}
		}

		private static object GetInstance(Type t)
		{
			PropertyInfo p = t.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (p != null)
			{
				object v = p.GetValue(null);
				if (v != null) return v;
			}

			FieldInfo f = t.GetField("Instance",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (f != null)
			{
				object v = f.GetValue(null);
				if (v != null) return v;
			}

			return null;
		}

		private static object GetLanguageValue(Type t, object instance, out string memberName)
		{
			memberName = null;

			// 1)  Culture (EFT.LocalizationManager)
			PropertyInfo cultureProp = t.GetProperty("Culture",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (cultureProp != null)
			{
				try
				{
					object v = cultureProp.GetValue(instance);
					if (v != null)
					{
						memberName = "Culture";
						return v;
					}
				}
				catch { /* Continue */ }
			}

			// 2) Instance field _currentApplicationCulture
			FieldInfo currentCulture = t.GetField("_currentApplicationCulture",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (currentCulture != null)
			{
				try
				{
					object v = currentCulture.GetValue(instance);
					if (v != null)
					{
						memberName = "_currentApplicationCulture";
						return v;
					}
				}
				catch { /* Continue */ }
			}

			// 3) Instance field _culture
			FieldInfo cultureField = t.GetField("_culture",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (cultureField != null)
			{
				try
				{
					object v = cultureField.GetValue(instance);
					if (v != null)
					{
						memberName = "_culture";
						return v;
					}
				}
				catch { /* Continue */ }
			}

			// 4) Backward compatibility: Legacy property names
			string[] legacy =
			{
				"CurrentLanguage", "SelectedLanguage", "Language",
				"CurrentLocale", "Locale"
			};
			foreach (string name in legacy)
			{
				PropertyInfo p = t.GetProperty(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (p == null) continue;
				try
				{
					object v = p.GetValue(instance);
					if (v != null)
					{
						memberName = name;
						return v;
					}
				}
				catch {}
			}

			return null;
		}

		/// <summary>
		/// Maps the game language identifier (e.g. "en", "ch", "zh-CN") to our file suffix.
		/// Only Simplified Chinese has a translated JSON; everything else falls back to English.
		/// </summary>
		private static string MapLocale(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return "en";

			string lower = raw.Trim().ToLowerInvariant().Replace('_', '-');

			if (lower == "ch" || lower == "chs" || lower == "cht" || lower == "zh")
			{
				return "chs";
			}
			if (lower.StartsWith("zh") || lower.StartsWith("ch")) return "chs";
			return "en";
		}

		internal static event Action LanguageChanged;
	}
}