using System.IO;
using GwentGameplay;
using HarmonyLib;

namespace GwentAI.Sim
{
    // Writes every action the main game applies, one per line, so two runs
    // of a seed (in different runtimes, builds or agents) can be diffed to
    // the first action where they part. Copies made by searches are skipped.
    public static class ActionTrace
    {
        private static TextWriter s_Writer;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ActionManager), nameof(ActionManager.ApplyAction)),
                prefix: new HarmonyMethod(typeof(ActionTrace), nameof(ApplyActionPrefix)));
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

        private static void ApplyActionPrefix(ActionManager __instance, AAction action)
        {
            if (s_Writer != null && __instance.GameController.IsMainController)
            {
                s_Writer.WriteLine(action);
            }
        }
    }
}
