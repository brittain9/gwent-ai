using System;
using GwentGameplay;
using HarmonyLib;
using RedLogger;

namespace GwentHost
{
    // Engine work a single-player host game never needs, skipped. None of it
    // changes the game: the same moves give the same results and traces.
    //
    // - The state hash: at every turn's end the engine hashes each player's
    //   whole state (SHA1 over a serialized copy plus a text dump of every
    //   number) for multiplayer desync checks. Copies already skip it
    //   (SearchPatches); in a single-player game the result only travels in
    //   ReportPlayerStatusAction to the [Cheat] check, which runs in
    //   multiplayer only. Copies of a game also copy its text dump.
    // - Network serialization: the main game serializes every applied action
    //   to a string for its peer and keeps it in a log cache, even in
    //   single-player, where nothing is sent.
    public static class HostPatches
    {
        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AStateHasher), nameof(AStateHasher.ComputeStateHash)),
                prefix: new HarmonyMethod(typeof(HostPatches), nameof(ComputeStateHashPrefix)));
            harmony.Patch(AccessTools.Method(typeof(ActionManager), "TrySendAction"),
                prefix: new HarmonyMethod(typeof(HostPatches), nameof(TrySendActionPrefix)));
        }

        private static bool ComputeStateHashPrefix(GameController gameController)
        {
            return gameController.IsMultiplayer;
        }

        private static bool TrySendActionPrefix(ActionManager __instance, ref bool __result)
        {
            if (__instance.GameController.IsMultiplayer)
            {
                return true;
            }
            __result = false;
            return false;
        }
    }

    // The engine's logger without its debug and info levels. The main game
    // logs every action, trigger, ability and card move it applies, which
    // formats each one and writes a flushed line: half of a random game's
    // time. Warnings and errors still reach the console.
    // GWENT_HOST_DEBUG_LOG=1 keeps the full log; action traces record every
    // action more cheaply.
    public class QuietLogger : IRedLogger
    {
        private readonly IRedLogger m_Inner;

        public QuietLogger(IRedLogger inner)
        {
            m_Inner = inner;
        }

        public bool IsBatching => m_Inner.IsBatching;

        public IRedLogger GetLogger(string name)
        {
            return new QuietLogger(m_Inner.GetLogger(name));
        }

        public IRedLogger GetLogger(LogSettings settings)
        {
            return new QuietLogger(m_Inner.GetLogger(settings));
        }

        public void Log(LC channels, string format, params object[] args)
        {
        }

        public void Log(LC channels, string log)
        {
        }

        public void LogDebug(LC channels, string format, params object[] args)
        {
        }

        public void LogDebug(LC channels, string log)
        {
        }

        public void LogWarning(LC channels, string format, params object[] args)
        {
            m_Inner.LogWarning(channels, format, args);
        }

        public void LogWarning(LC channels, string log)
        {
            m_Inner.LogWarning(channels, log);
        }

        public void LogError(LC channels, string format, params object[] args)
        {
            m_Inner.LogError(channels, format, args);
        }

        public void LogError(LC channels, string log)
        {
            m_Inner.LogError(channels, log);
        }

        public void LogCritical(LC channels, Exception exception, string format, params object[] args)
        {
            m_Inner.LogCritical(channels, exception, format, args);
        }

        public void LogCritical(LC channels, Exception exception, string log)
        {
            m_Inner.LogCritical(channels, exception, log);
        }

        public void Dispose()
        {
            m_Inner.Dispose();
        }

        public void StartBatch()
        {
            m_Inner.StartBatch();
        }

        public void EndBatch()
        {
            m_Inner.EndBatch();
        }

        public void UpdateParams(LogParams param)
        {
            m_Inner.UpdateParams(param);
        }

        public void Update()
        {
            m_Inner.Update();
        }
    }
}
