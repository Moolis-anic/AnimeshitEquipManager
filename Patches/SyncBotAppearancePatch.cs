using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using AnimeshitEquipManager.Modules;
using EFT;
using HarmonyLib;

namespace AnimeshitEquipManager.Patches
{
	[HarmonyPatch]
	internal static class SyncBotAppearancePatch
	{
		private static MethodBase TargetMethod()
		{
			var asyncStateMachine = AccessTools
				.Method(typeof(ClientBackendSession), "LoadBots")
				.GetCustomAttribute<AsyncStateMachineAttribute>()
				.StateMachineType;

			return AccessTools.Method(asyncStateMachine, "MoveNext");
		}

		internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var output = new List<CodeInstruction>();
			int replacements = 0;
			MethodInfo replacement = AccessTools.Method(
				typeof(BotAppearanceControl), nameof(BotAppearanceControl.SendBotsAfterSync));

			foreach (var instruction in instructions)
			{
				if (instruction.operand is MethodInfo mi
					&& mi.DeclaringType == typeof(Backend)
					&& mi.Name == "Send"
					&& mi.IsGenericMethod
					&& mi.GetGenericArguments()[0] == typeof(ProfileDescriptor[])
					&& mi.GetParameters().Length == 1
					&& mi.GetParameters()[0].ParameterType == typeof(SendRequest))
				{
					var newInstr = new CodeInstruction(instruction);
					newInstr.opcode = OpCodes.Call;
					newInstr.operand = replacement;
					output.Add(newInstr);
					replacements++;
				}
				else
				{
					output.Add(instruction);
				}
			}

			if (replacements != 1)
			{
				throw new InvalidOperationException(
					"Expected exactly one awaited bot-generation request in SPT 4.1.6 LoadBots.");
			}
			return output;
		}
	}
}