using System;

namespace AnimeshitEquipManager
{
	internal enum Audience
	{
		Unknown,
		Player,
		AI,
		Teammate
	}

	/// <summary>
	/// Per-slot audience selection stored in the config file. A set flag means the model is
	/// hidden for that audience, which keeps the legacy boolean semantics (true = hidden).
	///
	/// Only single-bit values are declared on purpose: ConfigurationManager 18.x draws one
	/// checkbox per named flag, so a combined value such as "All" would add a redundant box
	/// next to Player / AI / Teammate.
	/// </summary>
	[Flags]
	internal enum AudienceMask
	{
		Player = 1,
		AI = 2,
		Teammate = 4
	}
}
