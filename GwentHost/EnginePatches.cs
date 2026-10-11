using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using GwentGameplay;
using HarmonyLib;
using RedTools;

namespace GwentHost
{
    // Engine speed patches: the host installs them at GWENT_ENGINE_PATCHES
    // level 2 unless the variable says otherwise (0 turns them off, 1 keeps
    // only the first two patches).
    // The client mod (the AI on a stock client) never loads this class.
    //
    // Each patch removes work whose result no rule reads, so games stay
    // byte-identical on both runtimes (Mono and .NET).
    //
    // Level 1 is the two patches below. Level 2 adds the pack
    // (EnginePack.cs, EventPatches.cs): memoized template filters, a cheaper
    // authority check, and a rewrite of the engine's event class. The pack
    // is a separate level, so it can be turned off apart from level 1.
    // GWENT_ENGINE_PACK=name,name picks parts of the pack (default: all).
    public static class EnginePatches
    {
        public const string LevelVariable = "GWENT_ENGINE_PATCHES";
        public const string PackVariable = "GWENT_ENGINE_PACK";

        // 2 (unset or empty), 0 (off), 1 or 2. Any other value is an error: a
        // misspelled switch must not turn a pack run into a run without it.
        public static int Level
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(LevelVariable);
                switch (value)
                {
                    case null:
                    case "":
                    case "2": return 2;
                    case "0": return 0;
                    case "1": return 1;
                    default: throw new InvalidOperationException($"{LevelVariable}={value}: expected 0, 1 or 2");
                }
            }
        }

        public static bool Enabled => Level >= 1;

        public static void Install(Harmony harmony)
        {
            int level = Level;
            Pack pack = level >= 2 ? Pack.FromEnvironment() : Pack.None;
            Install(harmony, level, pack);
        }

        public static void Install(Harmony harmony, int level, Pack pack)
        {
            harmony.Patch(AccessTools.Method(typeof(CardPlayedHistoryEntry), "SetupClonedCard"),
                prefix: new HarmonyMethod(typeof(EnginePatches), nameof(SetupClonedCardPrefix)));
            harmony.Patch(AccessTools.Method(typeof(CardPlayedHistoryEntry), nameof(CardPlayedHistoryEntry.Copy)),
                prefix: new HarmonyMethod(typeof(EnginePatches), nameof(CardPlayedCopyPrefix)));
            // The pack's event rewrite replaces this constructor's list
            // sizing by a lazy list.
            if (!pack.Events)
            {
                harmony.Patch(AccessTools.Constructor(typeof(BaseEvent), Type.EmptyTypes),
                    transpiler: new HarmonyMethod(typeof(EnginePatches), nameof(BaseEventConstructorTranspiler)));
            }
            if (level >= 2)
            {
                EnginePack.Install(harmony, pack);
            }
        }

        // History snapshot cards: a played-card history entry keeps a second
        // card, a snapshot of the played card for the history panel. Only
        // the client's UI reads it, so a game copy (a simulation) never
        // does, yet each copy builds one per entry, and per card played in
        // it: a whole card with its seven events and data. A copy's entries
        // keep no snapshot. The snapshot's update hook on a hidden card's
        // token event goes too; it only refreshed the snapshot.
        private static bool SetupClonedCardPrefix(CardPlayedHistoryEntry __instance, Card card)
        {
            if (card.GameController.IsMainController)
            {
                return true;
            }
            __instance.ClonedCard = null;
            return false;
        }

        private static bool CardPlayedCopyPrefix(CardPlayedHistoryEntry __instance, AHistoryEntry other, GameController controller)
        {
            if (controller.IsMainController)
            {
                return true;
            }
            CardPlayedHistoryEntry source = (CardPlayedHistoryEntry)other;
            __instance.PlayerID = source.PlayerID;
            __instance.OriginalCard = controller.CardManager.GetCard(source.OriginalCard.Id);
            __instance.ClonedCard = null;
            __instance.From = source.From;
            __instance.To = source.To;
            return false;
        }

        // Listener lists: every engine event starts its listener list with
        // room for four, and most of a card's seven events never get a
        // listener. The list starts empty instead and grows to four on its
        // first listener.
        private static IEnumerable<CodeInstruction> BaseEventConstructorTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i + 1 < result.Count; i++)
            {
                if (result[i].opcode == OpCodes.Ldc_I4_4 && result[i + 1].opcode == OpCodes.Newobj &&
                    Equals(result[i + 1].operand, AccessTools.Constructor(typeof(List<KeyValuePair<object, int>>), new[] { typeof(int) })))
                {
                    result[i].opcode = OpCodes.Ldc_I4_0;
                    replaced++;
                }
            }
            if (replaced != 1)
            {
                throw new InvalidOperationException($"BaseEvent's constructor sizes {replaced} listener lists, expected 1");
            }
            return result;
        }
    }
}
