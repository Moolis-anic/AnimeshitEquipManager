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

		private const string PluginGuid = "xyz.pit.fireteam";

		/// <summary>How many profile shaped members are watched as teammate sources.</summary>
		private const int MaxProfileSources = 120;

		/// <summary>How often the discovery may retry before a partial result is accepted.</summary>
		private const int MaxProfileSourceAttempts = 30;

		/// <summary>How many objects of the plugin graph are inspected while walking into it.</summary>
		private const int MaxObjectWalk = 16;

		private static readonly Dictionary<Type, MemberInfo[]> ShapedMembers =
			new Dictionary<Type, MemberInfo[]>();

		private static readonly List<ProfileSource> ProfileSources = new List<ProfileSource>();
		private static readonly HashSet<string> _loggedSourceMatches = new HashSet<string>();
		private static bool _profileSourcesResolved;
		private static int  _lastProfileSourceProbe;
		private static int  _profileSourceAttempts;

		/// <summary>A member that may hold teammate profiles (static when Target is null).</summary>
		private sealed class ProfileSource
		{
			internal object Target;
			internal MemberInfo Member;
			internal string Label;
		}

		/// <summary>pitTeam available and parsed successfully</summary>
		internal static bool Available
		{
			get
			{
				EnsureInit(false);
				return _resolved;
			}
		}

		/// <summary>
		/// pitTeam available and its singleton instance already created. Load ordering only
		/// guarantees the assembly is loaded, not that BossPlayers has been instantiated.
		/// </summary>
		internal static bool Ready
		{
			get
			{
				EnsureInit(false);
				if (!_resolved) return false;

				object instance;
				return TryGetInstance(out instance) && instance != null;
			}
		}

		private static bool TryGetInstance(out object instance)
		{
			instance = null;
			try
			{
				if (_instanceProperty != null) instance = _instanceProperty.GetValue(null);
				else if (_instanceField != null) instance = _instanceField.GetValue(null);
			}
			catch { return false; }
			return true;
		}

		internal static void Probe()
		{
			EnsureInit(true);
			EnsureProfileSources();
		}

		/// <summary>
		/// Retries the teammate profile source discovery until it can run with the pitTeam plugin
		/// instance available. Cheap and throttled; called from the plugin update poll so the result
		/// is logged even when the game never opens a profile preview.
		/// </summary>
		internal static void PollProfileSources()
		{
			EnsureProfileSources();
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

		/// <summary>
		/// True when PitFireTeam knows the profile id as one of its teammates. Two sources are used:
		/// the follower dictionary of a running raid, and the profile shaped members the plugin keeps
		/// in the menu, where a teammate is only known as a profile.
		/// </summary>
		internal static bool IsTeammateProfile(string profileId)
		{
			if (string.IsNullOrEmpty(profileId)) return false;

			EnsureInit(false);

			if (FollowersContain(profileId)) return true;

			return ProfileSourcesContain(profileId);
		}

		private static bool FollowersContain(string profileId)
		{
			if (!_resolved) return false;

			object instance;
			if (!TryGetInstance(out instance) || instance == null) return false;

			IDictionary dict;
			try { dict = _followersField.GetValue(instance) as IDictionary; }
			catch { return false; }
			if (dict == null) return false;

			try { return dict.Contains(profileId); }
			catch { return false; }
		}

		internal static bool IsTeammate(Player player)
		{
			if (player == null) return false;
			EnsureInit(false);
			if (!_resolved) return false;

			object instance;
			if (!TryGetInstance(out instance)) return false;

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

		/// <summary>
		/// Lists the pitTeam members that can hold teammate profiles. The menu side of PitFireTeam
		/// loads teammate profiles over HTTP and keeps them outside the follower dictionary, which
		/// only exists while a raid runs. Members are found by name and declared type instead of
		/// hard-coded names, so this keeps working when the plugin changes its layout.
		/// </summary>
		private static void EnsureProfileSources()
		{
			if (_profileSourcesResolved) return;

			int now = Environment.TickCount;
			if (unchecked(now - _lastProfileSourceProbe) < ProbeCooldownMs) return;
			_lastProfileSourceProbe = now;

			object plugin = PluginInstance();
			Assembly assembly = plugin != null ? plugin.GetType().Assembly : null;
			if (assembly == null)
			{
				Type bossPlayers = AccessTools.TypeByName(BossPlayersTypeName);
				assembly = bossPlayers != null ? bossPlayers.Assembly : null;
			}
			if (assembly == null)
			{
				LogInfo("teammate profile sources unavailable: the pitTeam assembly is not resolved yet.");
				return;
			}

			var sources = new List<ProfileSource>();

			Type[] types;
			try { types = assembly.GetTypes(); }
			catch (ReflectionTypeLoadException ex) { types = ex.Types; }
			catch { types = new Type[0]; }

			for (int i = 0; i < types.Length; i++)
			{
				Type type = types[i];
				if (type == null || type.IsGenericTypeDefinition) continue;
				CollectSources(sources, null, type, true);
			}

			if (plugin != null)
			{
				CollectSources(sources, plugin, plugin.GetType(), false);

				// Walk a couple of levels into the objects the plugin keeps: the module that holds the
				// fetched teammate profiles can hang behind another one, and the profile of the member
				// that is currently shown is not enough for a list of avatars.
				var frontier = new List<object>(4);
				frontier.Add(plugin);

				for (int level = 0; level < 2 && sources.Count < MaxProfileSources; level++)
				{
					var next = new List<object>(4);
					for (int i = 0; i < frontier.Count && next.Count < MaxObjectWalk; i++)
					{
						object current = frontier[i];
						if (current == null) continue;

						List<MemberInfo> members = ProfileShapedMembers(current.GetType(), false);
						for (int m = 0; m < members.Count && next.Count < MaxObjectWalk; m++)
						{
							object value = ReadValue(current, members[m]);
							if (value == null || value is string || value.GetType().IsPrimitive) continue;
							if (ReferenceEquals(value, plugin)) continue;

							next.Add(value);
						}
					}

					for (int i = 0; i < next.Count; i++)
					{
						CollectSources(sources, next[i], next[i].GetType(), false);
					}

					frontier = next;
					if (frontier.Count == 0) break;
				}
			}

			bool complete = plugin != null;
			_profileSourceAttempts++;

			// Profile typed members first: gate and patch fields otherwise fill the quota before the
			// member that actually keeps the fetched teammate profiles is reached.
			sources.Sort(CompareSources);

			ProfileSources.Clear();
			ProfileSources.AddRange(sources);
			_profileSourcesResolved =
				complete || _profileSourceAttempts >= MaxProfileSourceAttempts;

			LogSources(sources, complete, types.Length);
		}

		private static void CollectSources(
			List<ProfileSource> into, object target, Type type, bool isStatic)
		{
			List<MemberInfo> members = ProfileShapedMembers(type, isStatic);
			for (int i = 0; i < members.Count && into.Count < MaxProfileSources; i++)
			{
				into.Add(new ProfileSource
				{
					Target = target,
					Member = members[i],
					Label = type.Name + "." + members[i].Name
				});
			}
		}

		/// <summary>
		/// Members whose name mentions a profile, teammate or follower, or whose declared type looks
		/// like a profile or a collection of profiles.
		/// </summary>
		private static List<MemberInfo> ProfileShapedMembers(Type type, bool isStatic)
		{
			var found = new List<MemberInfo>(4);
			BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
				(isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.DeclaredOnly;

			int level = 0;
			for (Type current = type;
				current != null && current != typeof(object) && level < 8;
				current = current.BaseType, level++)
			{
				try
				{
					FieldInfo[] fields = current.GetFields(flags);
					for (int i = 0; i < fields.Length; i++)
					{
						if (IsProfileShaped(fields[i].Name, fields[i].FieldType)) found.Add(fields[i]);
					}

					PropertyInfo[] properties = current.GetProperties(flags);
					for (int i = 0; i < properties.Length; i++)
					{
						if (properties[i].CanRead && properties[i].GetIndexParameters().Length == 0 &&
							IsProfileShaped(properties[i].Name, properties[i].PropertyType))
						{
							found.Add(properties[i]);
						}
					}
				}
				catch { }
			}

			return found;
		}

		private static bool IsProfileShaped(string name, Type memberType)
		{
			if (memberType == null || memberType.IsPrimitive || memberType == typeof(string))
			{
				return false;
			}

			if (Mentions(memberType.Name, "profile")) return true;

			// Collections of profiles carry the profile in their generic argument, while the member or
			// type name itself (List, Dictionary) says nothing about it.
			Type[] arguments = memberType.GetGenericArguments();
			for (int i = 0; i < arguments.Length; i++)
			{
				if (Mentions(arguments[i].Name, "profile")) return true;
			}

			return Mentions(name, "profile") ||
				Mentions(name, "teammate") ||
				Mentions(name, "follower");
		}

		private static object PluginInstance()
		{
			try
			{
				Dictionary<string, BepInEx.PluginInfo> infos = BepInEx.Bootstrap.Chainloader.PluginInfos;
				BepInEx.PluginInfo info;
				if (infos != null && infos.TryGetValue(PluginGuid, out info) && info != null)
				{
					return info.Instance;
				}
			}
			catch { }
			return null;
		}

		private static bool ProfileSourcesContain(string profileId)
		{
			EnsureProfileSources();
			if (ProfileSources.Count == 0) return false;

			for (int i = 0; i < ProfileSources.Count; i++)
			{
				object value = ReadValue(ProfileSources[i].Target, ProfileSources[i].Member);
				if (value == null || !ContainsProfile(value, profileId, 0)) continue;

				LogSourceMatch(ProfileSources[i].Label);
				return true;
			}
			return false;
		}

		private static object ReadValue(object target, MemberInfo member)
		{
			try
			{
				FieldInfo field = member as FieldInfo;
				if (field != null)
				{
					return field.IsStatic ? field.GetValue(null) : field.GetValue(target);
				}

				PropertyInfo property = member as PropertyInfo;
				if (property == null) return null;

				MethodInfo getter = property.GetGetMethod(true);
				if (getter == null) return null;

				return getter.IsStatic ? getter.Invoke(null, null) : getter.Invoke(target, null);
			}
			catch { return null; }
		}

		/// <summary>
		/// True when the value holds the profile id, directly (a Profile, or a wrapper with an id
		/// member) or inside a dictionary or collection of such values.
		/// </summary>
		private static bool ContainsProfile(object value, string profileId, int depth)
		{
			if (value == null || depth > 3) return false;

			string text = value as string;
			if (text != null) return text == profileId;

			Profile profile = value as Profile;
			if (profile != null) return ProfileIdOf(profile) == profileId;

			IDictionary dict = value as IDictionary;
			if (dict != null)
			{
				try
				{
					if (dict.Contains(profileId)) return true;
				}
				catch { }

				return ContainsInItems(dict.Values, profileId, depth) ||
					ContainsInItems(dict.Keys, profileId, depth);
			}

			IEnumerable items = value as IEnumerable;
			if (items != null) return ContainsInItems(items, profileId, depth);

			string id = ReadStringMember(value, "ProfileId") ??
				ReadStringMember(value, "profileId") ??
				ReadStringMember(value, "Id") ??
				ReadStringMember(value, "_profileId") ??
				ReadStringMember(value, "_id");
			if (!string.IsNullOrEmpty(id) && id == profileId) return true;

			object nested = ReadMember(value, "Profile") ?? ReadMember(value, "_profile");
			return nested != null && ContainsProfile(nested, profileId, depth + 1);
		}

		private static bool ContainsInItems(IEnumerable items, string profileId, int depth)
		{
			if (items == null) return false;

			int seen = 0;
			try
			{
				foreach (object item in items)
				{
					if (seen++ > 64) break;
					if (ContainsProfile(item, profileId, depth + 1)) return true;
				}
			}
			catch { }
			return false;
		}

		/// <summary>
		/// True when one of the pitTeam profiles owns the given equipment, which identifies a preview
		/// model as a teammate even when the UI does not expose a usable profile id. Only plain
		/// profiles and collections of profiles are inspected, which covers the members pitTeam keeps
		/// for the viewed profile and the loadout editor.
		/// </summary>
		internal static bool TeammateOwnsEquipment(string equipmentId)
		{
			if (string.IsNullOrEmpty(equipmentId)) return false;

			EnsureProfileSources();
			if (ProfileSources.Count == 0) return false;

			for (int i = 0; i < ProfileSources.Count; i++)
			{
				object value = ReadValue(ProfileSources[i].Target, ProfileSources[i].Member);
				if (value == null) continue;
				if (!OwnsEquipment(value, equipmentId, 0)) continue;

				LogSourceMatch(ProfileSources[i].Label + " (equipment)");
				return true;
			}

			return false;
		}

		private static bool OwnsEquipment(object value, string equipmentId, int depth)
		{
			if (value == null || depth > 2) return false;

			Profile profile = value as Profile;
			if (profile != null) return EquipmentIdOf(profile) == equipmentId;

			EFT.InventoryLogic.InventoryEquipment equipment =
				value as EFT.InventoryLogic.InventoryEquipment;
			if (equipment != null) return equipment.Id == equipmentId;

			IDictionary dict = value as IDictionary;
			if (dict != null)
			{
				int seen = 0;
				try
				{
					foreach (DictionaryEntry entry in dict)
					{
						if (seen++ > 64) break;
						if (OwnsEquipment(entry.Value, equipmentId, depth + 1)) return true;
						if (OwnsEquipment(entry.Key, equipmentId, depth + 1)) return true;
					}
				}
				catch { }
				return false;
			}

			IEnumerable items = value as IEnumerable;
			if (items != null)
			{
				int seen = 0;
				try
				{
					foreach (object item in items)
					{
						if (seen++ > 64) break;
						if (OwnsEquipment(item, equipmentId, depth + 1)) return true;
					}
				}
				catch { }
				return false;
			}

			if (depth >= 2) return false;

			// Wrappers such as OtherPlayerProfile keep the equipment of the shown player in a member
			// (Equipment, Inventory, ItemController, Profile), which is the only identity the profile
			// window offers when the loadout editor was never opened.
			MemberInfo[] members = ShapedMembersOf(value.GetType());
			for (int i = 0; i < members.Length; i++)
			{
				object nested = ReadValue(value, members[i]);
				if (nested == null || ReferenceEquals(nested, value)) continue;
				if (OwnsEquipment(nested, equipmentId, depth + 1)) return true;
			}
			return false;
		}

		/// <summary>
		/// Members worth following between a wrapper and its equipment: the ones whose name or
		/// declared type mentions an equipment, inventory, profile or item.
		/// </summary>
		private static MemberInfo[] ShapedMembersOf(Type type)
		{
			MemberInfo[] cached;
			if (ShapedMembers.TryGetValue(type, out cached)) return cached;

			var found = new List<MemberInfo>(4);
			for (Type current = type;
				current != null && current != typeof(object);
				current = current.BaseType)
			{
				try
				{
					FieldInfo[] fields = current.GetFields(BindingFlags.Public |
						BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
					for (int i = 0; i < fields.Length; i++)
					{
						if (IsShaped(fields[i].Name, fields[i].FieldType)) found.Add(fields[i]);
					}

					PropertyInfo[] properties = current.GetProperties(BindingFlags.Public |
						BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
					for (int i = 0; i < properties.Length; i++)
					{
						if (properties[i].CanRead && properties[i].GetIndexParameters().Length == 0 &&
							IsShaped(properties[i].Name, properties[i].PropertyType))
						{
							found.Add(properties[i]);
						}
					}
				}
				catch { }
			}

			cached = found.ToArray();
			ShapedMembers[type] = cached;
			return cached;
		}

		private static bool IsShaped(string name, Type type)
		{
			if (type == null || type.IsPrimitive) return false;

			return Mentions(type.Name, "equipment") || Mentions(type.Name, "inventory") ||
				Mentions(type.Name, "profile") || Mentions(type.Name, "item") ||
				Mentions(name, "equipment") || Mentions(name, "inventory") ||
				Mentions(name, "profile") || Mentions(name, "item");
		}

		private static bool Mentions(string text, string word)
		{
			return text != null && text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static string EquipmentIdOf(Profile profile)
		{
			try
			{
				EFT.InventoryLogic.Inventory inventory = profile.Inventory;
				EFT.InventoryLogic.InventoryEquipment equipment =
					inventory != null ? inventory.Equipment : null;
				return equipment != null ? equipment.Id : null;
			}
			catch { return null; }
		}

		private static string ProfileIdOf(Profile profile)
		{
			try { return profile != null ? profile.Id : null; }
			catch { return null; }
		}

		private static int CompareSources(ProfileSource a, ProfileSource b)
		{
			return RankOf(a).CompareTo(RankOf(b));
		}

		private static int RankOf(ProfileSource source)
		{
			Type type = DeclaredTypeOf(source.Member);
			return type.Name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1;
		}

		private static Type DeclaredTypeOf(MemberInfo member)
		{
			FieldInfo field = member as FieldInfo;
			return field != null ? field.FieldType : ((PropertyInfo)member).PropertyType;
		}

		private static void LogSources(List<ProfileSource> sources, bool complete, int assemblyTypes)
		{
			LogInfo("teammate profile sources=" + sources.Count +
				", complete=" + complete +
				", assemblyTypes=" + assemblyTypes);
		}

		private static void LogSourceMatch(string label)
		{
			if (!_loggedSourceMatches.Add(label)) return;
			EquipManagerPlugin.Log?.LogInfo(
				"[PitTeamInterop] teammate profile matched via " + label);
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