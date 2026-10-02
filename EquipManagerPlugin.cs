using System;
using AnimeshitEquipManager.Localization;
using AnimeshitEquipManager.Modules;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace AnimeshitEquipManager
{
	[BepInPlugin("com.animeshit.equipmanager", "Animeshit Equip Manager", "1.0.3")]
	public sealed class EquipManagerPlugin : BaseUnityPlugin
	{
		private void Awake()
		{
			EquipManagerPlugin.Log = base.Logger;

			try { L10n.Refresh(); }
			catch (Exception ex)
			{
				EquipManagerPlugin.Log.LogWarning("L10n.Refresh failed at startup: " + ex.Message);
			}

			BotAppearanceControl.Bind(base.Config);
			L10n.LanguageChanged += BotAppearanceControl.ApplyLocalizedNames;
			L10n.LanguageChanged += BotAppearanceControl.ApplyLocalizedDescription;
			PitTeamInterop.Probe();

			// Clear hidden status
			// Localization refresh is triggered before the right-click menu is built
			// because the game language is only loaded after Awake.
			Visibility.Reset();

			this.harmony = new Harmony("com.animeshit.equipmanager");
			try
			{
				this.harmony.PatchAll(typeof(EquipManagerPlugin).Assembly);
				EquipManagerPlugin.Log.LogInfo(
					"SPT 4.1.6 equipment visibility ready. 8 slots x player/AI/teammate, " +
					"all visible on launch. Right-click equipped gear to toggle each audience.");
			}
			catch (Exception ex)
			{
				this.harmony.UnpatchSelf();
				Visibility.Reset();
				EquipManagerPlugin.Log.LogError(
					"Equipment visibility patches could not start: " + ex);
			}
		}

		private void OnDestroy()
		{
			Visibility.Reset();
			BotAppearanceControl.Unbind();
			L10n.LanguageChanged -= BotAppearanceControl.ApplyLocalizedNames;
			L10n.LanguageChanged -= BotAppearanceControl.ApplyLocalizedDescription;
			Harmony harmony = this.harmony;
			if (harmony == null) return;
			harmony.UnpatchSelf();
		}

		private int _nextLanguageCheck;

		private void Update()
		{
			// Poll the game language so the localized config section/key and description are
			// applied as soon as the game finishes loading its language (Awake is too early).
			int now = Environment.TickCount;
			if (unchecked(now - _nextLanguageCheck) < 2000) return;
			_nextLanguageCheck = now;

			try { L10n.Refresh(); }
			catch (Exception ex)
			{
				EquipManagerPlugin.Log?.LogWarning("L10n.Refresh failed: " + ex.Message);
			}
		}

		internal static ManualLogSource Log;

		private Harmony harmony;
	}
}