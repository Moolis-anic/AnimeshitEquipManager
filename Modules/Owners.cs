using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;

namespace AnimeshitEquipManager.Modules
{
	/// <summary>
	/// Audience of the gear models the game renders through PlayerBody.SlotView.
	///
	/// Live models are resolved through the Player component above them. Preview models (inventory
	/// paper-doll, Hideout, other player profile, matchmaker, bot inspection) have no Player above
	/// them, so they are resolved through the identity of the equipment they render:
	///   1. equipment already seen for a live player,
	///   2. the equipment of the current session, which is always the local player,
	///   3. the equipment owned by one of the profiles PitFireTeam loaded (teammates),
	///   4. the profile the equipment links back to through its item owner.
	/// Anything else stays Unknown, which leaves that preview untouched instead of hiding the gear
	/// of an unrelated player.
	/// </summary>
	internal static class Owners
	{
		internal static void Register(Player player, bool aiControlled)
		{
			if (player == null) return;

			Audience audience = aiControlled ? Audience.AI : Audience.Player;

			Players.Remove(player);
			Players.Add(player, new Owner { Audience = audience });

			RememberProfile(player, audience);
		}

		internal static Audience Resolve(PlayerBody.SlotView view)
		{
			Player player = view._playerBody != null
				? view._playerBody.GetComponentInParent<Player>()
				: null;

			string equipmentId = EquipmentIdOf(view);

			if (player != null)
			{
				Audience audience = AudienceOfPlayer(player);
				if (audience != Audience.Unknown)
				{
					// AI is the leftover classification: caching it onto the equipment would make every
					// later preview of the same gear inherit it, even after the player turned out to be a
					// PitFireTeam teammate.
					if (audience != Audience.AI && !string.IsNullOrEmpty(equipmentId))
					{
						EquipmentOwners[equipmentId] = audience;
					}
					RememberProfile(player, audience);
				}
				return audience;
			}

			// Preview without a Player ancestor.
			if (string.IsNullOrEmpty(equipmentId)) return Audience.Unknown;

			Audience cached;
			if (EquipmentOwners.TryGetValue(equipmentId, out cached)) return cached;

			string sessionEquipmentId = SessionEquipmentId();
			if (!string.IsNullOrEmpty(sessionEquipmentId) && sessionEquipmentId == equipmentId)
			{
				return Remember(equipmentId, Audience.Player);
			}

			// Teammate avatars and the team profile window only carry a profile wrapper, so the
			// equipment those profiles own is what identifies them.
			if (PitTeamInterop.TeammateOwnsEquipment(equipmentId))
			{
				return Remember(equipmentId, Audience.Teammate);
			}

			// The owner chain of the equipment links back to its profile, which covers previews whose
			// equipment id differs from the session equipment, for example the other side's model on
			// the side selection screen.
			Audience profileAudience = AudienceOfProfile(EquipmentProfileId(view));
			if (profileAudience != Audience.Unknown) return Remember(equipmentId, profileAudience);

			return Audience.Unknown;
		}

		private static Audience Remember(string equipmentId, Audience audience)
		{
			EquipmentOwners[equipmentId] = audience;
			return audience;
		}

		/// <summary>
		/// Audience of a live player: the local player, a PitFireTeam teammates, a player that was
		/// already registered, or an AI.
		/// </summary>
		private static Audience AudienceOfPlayer(Player player)
		{
			if (player.IsYourPlayer) return Audience.Player;
			if (PitTeamInterop.IsTeammate(player)) return Audience.Teammate;

			// PitFireTeam only lists its followers while it tracks a raid. Before that, the same player
			// is matched through the profiles PitFireTeam loaded, otherwise a teammate would stay "AI"
			// and keep the slots that only AI is supposed to show.
			if (!PitTeamInterop.Ready && PitTeamInterop.IsTeammateProfile(ProfileIdOf(player)))
			{
				return Audience.Teammate;
			}

			Owner owner;
			if (Players.TryGetValue(player, out owner)) return owner.Audience;

			return player.IsAI ? Audience.AI : Audience.Unknown;
		}

