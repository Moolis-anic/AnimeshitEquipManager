using System;
using AnimeshitEquipManager.Localization;
using AnimeshitEquipManager.Modules;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace AnimeshitEquipManager
{
	[BepInPlugin("com.animeshit.equipmanager", "Animeshit Equip Manager", "1.0.2")]
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
			L10n.LanguageChanged += BotAppearanceControl.ApplyLocalizedDescription;
			PitTeamInterop.Probe();

			// Clear hidden status。
			// 本地化刷新改由右键菜单构建前触发，因为游戏语言在 Awake 之后才加载完成。
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
			L10n.LanguageChanged -= BotAppearanceControl.ApplyLocalizedDescription;
			Harmony harmony = this.harmony;
			if (harmony == null) return;
			harmony.UnpatchSelf();
		}

		internal static ManualLogSource Log;

		private Harmony harmony;
	}
}