using GwentGameplay;
using HarmonyLib;

namespace GwentAI.Sim
{
    // Gives the main game a virtual clock: every update advances its game
    // time by exactly Tick milliseconds. The engine's presentation pauses
    // (after a pass, between rounds) then last a fixed number of updates
    // instead of wall-clock seconds, so an unattended game runs as fast as
    // the CPU allows and its update count no longer depends on load.
    //
    // TimeManager.Update is the only caller of GetRealTimeMs, and it stores
    // the result as m_LastTime, so "last time plus one tick" is a complete
    // clock. Copies (the stock AI's search) keep real time, so the stock AI
    // plays exactly as in Practice.
    public static class VirtualClock
    {
        public const long Tick = 100;

        public static void Install(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(TimeManager), nameof(TimeManager.GetRealTimeMs)),
                prefix: new HarmonyMethod(typeof(VirtualClock), nameof(GetRealTimeMsPrefix)));
        }

        private static bool GetRealTimeMsPrefix(TimeManager __instance, long ___m_LastTime, ref long __result)
        {
            if (!__instance.GameController.IsMainController)
            {
                return true;
            }
            __result = ___m_LastTime + Tick;
            return false;
        }
    }
}
