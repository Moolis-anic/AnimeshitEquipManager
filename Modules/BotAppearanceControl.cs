using System;
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
			string name = L10n.Current?.ConfigName ?? "AI Module & Voice";
			string opt1 = L10n.Current?.ConfigEnableSub1 ?? "Animeshit";
			string description = L10n.Current?.ConfigEnableAIDesc
						?? "Enable the Animeshit anime model and voice for AI bots.";

			Enabled = config.Bind(
				name,
				opt1,
				true,
				new ConfigDescription(
					description,
					null,
					new object[]
					{
						new { Order = 100, Advanced = false }
					}));

			Enabled.SettingChanged += OnSettingChanged;

			acknowledged = null;
			QueueSync();
		}

		internal static void Unbind()
		{
			if (Enabled != null)
			{
				Enabled.SettingChanged -= OnSettingChanged;
			}
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

		internal const string Route = "/animeshit/botskins/settings";

		internal static ConfigEntry<bool> Enabled;

		private static readonly SemaphoreSlim SyncGate = new SemaphoreSlim(1, 1);

		private static bool? acknowledged;
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
}