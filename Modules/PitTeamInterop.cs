using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace AnimeshitEquipManager.Modules
{
	/// <summary>
	/// communicate with PitFireTeam using System.Reflection, recognize Player。
	/// pitTeam teammate Player is EFT.Player, only through pitTeam.Modules.BossPlayers._followersByProfileId.
	/// unload pitTeam will break the teammate recognition, but will not crash the game.
	/// </summary>
	internal static class PitTeamInterop
	{
		private const string BossPlayersTypeName = "pitTeam.Modules.BossPlayers";
		private const string InstanceMemberName  = "Instance";
		private const string FollowersFieldName  = "_followersByProfileId";

		// Avoid probing too frequently, because pitTeam may not be initialized yet, and reflection may throw exceptions.
		private const int ProbeCooldownMs = 2000;

		private static PropertyInfo _instanceProperty;
		private static FieldInfo     _instanceField;
		private static FieldInfo     _followersField;
		private static bool _resolved;
		private static bool _loggedUnavailable;
		private static int  _lastProbeTick;
		private static bool _dumpedEmptyFollowers;
		private static bool _dumpedNonEmptyFollowers;
		private static bool _loggedNullInstance;

		/// <summary>pitTeam available and parsed successfully</summary>
		internal static bool Available
		{
			get
			{
				EnsureInit(false);
				return _resolved;
			}
		}

		internal static void Probe()
		{
			EnsureInit(true);
		}

		private static void EnsureInit(bool force)
		{
			if (_resolved) return;

			int now = Environment.TickCount;
			if (!force && unchecked(now - _lastProbeTick) < ProbeCooldownMs) return;
			_lastProbeTick = now;

			Type t = AccessTools.TypeByName(BossPlayersTypeName);
			if (t == null)
			{
				if (!_loggedUnavailable)
				{
					_loggedUnavailable = true;
					LogInfo("pitTeam not detected: " + BossPlayersTypeName + " type not found.");
				}
				return;
			}

			// 1) Static property Instance
			_instanceProperty = t.GetProperty(InstanceMemberName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (_instanceProperty == null)
			{
				_instanceField = t.GetField(InstanceMemberName,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			}

			_followersField = t.GetField(FollowersFieldName,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

			if ((_instanceProperty == null && _instanceField == null) || _followersField == null)
			{
				LogInfo("pitTeam detected but expected members missing (Instance=" +
						(_instanceProperty != null || _instanceField != null) +
						", _followersByProfileId=" + (_followersField != null) +
						"). Teammate recognition disabled.");
				return;
			}

			_resolved = true;
			LogInfo("pitTeam detected: teammate recognition enabled via BossPlayers._followersByProfileId.");
		}

		internal static bool IsTeammate(Player player)
		{
			if (player == null) return false;
			EnsureInit(false);
			if (!_resolved) return false;

			object instance;
			try
			{
				instance = _instanceProperty != null
					? _instanceProperty.GetValue(null)
					: _instanceField.GetValue(null);
			}
			catch { return false; }

			if (instance == null)
			{
				if (!_loggedNullInstance)
				{
					_loggedNullInstance = true;
					LogInfo("BossPlayers.Instance is null; teammate recognition waits for pitTeam to initialize.");
				}
				return false;
			}

			string profileId = player.ProfileId;
			if (string.IsNullOrEmpty(profileId)) return false;

			IDictionary dict;
			try
			{
				dict = _followersField.GetValue(instance) as IDictionary;
			}
			catch { return false; }

			if (dict == null) return false;

			// Strategy 1: Directly check if the key exists in the dictionary.
			// _followersByProfileId[bot.ProfileId] = _follower
			try
			{
				if (dict.Contains(profileId))
				{
					DiagnoseFollowers(dict, profileId, true);
					return true;
				}
			}
			catch { /* key not found in value */ }

			// Strategy 2: Scan values, use reflection to match BotOwner.ProfileId / Player references.
			bool matched = ScanValues(dict, player, profileId);
			DiagnoseFollowers(dict, profileId, matched);
			return matched;
		}

		private static bool ScanValues(IDictionary dict, Player player, string profileId)
		{
			foreach (DictionaryEntry entry in dict)
			{
				object value = entry.Value;
				if (value == null) continue;

				if (ReferenceEquals(value, player)) return true;

				string valueProfileId = ReadStringMember(value, "ProfileId");
				if (!string.IsNullOrEmpty(valueProfileId) && valueProfileId == profileId) return true;

				object owner =
					ReadMember(value, "BotOwner") ??
					ReadMember(value, "Player") ??
					ReadMember(value, "_botOwner") ??
					ReadMember(value, "_player");
				if (owner == null) continue;

				if (ReferenceEquals(owner, player)) return true;

				string ownerProfileId = ReadStringMember(owner, "ProfileId");
				if (!string.IsNullOrEmpty(ownerProfileId) && ownerProfileId == profileId) return true;

				// BotOwner.GetPlayer exposed EFT.Player usually
				object ownerPlayer = ReadMember(owner, "GetPlayer") ?? ReadMember(owner, "Player");
				if (ReferenceEquals(ownerPlayer, player)) return true;
			}
			return false;
		}

		private static object ReadMember(object target, string name)
		{
			if (target == null) return null;

			Type t = target.GetType();
			try
			{
				PropertyInfo p = t.GetProperty(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target);
			}
			catch { }

			try
			{
				FieldInfo f = t.GetField(name,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (f != null) return f.GetValue(target);
			}
			catch { }

			return null;
		}

		private static string ReadStringMember(object target, string name)
		{
			object value = ReadMember(target, name);
			return value?.ToString();
		}

		private static void DiagnoseFollowers(IDictionary dict, string profileId, bool matched)
		{
			if (dict.Count == 0)
			{
				if (_dumpedEmptyFollowers) return;
				_dumpedEmptyFollowers = true;
			}
			else
			{
				if (_dumpedNonEmptyFollowers) return;
				_dumpedNonEmptyFollowers = true;
			}

			var log = EquipManagerPlugin.Log;
			if (log == null) return;

			var keys = new List<string>();
			foreach (DictionaryEntry entry in dict)
			{
				if (keys.Count >= 5) break;
				keys.Add(Convert.ToString(entry.Key));
			}

			bool contains;
			try { contains = dict.Contains(profileId); }
			catch { contains = false; }

			log.LogInfo("[PitTeamInterop] followers=" + dict.Count +
				", playerProfileId='" + profileId + "'" +
				", contains=" + contains +
				", valueScanMatch=" + matched +
				", sampleKeys=[" + string.Join(", ", keys.ToArray()) + "]");
		}

		private static readonly HashSet<string> _loggedMessages = new HashSet<string>();

		private static void LogInfo(string msg)
		{
			if (!_loggedMessages.Add(msg)) return;
			var log = EquipManagerPlugin.Log;
			if (log != null) log.LogInfo("[PitTeamInterop] " + msg);
		}
	}
}