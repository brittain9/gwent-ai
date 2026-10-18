using System;
using System.Collections.Generic;
using System.IO;
using GwentAI.Search;
using GwentGameplay;
using HarmonyLib;

namespace GwentAI.Sim
{
    // The oracle files beside a traced game's ID.txt (the text is
    // OracleFormat's):
    //
    //   ID.legal  GWENT_TRACE_LEGAL=1: at every request delivered to a seat
    //             of the main game, the Actions.Legal keys the acting seat
    //             sees, full (groupPlays off) and, for a play request,
    //             grouped (groupPlays on).
    //   ID.state  GWENT_TRACE_STATE=1: the state dump (StateDump) at the
    //             same points; GWENT_TRACE_STATE=actions also after every
    //             ApplyAction of the main game.
    //
    // Both are off by default, and off they install no patch: the host's
    // games are then the games of a host without this file. They write only
    // while a bench traces (BenchSession, --trace) and only for the main
    // game; search copies are skipped. On, they read the game and change
    // nothing.
    //
    // The delivery point is inside ActionManager.ApplyAction,
    // right after the request's Apply() and before the seat's
    // OnRequestReceived. A postfix of ApplyAction would be too late: a
    // synchronous seat (RandomPlayer, rand-sm) has queued its answer by then.
    // One mulligan request takes several cards, and the engine re-asks the
    // seat through OnCardMulligan, which is not a delivery: those re-asks
    // write nothing.
    public static class OracleTrace
    {
        public const string LegalVariable = "GWENT_TRACE_LEGAL";
        public const string StateVariable = "GWENT_TRACE_STATE";

        public static readonly bool LegalOn = Flag(LegalVariable, "1");
        public static readonly bool StateOn = Flag(StateVariable, "1", "actions");
        public static readonly bool StateEveryAction = Environment.GetEnvironmentVariable(StateVariable) == "actions";
        public static bool Enabled => LegalOn || StateOn;

        private static bool s_Installed;
        private static TextWriter s_Legal;
        private static TextWriter s_State;
        // ApplyAction calls of the main game so far in this game: the line
        // number of ID.txt of the call in progress.
        private static int s_TraceLines;
        // The main game's ApplyAction calls in progress, innermost last, with
        // their trace lines.
        private static readonly List<KeyValuePair<AAction, int>> s_Applying = new List<KeyValuePair<AAction, int>>();
        private static readonly List<string> s_NoKeys = new List<string>();

        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (s_Installed || !Enabled)
            {
                return;
            }
            s_Installed = true;
            harmony.Patch(AccessTools.Method(typeof(ActionManager), nameof(ActionManager.ApplyAction)),
                prefix: new HarmonyMethod(typeof(OracleTrace), nameof(ApplyActionPrefix)),
                postfix: new HarmonyMethod(typeof(OracleTrace), nameof(ApplyActionPostfix)));
            harmony.Patch(AccessTools.Method(typeof(AAction), nameof(AAction.Apply)),
                postfix: new HarmonyMethod(typeof(OracleTrace), nameof(ApplyPostfix)));
        }

        // `basePath` is the trace directory and the match id: the files are
        // basePath.legal and basePath.state, for the flags that are on.
        public static void Begin(string basePath)
        {
            End();
            s_TraceLines = 0;
            s_Applying.Clear();
            if (LegalOn)
            {
                s_Legal = Writer(basePath + ".legal");
            }
            if (StateOn)
            {
                s_State = Writer(basePath + ".state");
            }
        }

        public static void End()
        {
            s_Legal?.Close();
            s_Legal = null;
            s_State?.Close();
            s_State = null;
        }

        // UTF-8 without a byte order mark and "\n" line ends, as ID.txt.
        private static TextWriter Writer(string path)
        {
            return new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        }

        private static bool Flag(string variable, params string[] allowed)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(value) || value == "0")
            {
                return false;
            }
            if (Array.IndexOf(allowed, value) < 0)
            {
                throw new ArgumentException($"{variable}={value}: use {string.Join(" or ", allowed)}, or leave it unset");
            }
            return true;
        }

        private static void ApplyActionPrefix(ActionManager __instance, AAction action)
        {
            if ((s_Legal != null || s_State != null) && __instance.GameController.IsMainController)
            {
                s_Applying.Add(new KeyValuePair<AAction, int>(action, ++s_TraceLines));
            }
        }

        private static void ApplyActionPostfix(ActionManager __instance)
        {
            if ((s_Legal == null && s_State == null) || !__instance.GameController.IsMainController || s_Applying.Count == 0)
            {
                return;
            }
            int line = s_Applying[s_Applying.Count - 1].Value;
            s_Applying.RemoveAt(s_Applying.Count - 1);
            if (StateEveryAction && s_State != null)
            {
                WriteState(OracleFormat.ActionHeader(line), __instance.GameController);
            }
        }

        // AAction.Apply also runs for the parts of a CompoundAction; only the
        // request ApplyAction itself is applying is a delivery.
        private static void ApplyPostfix(AAction __instance)
        {
            if ((s_Legal == null && s_State == null) || s_Applying.Count == 0 || !(__instance is ARequestAction request))
            {
                return;
            }
            KeyValuePair<AAction, int> applying = s_Applying[s_Applying.Count - 1];
            if (!ReferenceEquals(applying.Key, request) || !request.CanProcess)
            {
                return;
            }
            GameController game = request.GameController;
            string type = request.GetRequestType().ToString();
            string player = request.PlayerId.ToString();
            if (s_Legal != null)
            {
                Player seat = game.PlayerManager.GetPlayer(request.PlayerId);
                s_Legal.WriteLine(OracleFormat.Legal(request.Id, type, player, OracleFormat.Full, Keys(Actions.Legal(request, seat, s_NoKeys))));
                if (request is RequestPlayCardAction)
                {
                    s_Legal.WriteLine(OracleFormat.Legal(request.Id, type, player, OracleFormat.Grouped,
                                                         Keys(Actions.Legal(request, seat, s_NoKeys, groupPlays: true))));
                }
            }
            if (s_State != null)
            {
                WriteState(OracleFormat.DecisionHeader(applying.Value, request.Id, type, player), game);
            }
        }

        private static List<string> Keys(List<GameAction> legal)
        {
            List<string> keys = new List<string>(legal.Count);
            foreach (GameAction action in legal)
            {
                keys.Add(action.Key);
            }
            return keys;
        }

        private static void WriteState(string header, GameController game)
        {
            s_State.WriteLine(header);
            foreach (string line in StateDump.Lines(game))
            {
                s_State.WriteLine(line);
            }
            s_State.WriteLine(OracleFormat.End);
        }
    }
}
