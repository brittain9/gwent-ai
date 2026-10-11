using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GwentAI.Search
{
    public enum Phase
    {
        // Playout.Clone: the engine's GameController.Clone, the seats' copy
        // (Player.Copy and restrictions), and the repairs after it.
        CloneEngine,
        CloneSeats,
        CloneRepair,
        // Belief.Determinize.
        Determinize,
        // Playout.Run, which holds every phase below up to Evaluate.
        Run,
        // Ismcts.Decide: the search's step choice, which holds Legal,
        // RolloutPlay, RolloutOther and the tree's own selection.
        Decide,
        Legal,
        RolloutPlay,
        RolloutOther,
        // Move.Apply: the seat's answer handed to the engine.
        Apply,
        // The leaf evaluator at the round horizon.
        Evaluate,
        // PolicyNet.Evaluate (prior and play-out policy alike): the seat's
        // view encoded (StateEncoder), then the forward pass (Scores).
        PolicyEncode,
        PolicyScore,
        // Rollout: the playable cards of a play, the positions of a card,
        // and a step drawn from the play-out policy (encode, forward pass,
        // softmax, draw).
        RolloutPlayable,
        RolloutPositions,
        RolloutPolicy,
        Count,
    }

    // Phase timers for profiling a search, off by default: set
    // GWENT_PHASE_CLOCK to a file path and the process appends each phase's
    // total (ms) and call count to it at exit, with the GC counts. Phases
    // nest (see Phase), so the totals overlap. Off, a timer costs one
    // static bool test. Each finished search also appends its own share of
    // every phase (Mark), so one process can time several searches.
    public static class PhaseClock
    {
        private static readonly string s_Path = Environment.GetEnvironmentVariable("GWENT_PHASE_CLOCK");
        public static readonly bool On = !string.IsNullOrEmpty(s_Path);
        private static readonly long[] s_Ticks = new long[(int)Phase.Count];
        private static readonly long[] s_Calls = new long[(int)Phase.Count];
        private static readonly long[] s_MarkTicks = new long[(int)Phase.Count];
        private static readonly long[] s_MarkCalls = new long[(int)Phase.Count];
        private static long s_MarkCpuTicks, s_MarkWall = Stopwatch.GetTimestamp();
        private static int s_Searches;

        static PhaseClock()
        {
            if (On)
            {
                AppDomain.CurrentDomain.ProcessExit += (sender, args) => Write();
            }
        }

        public static long Start()
        {
            return On ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Stop(Phase phase, long start)
        {
            if (On)
            {
                s_Ticks[(int)phase] += Stopwatch.GetTimestamp() - start;
                s_Calls[(int)phase]++;
            }
        }

        // Appends the phases' time and calls since the previous mark, with the
        // search's iterations and the process CPU time in between:
        // "search N iterations I cpu C ms wall W ms" then one "  PHASE ms calls" line
        // per phase that ran.
        public static void Mark(int iterations)
        {
            if (!On)
            {
                return;
            }
            long cpu = Process.GetCurrentProcess().TotalProcessorTime.Ticks, wall = Stopwatch.GetTimestamp();
            StringBuilder text = new StringBuilder();
            text.Append("search ").Append(++s_Searches).Append(" iterations ").Append(iterations).Append(" cpu ")
                .Append(((cpu - s_MarkCpuTicks) / (double)TimeSpan.TicksPerMillisecond).ToString("F0")).Append(" ms wall ")
                .Append(((wall - s_MarkWall) * 1000.0 / Stopwatch.Frequency).ToString("F0")).Append(" ms\n");
            s_MarkCpuTicks = cpu;
            s_MarkWall = wall;
            for (int i = 0; i < (int)Phase.Count; i++)
            {
                long ticks = s_Ticks[i] - s_MarkTicks[i], calls = s_Calls[i] - s_MarkCalls[i];
                s_MarkTicks[i] = s_Ticks[i];
                s_MarkCalls[i] = s_Calls[i];
                if (calls > 0)
                {
                    text.Append("  ").Append((Phase)i).Append(' ').Append((ticks * 1000.0 / Stopwatch.Frequency).ToString("F1")).Append(" ms ")
                        .Append(calls).Append(" calls\n");
                }
            }
            File.AppendAllText(s_Path, text.ToString());
        }

        private static void Write()
        {
            StringBuilder text = new StringBuilder();
            text.Append("pid ").Append(Process.GetCurrentProcess().Id).Append('\n');
            for (int i = 0; i < (int)Phase.Count; i++)
            {
                text.Append("phase ").Append((Phase)i).Append(' ').Append((s_Ticks[i] * 1000.0 / Stopwatch.Frequency).ToString("F0"))
                    .Append(" ms ").Append(s_Calls[i]).Append(" calls\n");
            }
            text.Append("gc ").Append(GC.CollectionCount(0)).Append(' ').Append(GC.CollectionCount(1)).Append(' ').Append(GC.CollectionCount(2))
                .Append(" (gen0 gen1 gen2 collections)\n");
            text.Append("cpu ").Append(Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds.ToString("F0")).Append(" ms\n");
            File.AppendAllText(s_Path, text.ToString());
        }
    }
}
