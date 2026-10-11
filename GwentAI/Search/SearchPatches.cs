using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GwentGameplay;
using HarmonyLib;
using RedLogger;

namespace GwentAI.Search
{
    // Engine patches a fair search depends on.
    //
    // Some card abilities read a player's starting deck list ("Get Initial
    // Deck Definitions" nodes, nine abilities in 0.9.24). They read it from
    // the game settings, which every copy of a game shares with the real one,
    // so in a determinized copy they would see the opponent's real deck. A
    // copy that Belief determinized answers them from its belief instead.
    public static class SearchPatches
    {
        // Per thread: a search on Practice's worker thread and one on the
        // main thread must not see each other's override, or
        // one copy's abilities would read the real deck lists.
        [System.ThreadStatic]
        private static GameController s_Copy;
        [System.ThreadStatic]
        private static BattleDeck[] s_Decks;

        public static bool Installed { get; private set; }

        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (Installed)
            {
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(GetInitialDeckDefinitions), "GetDeck"),
                prefix: new HarmonyMethod(typeof(SearchPatches), nameof(GetInitialDeckPrefix)));
            harmony.Patch(AccessTools.Method(typeof(AStateHasher), nameof(AStateHasher.ComputeStateHash)),
                prefix: new HarmonyMethod(typeof(SearchPatches), nameof(ComputeStateHashPrefix)));
            harmony.Patch(AccessTools.Method(typeof(ActionManager), "CreateAction", new[] { typeof(Type) }),
                prefix: new HarmonyMethod(typeof(SearchPatches), nameof(CreateActionPrefix)));
            harmony.Patch(AccessTools.Constructor(typeof(ActionManager), new[] { typeof(GameController) }),
                transpiler: new HarmonyMethod(typeof(SearchPatches), nameof(ActionManagerTranspiler)));
            harmony.Patch(AccessTools.Method(typeof(CardPlayedHistoryEntry), nameof(CardPlayedHistoryEntry.Clone)),
                prefix: new HarmonyMethod(typeof(SearchPatches), nameof(CardPlayedClonePrefix)));
            harmony.Patch(AccessTools.Method(typeof(ActionManager), nameof(ActionManager.ApplyAction)),
                transpiler: new HarmonyMethod(typeof(SearchPatches), nameof(ApplyActionTranspiler)));
            Installed = true;
        }

        // The starting decks the copy's abilities see, by seat index. A
        // thread simulates one copy at a time, so one copy per thread holds an
        // override; a later call replaces it.
        public static void OverrideInitialDecks(GameController copy, BattleDeck[] decks)
        {
            s_Copy = copy;
            s_Decks = decks;
        }

        // At every turn's end the engine hashes each player's whole state
        // (SHA1 over a serialized copy, plus a text dump of every number),
        // for multiplayer desync checks. A copy has no peer to check
        // against, and simulations spent much of their managed time on it.
        // The hash algorithm is also one static instance, which a search
        // thread and the main game must not share.
        private static bool ComputeStateHashPrefix(GameController gameController)
        {
            return gameController.IsMainController;
        }

        // Work a game copy (and, for the action pool, any game) does that
        // changes nothing in its play, skipped. Simulations spend their time
        // in copies.
        //
        // Action pool: each copy gets its own (Playout.RepairCopy), as does
        // each new game, and the engine fills an empty pool with ten new
        // actions of a type the first time it needs one, by reflection: most
        // of them are never used. A game takes a returned action if it has
        // one (the most recently returned first) and otherwise
        // makes one new action; new actions are all alike.
        private static readonly AccessTools.FieldRef<ActionManager, Dictionary<Type, Stack<AAction>>> s_ActionPool =
            AccessTools.FieldRefAccess<ActionManager, Dictionary<Type, Stack<AAction>>>("m_ActionPool");

        private static bool CreateActionPrefix(ActionManager __instance, Type type, ref AAction __result)
        {
            GameController game = __instance.GameController;
            Dictionary<Type, Stack<AAction>> pool = s_ActionPool(__instance);
            if (!pool.TryGetValue(type, out Stack<AAction> returned))
            {
                returned = new Stack<AAction>();
                pool.Add(type, returned);
            }
            AAction action = returned.Count != 0 ? returned.Pop() : NewAction(type);
            action.Reset();
            action.SetGameController(game);
            __result = action;
            return false;
        }

        // A new action of the type, as Activator.CreateInstance makes it but
        // through a compiled constructor call per type.
        private static readonly Dictionary<Type, Func<AAction>> s_ActionFactories = new Dictionary<Type, Func<AAction>>();

        private static AAction NewAction(Type type)
        {
            Func<AAction> factory;
            lock (s_ActionFactories)
            {
                if (!s_ActionFactories.TryGetValue(type, out factory))
                {
                    DynamicMethod method = new DynamicMethod("New" + type.Name, typeof(AAction), Type.EmptyTypes, typeof(SearchPatches), true);
                    ILGenerator il = method.GetILGenerator();
                    il.Emit(OpCodes.Newobj, type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null));
                    il.Emit(OpCodes.Ret);
                    factory = s_ActionFactories[type] = (Func<AAction>)method.CreateDelegate(typeof(Func<AAction>));
                }
            }
            return factory();
        }

        // Network buffer: every ActionManager allocates a 4,096-character
        // buffer that only the main game uses, to serialize actions for its
        // peer (ActionManager.TrySendAction). Copies share one.
        private static ADataBuffer s_CopyNetworkBuffer;

        private static IEnumerable<CodeInstruction> ActionManagerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo buffer = AccessTools.Constructor(typeof(StringDataBuffer), new[] { typeof(IRedLogger) });
            int replaced = 0;
            List<CodeInstruction> result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, buffer))
                {
                    // The logger is on the stack; add the controller (the
                    // constructor's argument).
                    result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                    result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SearchPatches), nameof(NetworkBuffer))));
                    replaced++;
                    continue;
                }
                result.Add(instruction);
            }
            if (replaced != 1)
            {
                throw new InvalidOperationException($"ActionManager's constructor creates {replaced} network buffers, expected 1");
            }
            return result;
        }

        private static ADataBuffer NetworkBuffer(IRedLogger logger, GameController game)
        {
            if (game.IsMainController)
            {
                return new StringDataBuffer(logger);
            }
            return s_CopyNetworkBuffer ?? (s_CopyNetworkBuffer = new StringDataBuffer(logger));
        }

        // History: a played-card entry's private constructor makes a
        // placeholder card that Copy replaces at once (a card builds seven
        // events and its data). The copy is made without it.
        private static bool CardPlayedClonePrefix(CardPlayedHistoryEntry __instance, GameController controller, ref AHistoryEntry __result)
        {
            CardPlayedHistoryEntry copy = (CardPlayedHistoryEntry)FormatterServices.GetUninitializedObject(typeof(CardPlayedHistoryEntry));
            copy.Copy(__instance, controller);
            __result = copy;
            return false;
        }

        // An unthrown exception: ApplyAction builds an ArgumentException (by
        // reflection, with a formatted message) for every action applied
        // without its "before" trigger, as timer abilities apply theirs, and
        // drops it. Its arguments are popped instead.
        private static IEnumerable<CodeInstruction> ApplyActionTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i + 1 < result.Count; i++)
            {
                if (result[i].opcode == OpCodes.Call && result[i].operand is MethodInfo method && method.DeclaringType == typeof(ExceptionHelper) &&
                    method.Name == "Create" && method.GetParameters().Length == 2 && result[i + 1].opcode == OpCodes.Pop)
                {
                    // Create(context, format) -> pop both; the Pop after it
                    // then takes the null pushed in the exception's place.
                    result[i].opcode = OpCodes.Pop;
                    result[i].operand = null;
                    result.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
                    result.Insert(i + 2, new CodeInstruction(OpCodes.Ldnull));
                    replaced++;
                }
            }
            if (replaced != 1)
            {
                throw new InvalidOperationException($"ApplyAction builds {replaced} unthrown exceptions, expected 1");
            }
            return result;
        }

        private static bool GetInitialDeckPrefix(GetInitialDeckDefinitions __instance, EPlayerId playerId, ref BattleDeck __result)
        {
            if (s_Copy == null || !ReferenceEquals(__instance.GameController, s_Copy))
            {
                return true;
            }
            __result = s_Decks[playerId.ToIndex()];
            return false;
        }
    }
}
