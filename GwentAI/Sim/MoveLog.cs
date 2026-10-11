using System;
using System.IO;
using GwentGameplay;
using HarmonyLib;

namespace GwentAI.Sim
{
    // Reports every decision the main game applies, and can write them as a
    // move list (see Move) from which Replay rebuilds the game. Copies made
    // by searches are skipped.
    public static class MoveLog
    {
        private static TextWriter s_Writer;
        private static bool s_Installed;

        public static event Action<Move> Applied;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (s_Installed)
            {
                return;
            }
            s_Installed = true;
            harmony.Patch(AccessTools.Method(typeof(ActionManager), nameof(ActionManager.ApplyAction)),
                prefix: new HarmonyMethod(typeof(MoveLog), nameof(ApplyActionPrefix)));
        }

        public static void Begin(string path)
        {
            End();
            s_Writer = new StreamWriter(path);
        }

        public static void End()
        {
            if (s_Writer != null)
            {
                s_Writer.Close();
                s_Writer = null;
            }
        }

        public static string Describe(GameController game, Move move)
        {
            Card card = move.CardId == 0 ? null : game.CardManager.GetCard(move.CardId);
            return card == null ? move.ToString() : move + "  # " + card.Template.DebugName;
        }

        private static void ApplyActionPrefix(ActionManager __instance, AAction action)
        {
            if (!__instance.GameController.IsMainController)
            {
                return;
            }
            Move move = Move.FromAction(action);
            if (move == null)
            {
                return;
            }
            s_Writer?.WriteLine(Describe(__instance.GameController, move));
            Applied?.Invoke(move);
        }
    }
}
