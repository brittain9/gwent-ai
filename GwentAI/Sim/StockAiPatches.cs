using System.Threading;
using GwentCore;
using GwentGameplay.AI;
using HarmonyLib;

namespace GwentAI.Sim
{
    // The stock AIPlayer paces itself with real-time DelayedDecisions: a
    // configured "thinking" wait, cosmetic highlight delays and 0.2 s polls
    // of its background search. None of that changes what it decides, so
    // with these patches those fire on the next update.
    //
    // ForceFinishDecision is different: one second after the play request
    // it truncates the background search, so how far the search got would
    // depend on CPU speed and load. With these patches it never fires; the
    // AI keeps polling until its search completes, so every game is
    // reproducible at any configured depth. Each suppressed firing is counted
    // as an overrun: a search Practice would have cut short.
    //
    // The client mod applies them for unattended games and the headless
    // host for every game it runs (Program.Engine), so the class lives in
    // the AI assembly, which both load.
    public static class StockAiPatches
    {
        private static int s_SearchOverruns;

        public static int SearchOverruns => s_SearchOverruns;

        public static void Apply(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(DelayedDecision), "TryExecute"),
                prefix: new HarmonyMethod(typeof(StockAiPatches), nameof(TryExecutePrefix)));
            harmony.Patch(AccessTools.Method(typeof(AIModule_TreeSearch), "ForceFinishDecision"),
                prefix: new HarmonyMethod(typeof(StockAiPatches), nameof(ForceFinishPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Node), nameof(Node.ToString)),
                finalizer: new HarmonyMethod(typeof(StockAiPatches), nameof(NodeToStringFinalizer)));
        }

        // The stock search describes a failed node for its error message,
        // and describing some nodes throws; on its thread-pool thread that
        // would end the whole host. The description is only for the log.
        private static System.Exception NodeToStringFinalizer(System.Exception __exception, ref string __result)
        {
            if (__exception != null)
            {
                __result = "(node)";
            }
            return null;
        }

        private static bool TryExecutePrefix(DelayedDecision __instance, ref bool __result)
        {
            if (!__instance.Watch.IsRunning || __instance.Reaction.Method.Name == "ForceFinishDecision")
            {
                return true;
            }
            __instance.Watch.Stop();
            __instance.Reaction();
            __result = true;
            return false;
        }

        private static bool ForceFinishPrefix()
        {
            Interlocked.Increment(ref s_SearchOverruns);
            return false;
        }
    }
}
