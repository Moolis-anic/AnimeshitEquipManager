using System.Collections.Generic;

namespace AnimeshitEquipManager.Localization
{
	internal static class EmbeddedEnglishLanguageProvider
	{
		private static LanguageOptions _cached;

		internal static LanguageOptions Fallback
		{
			get
			{
				if (_cached == null) _cached = Create();
				return _cached;
			}
		}

		internal static LanguageOptions Create()
		{
			return new LanguageOptions
			{
				Show = "Show",
				Hide = "Hide",
				Appearance = " Appearance",

				AudiencePlayer = "Player",
				AudienceAI = "AI",
				AudienceTeammate = "Teammate",

				Slots = new Dictionary<string, string>
				{
					{ "4",  " Backpack" },
					{ "6",  " Tactical Vest" },
					{ "7",  " Armor Vest" },
					{ "9",  " Eyewear" },
					{ "10", " Face Cover" },
					{ "11", " Headwear" },
					{ "12", " Earpiece" },
					{ "14", " Armband" },
				},

				ConfigName         = "AI Module & Voice",
				ConfigEnableSub1   = "Animeshit",
				ConfigEnableAIDesc = 
					"On: AI bots use the Animeshit anime model and voice. " +
					"Off: keep the server-generated model and voice. " +
					"The setting is saved and applies to bots generated afterwards; " +
					"already-spawned AI will not transform immediately. " +
					"Enter the next raid for a full apply.",
			};
		}
	}
}