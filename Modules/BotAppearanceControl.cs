using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using AnimeshitEquipManager.Localization;

namespace AnimeshitEquipManager.Modules
{
	internal static class BotAppearanceControl
	{
		internal static void Bind(ConfigFile config)
		{
			_config = config;

			// The game language is not loaded yet during Awake, so the current language can
			// still be English here. If the config file already contains a section from any
			// shipped language, adopt it so a previously saved value is kept; otherwise use
			// the section/key of the current language.
			L10n.ConfigNamePair pair = AdoptExistingPair() ?? CurrentPair();

			Enabled = BindEntry(pair, true);
			Enabled.SettingChanged += OnSettingChanged;

			EquipManagerPlugin.Log?.LogInfo("[Config] file: " + config.ConfigFilePath);

			acknowledged = null;
			QueueSync();
		}

		/// <summary>
		/// Re-create the config entry with the section and key of the current language,
		/// carrying the current value over. Called when the detected language changes.
		/// </summary>
		internal static void ApplyLocalizedNames()
		{
			if (_config == null || Enabled == null) return;

			L10n.ConfigNamePair pair = CurrentPair();
			ConfigDefinition definition = Enabled.Definition;
			if (pair.Section == definition.Section && pair.Key == definition.Key) return;

			try
			{
				bool value = Enabled.Value;

				Enabled.SettingChanged -= OnSettingChanged;
				_config.Remove(definition);

				ConfigEntry<bool> entry = BindEntry(pair, value);
				entry.SettingChanged += OnSettingChanged;
				Enabled = entry;

				_config.Save();
			}
			catch (Exception ex)
			{
				WarnDescriptionOnce("could not localize the config section: " + ex.Message);
			}
		}

		private static ConfigEntry<bool> BindEntry(L10n.ConfigNamePair pair, bool defaultValue)
		{
			string text = L10n.Current != null ? L10n.Current.ConfigEnableAIDesc : null;
			if (string.IsNullOrEmpty(text))
			{
				text = "Enable the Animeshit anime model and voice for AI bots.";
			}
			_appliedDescription = text;

			return _config.Bind(
				pair.Section,
				pair.Key,
				defaultValue,
				new ConfigDescription(
					text,
					null,
					new object[]
					{
						new { Order = 100, Advanced = false }
					}));
		}

		private static L10n.ConfigNamePair CurrentPair()
		{
			LanguageOptions options = L10n.Current;
			string section = options != null && !string.IsNullOrEmpty(options.ConfigName)
				? options.ConfigName
				: "AI Module & Voice";
			string key = options != null && !string.IsNullOrEmpty(options.ConfigEnableSub1)
				? options.ConfigEnableSub1
				: "Animeshit";
			return new L10n.ConfigNamePair(section, key);
		}

		/// <summary>
		/// Returns the (section, key) pair of a language whose section already exists in the
		/// config file, or null when the file contains none of them yet.
		/// </summary>
		private static L10n.ConfigNamePair? AdoptExistingPair()
		{
			try
			{
				string path = _config.ConfigFilePath;
				if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

				string text = File.ReadAllText(path);
				foreach (L10n.ConfigNamePair pair in L10n.GetConfigNameCandidates())
				{
					if (text.Contains("[" + pair.Section + "]")) return pair;
				}
			}
			catch { }
			return null;
		}

		internal static void Unbind()
		{
			if (Enabled != null)
			{
				Enabled.SettingChanged -= OnSettingChanged;
			}
			_config = null;
		}

		private static void OnSettingChanged(object sender, EventArgs args)
		{
			QueueSync();
		}

		private static async void QueueSync()
		{
			try
			{
				await Synchronize(false);
			}
			catch (Exception ex)
			{
				ManualLogSource log = EquipManagerPlugin.Log;
				if (log != null)
				{
					log.LogWarning("AI appearance setting sync failed; will retry before loading bots: " + ex.Message);
				}
			}
		}

		internal static async Task Synchronize(bool force)
		{
			await SyncGate.WaitAsync().ConfigureAwait(false);
			try
			{
				while (true)
				{
					bool target = Enabled == null || Enabled.Value;

					if (!force && acknowledged == target)
					{
						return;
					}

					JObject response = JObject.Parse(
						await RequestHandler.PostJsonAsync(
							Route,
							target ? "{\"enabled\":true}" : "{\"enabled\":false}"
						).ConfigureAwait(false));

					bool ok = (bool?)response["ok"] ?? false;
					bool echoed = (bool?)response["enabled"] ?? false;

					if (!ok || echoed != target)
					{
						throw new InvalidOperationException(
							"Server did not acknowledge the AI model/voice setting. " +
							"Update both client and AnimeshitBotSkins server module.");
					}

					acknowledged = target;
					force = false;
				}
			}
			finally
			{
				SyncGate.Release();
			}
		}

		internal static async Task<ProfileDescriptor[]> SendBotsAfterSync(Backend session, SendRequest request)
		{
			await Synchronize(true);
			return await session.Send<ProfileDescriptor[]>(request);
		}
		internal static void ApplyLocalizedDescription()
		{
			ConfigEntry<bool> entry = Enabled;
			LanguageOptions options = L10n.Current;
			if (entry == null || options == null) return;

			string text = options.ConfigEnableAIDesc;
			if (string.IsNullOrEmpty(text) || text == _appliedDescription) return;

			try
			{
				ConfigDescription old = entry.Description;
				object replacement = Activator.CreateInstance(
					typeof(ConfigDescription),
					new object[]
					{
						text,
						old == null ? null : old.AcceptableValues,
						old == null ? new object[0] : old.Tags
					});

				PropertyInfo property = typeof(ConfigEntryBase).GetProperty("Description",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				MethodInfo setter = property == null ? null : property.GetSetMethod(true);

				if (setter != null)
				{
					setter.Invoke(entry, new object[] { replacement });
				}
				else
				{
					FieldInfo backing = null;
					for (Type t = typeof(ConfigEntryBase); t != null && backing == null; t = t.BaseType)
						backing = t.GetField("<Description>k__BackingField",
							BindingFlags.NonPublic | BindingFlags.Instance);
					if (backing == null)
					{
						WarnDescriptionOnce("ConfigEntry.Description is read-only in this BepInEx build.");
						return;
					}
					backing.SetValue(entry, replacement);
				}

				_appliedDescription = text;
			}
			catch (Exception ex)
			{
				WarnDescriptionOnce("could not localize config description: " + ex.Message);
			}
		}

		private static void WarnDescriptionOnce(string message)
		{
			if (_warnedDescription) return;
			_warnedDescription = true;
			EquipManagerPlugin.Log?.LogWarning("[Config] " + message);
		}

		private static string _appliedDescription;
		private static bool _warnedDescription;

		internal const string Route = "/animeshit/botskins/settings";

		internal static ConfigEntry<bool> Enabled;

		private static readonly SemaphoreSlim SyncGate = new SemaphoreSlim(1, 1);

		private static bool? acknowledged;

		private static ConfigFile _config;
	}

}