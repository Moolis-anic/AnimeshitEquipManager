using System;
using AnimeshitEquipManager.Localization;
using AnimeshitEquipManager.Modules;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace AnimeshitEquipManager
{
	[BepInPlugin("com.animeshit.equipmanager", "Animeshit Equip Manager", "1.1.0")]
	[BepInDependency("xyz.pit.fireteam", BepInDependency.DependencyFlags.SoftDependency)]
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
			L10n.LanguageChanged += Visibility.RebindForLanguage;
			Visibility.Bind(base.Config);
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
					"config default is hidden. Right-click equipped gear to toggle each audience.");
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
			L10n.LanguageChanged -= Visibility.RebindForLanguage;
			Harmony harmony = this.harmony;
			if (harmony == null) return;
			harmony.UnpatchSelf();
		}

		private int _nextLanguageCheck;

		private void Update()
		{
			// Re-resolve tracked views: unresolved ones are retried a few times per second, because a
			// profile source is often only filled while the game builds the screen that shows it.
			Visibility.Reconcile();

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

			// Finish the pitTeam profile source discovery once the plugin instance is available.
			PitTeamInterop.PollProfileSources();
		}

		internal static ManualLogSource Log;

		private Harmony harmony;
	}
}