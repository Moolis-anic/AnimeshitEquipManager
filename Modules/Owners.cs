using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;

namespace AnimeshitEquipManager.Modules
{
	internal static class Owners
	{
		internal static void Register(Player player, bool aiControlled)
		{
			if (player == null) return;

			Audience audience = aiControlled ? Audience.AI : Audience.Player;

			Players.Remove(player);
			Players.Add(player, new Owner
			{
				Audience = audience
			});

			RememberEquipment(player, audience);
		}

		/// <summary>
		/// Remember the profile and equipment of a player, so a model that has no Player ancestor
		/// (inventory paper-doll, profile screen, bot preview) can still be matched back to an
		/// audience through the profile or the equipment it renders.
		/// </summary>
		internal static void RememberEquipment(Player player, Audience audience)
		{
			if (player == null || audience == Audience.Unknown) return;

			try
			{
				Profile profile = player.Profile;
				if (profile == null) return;

				if (!string.IsNullOrEmpty(profile.Id)) ProfileOwners[profile.Id] = audience;

				Inventory inventory = profile.Inventory;
				InventoryEquipment equipment = inventory != null ? inventory.Equipment : null;
				if (equipment != null && !string.IsNullOrEmpty(equipment.Id))
				{
					EquipmentOwners[equipment.Id] = audience;
				}
			}
			catch { }
		}

		internal static Audience Resolve(PlayerBody.SlotView view)
		{
			Player player = view._playerBody != null
				? view._playerBody.GetComponentInParent<Player>()
				: null;

			string equipmentId = EquipmentIdOf(view);

			if (player != null)
			{
				Audience audience;
				if (player.IsYourPlayer)
				{
					audience = Audience.Player;
				}
				else if (PitTeamInterop.IsTeammate(player))
				{
					audience = Audience.Teammate;
				}
				else if (Players.TryGetValue(player, out Owner owner))
				{
					audience = owner.Audience;
				}
				else if (player.IsAI)
				{
					audience = Audience.AI;
				}
				else
				{
					audience = Audience.Unknown;
				}

				if (audience != Audience.Unknown)
				{
					if (!string.IsNullOrEmpty(equipmentId)) EquipmentOwners[equipmentId] = audience;
					RememberEquipment(player, audience);
				}
				return audience;
			}

			// The model has no Player ancestor: an inventory paper-doll, a profile screen or a bot
			// preview. Match it through the equipment it renders, then through the equipment of the
			// current session, which always belongs to the local player.
			if (!string.IsNullOrEmpty(equipmentId))
			{
				Audience cached;
				if (EquipmentOwners.TryGetValue(equipmentId, out cached)) return cached;

				string sessionEquipmentId = SessionEquipmentId();
				if (!string.IsNullOrEmpty(sessionEquipmentId) && sessionEquipmentId == equipmentId)
				{
					EquipmentOwners[equipmentId] = Audience.Player;
					return Audience.Player;
				}
			}

			// Previews of a profile that is not loaded as a live player (other player profile,
			// team inspect, matchmaker side selection) keep the profile on an ancestor of the
			// model, which is the most reliable source: that is the profile the screen was opened
			// for.
			string hostName;
			Profile hostProfile = ProfileOfHost(view, out hostName);
			Audience hostAudience = AudienceOfProfile(ProfileIdOf(hostProfile));
			if (hostAudience != Audience.Unknown)
			{
				if (!string.IsNullOrEmpty(equipmentId)) EquipmentOwners[equipmentId] = hostAudience;
				return hostAudience;
			}

			// Last resort: the equipment links back to its owner profile through the item owner.
			Audience profileAudience = AudienceOfProfile(EquipmentProfileId(view));
			if (profileAudience != Audience.Unknown)
			{
				if (!string.IsNullOrEmpty(equipmentId)) EquipmentOwners[equipmentId] = profileAudience;
				return profileAudience;
			}

			return Audience.Unknown;
		}

