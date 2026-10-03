using System.Collections.Generic;

namespace AnimeshitEquipManager
{
	/// <summary>
	/// Serializable bundle of every UI string this plugin may display.
	/// Property names are the JSON keys used in lang/&lt;code&gt;.json.
	/// </summary>
	public class LanguageOptions
	{
		public string Show { get; set; }
		public string Hide { get; set; }
		public string Appearance { get; set; }

		public string AudiencePlayer { get; set; }
		public string AudienceAI { get; set; }
		public string AudienceTeammate { get; set; }

		/// <summary>EquipmentSlot integer value (as string) -> display name.</summary>
		public Dictionary<string, string> Slots { get; set; }

		/// <summary>Description text for the AI module config entry.</summary>
		public string ConfigEnableAIDesc { get; set; }

		/// <summary>Section title of the per-slot audience selection.</summary>
		public string ConfigSlotGroup { get; set; }

		public string ConfigName { get; set; }

		public string ConfigEnableSub1 { get; set; }
	}
}