using System;
using System.Collections.Generic;

namespace GwentAI.Search
{
    // The work a search did, counted in operations that are the same on every
    // machine and on every run: a search's result then depends on its seeds
    // and its work budget alone (SearchSettings.WorkBudgetMs). The counts are
    // kept for every search, budget or not, so the weights can be measured
    // again offline.
    //
    // The ms fields are the time the nets spent, for calibration only: they
    // never reach a decision.
    public class WorkTally
    {
        // Simulated games started: the copy of the live game and its
        // determinization.
        public long Simulations;
        // Steps the simulated games took (Ismcts.Decide).
        public long Actions;
        // Value net passes at the leaf cuts (ValueNet.Calls).
        public long ValuePasses;
        // Play-out policy passes (PolicyNet.Calls of SearchSettings.RolloutPolicy).
        public long RolloutPasses;
        // Policy prior passes (PolicyNet.Calls of SearchSettings.Prior).
        public long PriorPasses;
        public double ValueMs, RolloutMs, PriorMs;
        // The counts at WorkWeights: what a budget in work units counts.
        public long Units;

        public static WorkTally Sum(IEnumerable<WorkTally> tallies)
        {
            WorkTally total = new WorkTally();
            foreach (WorkTally tally in tallies)
            {
                if (tally == null)
                {
                    continue;
                }
                total.Simulations += tally.Simulations;
                total.Actions += tally.Actions;
                total.ValuePasses += tally.ValuePasses;
                total.RolloutPasses += tally.RolloutPasses;
                total.PriorPasses += tally.PriorPasses;
                total.ValueMs += tally.ValueMs;
                total.RolloutMs += tally.RolloutMs;
                total.PriorMs += tally.PriorMs;
                total.Units += tally.Units;
            }
            return total;
        }
    }

    // What one operation costs, in work units. One unit is one nanosecond of
    // the .NET workers on the reference machine, so a budget of N work-ms
    // is the time budget of N ms on a machine whose operations cost what
    // they cost there. The weights are measured offline, not chosen, from
    // the search records of games on the .NET host. They hold for the forms
    // measured (Source); a form whose operations cost differently (a bigger
    // net) needs the weights measured again.
    public static class WorkWeights
    {
        // Measured offline on the search records of self-play games, pooled
        // over many searched decisions.
        // Per form the simulation cost ranges 1.16 to 1.49 M ns.
        public const string Source = "measured offline on self-play games";

        // Work units in one work-ms, the unit of SearchSettings.WorkBudgetMs.
        public const long UnitsPerMs = 1000000;

        public const long Simulation = 1267700;
        public const long Action = 104600;
        public const long ValuePass = 1799300;
        public const long RolloutPass = 35200;
        public const long PriorPass = 562100;

        public static long Units(long simulations, long actions, long valuePasses, long rolloutPasses, long priorPasses)
        {
            return simulations * Simulation + actions * Action + valuePasses * ValuePass + rolloutPasses * RolloutPass + priorPasses * PriorPass;
        }
    }

    // Where the workers of a work-budget search stop to be merged. Every
    // worker runs on until its work passes the next multiple of the spacing,
    // reports its root statistics and waits for the coordinator, which sums
    // them in worker order and applies the stopping rule (Ismcts.Continue) to
    // the sum. Where a worker stops depends on its own work alone, so the
    // merged statistics at a checkpoint, and the decision, depend on the
    // seeds and the budget alone.
    public static class WorkCheckpoints
    {
        // A work-budget agent searches one tree per worker process. The
        // nets' call counters are per process, so a process that searched
        // two trees at once would count the other tree's passes and the
        // checkpoints would depend on thread scheduling.
        public static void RequireOneTreePerWorker(int trees, int workers)
        {
            if (workers < 1)
            {
                throw new ArgumentException("A work budget needs search workers: the work is counted and merged at checkpoints by search workers");
            }
            if (trees != workers)
            {
                throw new ArgumentException($"A work budget needs as many trees as workers ({workers} trees for {workers} workers, not {trees}): the work counters are per process, and a process that searched two trees would count the other tree's passes, so the checkpoints would depend on thread scheduling");
            }
        }

        // Checkpoints in one budget. The settled stop (from half the budget)
        // and the open stop (at the extension) are read at these; a finer
        // spacing follows the clocked rule more closely and makes the workers
        // wait for each other more often.
        public const int PerBudget = 16;

        public static long Spacing(long budgetUnits)
        {
            return Math.Max(1, budgetUnits / PerBudget);
        }

        // A worker's own pace: it reports each time its work passes the next
        // multiple of the spacing, and once for every multiple a single
        // iteration passes (the next iteration then reports at once).
        public sealed class Clock
        {
            private readonly long m_Spacing;
            private long m_Next;

            public Clock(long spacing)
            {
                m_Spacing = spacing;
                m_Next = spacing;
            }

            public bool Reached(long units)
            {
                if (units < m_Next)
                {
                    return false;
                }
                m_Next += m_Spacing;
                return true;
            }
        }

        // The share of its budget a search has used, as the stopping rule
        // reads it: the larger of the work share and the iteration share,
        // each over every worker's budget (as the clocked rule takes the
        // larger of the time and iteration shares).
        public static double Used(long units, long budgetUnits, long iterations, long iterationBudget, int workers)
        {
            double work = units / ((double)Math.Max(1, budgetUnits) * workers);
            double iteration = iterations / ((double)Math.Max(1, iterationBudget) * workers);
            return Math.Max(work, iteration);
        }
    }
}