		/// <summary>
		/// Equipment id the view renders: the parent of the slot, or the equipment found through
		/// the address of the contained item when the slot does not expose a parent directly.
		/// </summary>
		internal static string EquipmentIdOf(PlayerBody.SlotView view)
		{
			Slot slot = view._slot;
			InventoryEquipment equipment = (slot != null ? slot.ParentItem : null) as InventoryEquipment;
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

		/// <summary>
		/// Audience of a profile: the local player, one of the PitFireTeam followers, or a profile
		/// that was already seen for a live player. Unknown means "no match", which leaves the
		/// preview untouched instead of hiding gear of an unrelated player.
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
		/// Profile id that owns the equipment a preview renders. The profile is only reachable
		/// through the owner chain of the equipment and that chain is game-version specific, so the
		/// members are looked up by name; a missing member simply leaves the preview unknown.
		/// </summary>
		internal static string EquipmentProfileId(PlayerBody.SlotView view)
		{
			InventoryEquipment equipment = EquipmentOf(view);
			if (equipment == null) return null;

			object owner = ReadMember(equipment, OwnerMemberNames);
			string profileId = ReadId(ReadMember(owner, ProfileMemberNames));
			if (profileId != null) return profileId;

			return ReadId(ReadMember(equipment, ProfileMemberNames));
		}

		/// <summary>Owner type of the rendered equipment, reported by the diagnostic log.</summary>
		internal static string EquipmentOwnerSummary(PlayerBody.SlotView view)
		{
			InventoryEquipment equipment = EquipmentOf(view);
			if (equipment == null) return "(noEquipment)";

			object owner = ReadMember(equipment, OwnerMemberNames);
			return owner == null ? "(noOwner)" : owner.GetType().Name;
		}

		private static InventoryEquipment EquipmentOf(PlayerBody.SlotView view)
		{
			Slot slot = view._slot;
			return (slot != null ? slot.ParentItem : null) as InventoryEquipment;
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

		/// <summary>
		/// Preview host of the rendered model: the nearest ancestor whose component carries a
		/// Profile. That is the profile the screen was opened for, so it identifies previews that
		/// are not backed by a live Player (other player profile, team inspect, matchmaker).
		/// </summary>
		internal static Profile ProfileOfHost(PlayerBody.SlotView view, out string hostName)
		{
			hostName = null;

			Transform transform = view._playerBody != null ? view._playerBody.transform : null;
			for (int depth = 0; transform != null && depth < MaxHostDepth; depth++)
			{
				GameObject gameObject = transform.gameObject;
				Profile profile = ProfileInObject(gameObject);
				if (profile != null)
				{
					hostName = HostNameOf(gameObject);
					return profile;
				}
				transform = transform.parent;
			}

			return null;
		}

		private static Profile ProfileInObject(GameObject gameObject)
		{
			Component[] components = gameObject.GetComponents<Component>();
			for (int i = 0; i < components.Length; i++)
			{
				Profile profile = ProfileInComponent(components[i]);
				if (profile != null) return profile;
			}
			return null;
		}

		private static string HostNameOf(GameObject gameObject)
		{
			Component[] components = gameObject.GetComponents<Component>();
			for (int i = 0; i < components.Length; i++)
			{
				Component component = components[i];
				if (component == null || component is Transform) continue;
				if (ProfileInComponent(component) != null) return component.GetType().Name;
			}
			return gameObject.name;
		}

		/// <summary>
		/// Reads whichever member of the component holds a Profile. The member is found by type
		/// instead of by name, because the UI classes of the game are not part of this plugin's
		/// compile-time contract and the member name is unknown.
		/// </summary>
		private static Profile ProfileInComponent(Component component)
		{
			if (component == null) return null;

			MemberInfo[] members = ProfileMembersOf(component.GetType());
			for (int i = 0; i < members.Length; i++)
			{
				try
				{
					FieldInfo field = members[i] as FieldInfo;
					object value = field != null
						? field.GetValue(component)
						: ((PropertyInfo)members[i]).GetValue(component, null);

					Profile profile = value as Profile;
					if (profile != null) return profile;
				}
				catch { }
			}
			return null;
		}

		private static MemberInfo[] ProfileMembersOf(Type type)
		{
			MemberInfo[] cached;
			if (ProfileMembers.TryGetValue(type, out cached)) return cached;

			var found = new List<MemberInfo>(1);
			try
			{
				FieldInfo[] fields = type.GetFields(
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				for (int i = 0; i < fields.Length; i++)
				{
					if (typeof(Profile).IsAssignableFrom(fields[i].FieldType)) found.Add(fields[i]);
				}

				PropertyInfo[] properties = type.GetProperties(
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				for (int i = 0; i < properties.Length; i++)
				{
					if (properties[i].CanRead && properties[i].GetIndexParameters().Length == 0 &&
						typeof(Profile).IsAssignableFrom(properties[i].PropertyType))
					{
						found.Add(properties[i]);
					}
				}
			}
			catch { }

			cached = found.ToArray();
			ProfileMembers[type] = cached;
			return cached;
		}

		private static string ProfileIdOf(Profile profile)
		{
			try { return profile != null ? profile.Id : null; }
			catch { return null; }
		}

		internal static void Reset()
		{
			Players = new ConditionalWeakTable<Player, Owner>();
			EquipmentOwners.Clear();
			ProfileOwners.Clear();
			ProfileMembers.Clear();
		}

		private static ConditionalWeakTable<Player, Owner> Players = new ConditionalWeakTable<Player, Owner>();

		private static readonly Dictionary<string, Audience> EquipmentOwners = new Dictionary<string, Audience>();

		private static readonly Dictionary<string, Audience> ProfileOwners = new Dictionary<string, Audience>();

		/// <summary>Per component type: the members that hold a Profile. Empty array = none.</summary>
		private static readonly Dictionary<Type, MemberInfo[]> ProfileMembers =
			new Dictionary<Type, MemberInfo[]>();

		// Members are looked up by name because the owner/profile chain of an item is not part of
		// this plugin's compile-time contract with the game.
		private static readonly string[] OwnerMemberNames = { "Owner", "_owner", "owner" };

		private static readonly string[] ProfileMemberNames = { "Profile", "_profile", "profile" };

		private static readonly string[] IdMemberNames = { "Id", "_id", "id", "ProfileId" };

		private sealed class Owner
		{
			internal Audience Audience;
		}

		/// <summary>
		/// Diagnostic snapshot of how a view resolves, so the possible failure reasons (no Player
		/// parent, unknown AI flag, unresolved teammate, unknown equipment id) can be told apart
		/// from a single log line, and so the host of a preview model can be identified without
		/// decompiling, because the ancestor chain of the model is included.
		/// </summary>
		internal static string Describe(PlayerBody.SlotView view)
		{
			try
			{
				var text = new StringBuilder(320);

				Player player = view._playerBody != null
					? view._playerBody.GetComponentInParent<Player>()
					: null;

				string equipmentId = EquipmentIdOf(view);

				if (player == null)
				{
					text.Append("player=null");
				}
				else
				{
					Owner owner;
					text.Append("player=ok isYour=").Append(player.IsYourPlayer)
						.Append(" isAI=").Append(player.IsAI)
						.Append(" cached=").Append(Players.TryGetValue(player, out owner))
						.Append(" profile=").Append(ProfileIdOf(player));
				}

				string sessionEquipmentId = SessionEquipmentId();
				string ownerProfileId = EquipmentProfileId(view);

				string hostName;
				Profile hostProfile = ProfileOfHost(view, out hostName);
				string hostProfileId = ProfileIdOf(hostProfile);

				Audience cachedAudience = Audience.Unknown;
				bool cached = equipmentId != null &&
					EquipmentOwners.TryGetValue(equipmentId, out cachedAudience);

				text.Append(" equipment=").Append(equipmentId ?? "(none)")
					.Append(" equipHit=").Append(cached ? cachedAudience.ToString() : "(none)")
					.Append(" sessionEquip=").Append(sessionEquipmentId ?? "(none)")
					.Append(" owner=").Append(EquipmentOwnerSummary(view))
					.Append(" ownerProfile=").Append(ownerProfileId ?? "(none)")
					.Append(" profileHit=").Append(AudienceOfProfile(ownerProfileId))
					.Append(" host=").Append(hostName ?? "(none)")
					.Append(" hostProfile=").Append(hostProfileId ?? "(none)")
					.Append(" hostHit=").Append(AudienceOfProfile(hostProfileId))
					.Append(" slot=").Append(view._slot != null ? view._slot.ID : "(none)")
					.Append(" contained=").Append(ContainedItemName(view))
					.Append(" line=").Append(Lineage(view));

				return text.ToString();
			}
			catch (Exception ex)
			{
				return "describeFailed=" + ex.Message;
			}
		}

		/// <summary>
		/// Ancestor chain of the rendered model: "name(ComponentA,ComponentB) &lt; parent(...)".
		/// Ancestors that look like a preview host are marked, so the log itself tells which class
		/// has to be patched for the inventory paper-doll / Hideout preview.
		/// </summary>
		private static string Lineage(PlayerBody.SlotView view)
		{
			Transform transform = view._playerBody != null ? view._playerBody.transform : null;
			if (transform == null) return "(noBody)";

			var text = new StringBuilder(160);
			for (int depth = 0; transform != null && depth < MaxLineageDepth; depth++)
			{
				if (depth > 0) text.Append(" < ");
				text.Append(transform.name).Append('(')
					.Append(ComponentNames(transform.gameObject)).Append(')');
				if (IsPreviewHost(transform.gameObject)) text.Append("[PREVIEW?]");
				transform = transform.parent;
			}

			if (transform != null) text.Append(" < ...");
			return text.ToString();
		}

		/// <summary>Name of the outermost ancestor of the rendered model; used to dedupe logs.</summary>
		internal static string RootName(PlayerBody.SlotView view)
		{
			Transform transform = view._playerBody != null ? view._playerBody.transform : null;
			if (transform == null) return "(noBody)";

			for (int depth = 0; transform.parent != null && depth < MaxLineageDepth; depth++)
			{
				transform = transform.parent;
			}
			return transform.name;
		}

		private static string ComponentNames(GameObject gameObject)
		{
			var text = new StringBuilder(48);
			int count = 0;

			Component[] components = gameObject.GetComponents<Component>();
			for (int i = 0; i < components.Length; i++)
			{
				Component component = components[i];
				if (component == null || component is Transform) continue;
				if (count++ > 0) text.Append(',');
				text.Append(component.GetType().Name);
				if (count >= 3) break;
			}

			return count == 0 ? "-" : text.ToString();
		}

		/// <summary>
		/// True when any component of the object looks like a character preview host. Used only
		/// for the diagnostic, never for the visibility decision itself.
		/// </summary>
		private static bool IsPreviewHost(GameObject gameObject)
		{
			Component[] components = gameObject.GetComponents<Component>();
			for (int i = 0; i < components.Length; i++)
			{
				Component component = components[i];
				if (component == null) continue;

				string name = component.GetType().Name;
				for (int hint = 0; hint < PreviewHostHints.Length; hint++)
				{
					if (name.IndexOf(PreviewHostHints[hint], StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static string ContainedItemName(PlayerBody.SlotView view)
		{
			try
			{
				if (view.ContainedItem == null) return "(none)";
				Item item = view.ContainedItem.Value;
				return item == null ? "(empty)" : item.GetType().Name + ":" + item.Id;
			}
			catch { return "(unreadable)"; }
		}

		private static string ProfileIdOf(Player player)
		{
			try { return player.ProfileId ?? "(null)"; }
			catch { return "(unreadable)"; }
		}

		private const int MaxLineageDepth = 8;

		/// <summary>How far up the model hierarchy a preview host is searched for.</summary>
		private const int MaxHostDepth = 10;

		private static readonly string[] PreviewHostHints =
		{
			"ModelView", "Preview", "InventoryScreen", "Hideout", "Dresser", "Trading"
		};
	}
}