		private static string ProfileIdOf(Player player)
		{
			try { return player.ProfileId; }
			catch { return null; }
		}

		/// <summary>
		/// Audience of a profile id: the local player, one of the PitFireTeam teammates, or a profile
		/// already seen for a live player.
		/// </summary>
		internal static Audience AudienceOfProfile(string profileId)
		{
			if (string.IsNullOrEmpty(profileId)) return Audience.Unknown;

			if (profileId == SessionProfileId()) return Audience.Player;

			if (PitTeamInterop.IsTeammateProfile(profileId)) return Audience.Teammate;

			Audience cached;
			return ProfileOwners.TryGetValue(profileId, out cached) ? cached : Audience.Unknown;
		}

		/// <summary>
		/// Remember the profile and the equipment of a player, so a model without a Player ancestor
		/// can be recognised by the equipment it renders.
		/// </summary>
		private static void RememberProfile(Player player, Audience audience)
		{
			if (player == null || audience == Audience.Unknown) return;

			try
			{
				Profile profile = player.Profile;
				if (profile == null) return;

				if (!string.IsNullOrEmpty(profile.Id)) ProfileOwners[profile.Id] = audience;

				// Equipment of an AI is deliberately not remembered: it is the leftover classification and
				// would be inherited by every preview of the same gear.
				if (audience == Audience.AI) return;

				Inventory inventory = profile.Inventory;
				InventoryEquipment equipment = inventory != null ? inventory.Equipment : null;
				if (equipment != null && !string.IsNullOrEmpty(equipment.Id))
				{
					EquipmentOwners[equipment.Id] = audience;
				}
			}
			catch { }
		}

		/// <summary>
		/// Equipment id the view renders: the parent of the slot, or the equipment found through the
		/// address of the contained item when the slot does not expose a parent directly.
		/// </summary>
		internal static string EquipmentIdOf(PlayerBody.SlotView view)
		{
			InventoryEquipment equipment = EquipmentOf(view);
			if (equipment != null && !string.IsNullOrEmpty(equipment.Id)) return equipment.Id;

			try
			{
				Item contained = view.ContainedItem != null ? view.ContainedItem.Value : null;
				SlotItemAddress address =
					contained != null ? contained.CurrentAddress as SlotItemAddress : null;
				InventoryEquipment owner = (address != null && address.Slot != null)
					? address.Slot.ParentItem as InventoryEquipment
					: null;
				if (owner != null && !string.IsNullOrEmpty(owner.Id)) return owner.Id;
			}
			catch { }

			return null;
		}

		private static InventoryEquipment EquipmentOf(PlayerBody.SlotView view)
		{
			Slot slot = view._slot;
			return (slot != null ? slot.ParentItem : null) as InventoryEquipment;
		}

		/// <summary>
		/// Profile id that owns the equipment a preview renders. The profile is only reachable through
		/// the owner chain of the equipment and that chain is game-version specific, so the members are
		/// looked up by name; a missing member simply leaves the preview unknown.
		/// </summary>
		private static string EquipmentProfileId(PlayerBody.SlotView view)
		{
			InventoryEquipment equipment = EquipmentOf(view);
			if (equipment == null) return null;

			object owner = ReadMember(equipment, OwnerMemberNames);
			string profileId = ReadId(ReadMember(owner, ProfileMemberNames));
			if (profileId != null) return profileId;

			// Inventory controllers expose the profile under names that differ between their subclasses,
			// so the member is also searched by type.
			profileId = ReadId(FindMemberOfType(owner, typeof(Profile)));
			if (profileId != null) return profileId;

			return ReadId(ReadMember(equipment, ProfileMemberNames));
		}

		/// <summary>
		/// First non-null value of a member whose declared type matches, looked up on the type and its
		/// base types. Used where the member name is not stable between game classes.
		/// </summary>
		private static object FindMemberOfType(object target, Type memberType)
		{
			if (target == null) return null;

			Type type = target.GetType();
			MemberInfo[] members;
			if (!TypedMembers.TryGetValue(type, out members))
			{
				var found = new List<MemberInfo>(2);
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
							if (memberType.IsAssignableFrom(fields[i].FieldType)) found.Add(fields[i]);
						}

						PropertyInfo[] properties = current.GetProperties(BindingFlags.Public |
							BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
						for (int i = 0; i < properties.Length; i++)
						{
							if (properties[i].CanRead && properties[i].GetIndexParameters().Length == 0 &&
								memberType.IsAssignableFrom(properties[i].PropertyType))
							{
								found.Add(properties[i]);
							}
						}
					}
					catch { }
				}

				members = found.ToArray();
				TypedMembers[type] = members;
			}

			for (int i = 0; i < members.Length; i++)
			{
				object value = ReadMemberValue(target, members[i]);
				if (value != null) return value;
			}
			return null;
		}

		private static object ReadMemberValue(object target, MemberInfo member)
		{
			try
			{
				FieldInfo field = member as FieldInfo;
				return field != null
					? field.GetValue(target)
					: ((PropertyInfo)member).GetValue(target, null);
			}
			catch { return null; }
		}

		private static Profile SessionProfile()
		{
			try
			{
				ItemUiContext context = ItemUiContext.Instance;
				IClientSession session = context != null ? context.ClientSession : null;
				return session != null ? session.Profile : null;
			}
			catch { return null; }
		}

		private static string SessionProfileId()
		{
			try
			{
				Profile profile = SessionProfile();
				return profile != null ? profile.Id : null;
			}
			catch { return null; }
		}

		private static string SessionEquipmentId()
		{
			try
			{
				Profile profile = SessionProfile();
				Inventory inventory = profile != null ? profile.Inventory : null;
				InventoryEquipment equipment = inventory != null ? inventory.Equipment : null;
				return equipment != null ? equipment.Id : null;
			}
			catch { return null; }
		}

		private static object ReadMember(object target, string[] names)
		{
			if (target == null) return null;

			Type type = target.GetType();
			for (int i = 0; i < names.Length; i++)
			{
				try
				{
					PropertyInfo property = type.GetProperty(names[i],
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (property != null && property.CanRead &&
						property.GetIndexParameters().Length == 0)
					{
						return property.GetValue(target, null);
					}

					FieldInfo field = type.GetField(names[i],
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (field != null) return field.GetValue(target);
				}
				catch { }
			}
			return null;
		}

		private static string ReadId(object target)
		{
			return ReadMember(target, IdMemberNames) as string;
		}

		internal static void Reset()
		{
			Players = new ConditionalWeakTable<Player, Owner>();
			EquipmentOwners.Clear();
			ProfileOwners.Clear();
			TypedMembers.Clear();
		}

		private static ConditionalWeakTable<Player, Owner> Players =
			new ConditionalWeakTable<Player, Owner>();

		/// <summary>Equipment id to audience, learned from live players and from resolved previews.</summary>
		private static readonly Dictionary<string, Audience> EquipmentOwners =
			new Dictionary<string, Audience>();

		/// <summary>Profile id to audience, learned from live players.</summary>
		private static readonly Dictionary<string, Audience> ProfileOwners =
			new Dictionary<string, Audience>();

		/// <summary>Per type: members declared as a Profile, whatever their name.</summary>
		private static readonly Dictionary<Type, MemberInfo[]> TypedMembers =
			new Dictionary<Type, MemberInfo[]>();

		// The owner/profile chain of an item is not part of this plugin's compile-time contract with
		// the game, so the members are looked up by name.
		private static readonly string[] OwnerMemberNames = { "Owner", "_owner", "owner" };

		private static readonly string[] ProfileMemberNames = { "Profile", "_profile", "profile" };

		private static readonly string[] IdMemberNames = { "Id", "_id", "id", "ProfileId" };

		private sealed class Owner
		{
			internal Audience Audience;
		}
	}
}
