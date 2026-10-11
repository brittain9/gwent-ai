using System;
using System.Collections.Generic;
using System.Linq;

namespace GwentAI.Search
{
    // What the second look (SecondLook) compared at one decision and
    // what it found: the record of the decision's step (DecisionStat).
    public class SecondLookRecord
    {
        // The two most visited root steps of the main search, and their mean
        // values there (round horizon, scored by the evaluator).
        public string[] Keys;
        public double[] Main;
        // The same two steps in the second search (whole match, no cutoff):
        // mean values and visits; null when the second search did not decide.
        public double[] Look;
        public int[] LookVisits;
        // Simulations of the second search, and its wall time in ms.
        public int Iterations;
        public double Ms;
        // The step the second look chose, and whether that differs from the
        // step the main search (with its tie-breaks) had chosen.
        public string Chosen;
        public bool Changed;
        // Why the second look did not decide, if it did not: no clock left,
        // no workers, a step the second search never reached.
        public string Skipped;
    }

    // The pure decision of the second look, on ranked root statistics
    // (engine-free, so GwentAI.Tests checks it).
    public static class SecondLookChoice
    {
        // The two most visited steps of the main search when their mean
        // values are within gap of each other; null when the root has fewer
        // than two steps or the gap is wider. The most visited, not the first
        // of the list: the economy tie-break may have moved another step first.
        public static SecondLookRecord Plan(List<RootStat> root, double gap)
        {
            if (gap < 0 || root.Count < 2)
            {
                return null;
            }
            List<RootStat> top = root.OrderByDescending(stat => stat.Visits).ThenByDescending(stat => stat.Mean)
                .ThenBy(stat => stat.Key, StringComparer.Ordinal).Take(2).ToList();
            if (Math.Abs(top[0].Mean - top[1].Mean) > gap)
            {
                return null;
            }
            return new SecondLookRecord
            {
                Keys = top.Select(stat => stat.Key).ToArray(),
                Main = top.Select(stat => stat.Mean).ToArray(),
            };
        }

        // The step with the higher mean value in the second search; the
        // record gets the second search's values. When the second search did
        // not reach both steps, the main search's choice (current) stands and
        // the record says so. A tie goes to the more visited step of the main
        // search (the first key of the record).
        public static string Choose(SecondLookRecord plan, List<RootStat> look, string current)
        {
            RootStat[] found = plan.Keys.Select(key => look.Find(stat => stat.Key == key)).ToArray();
            if (found.Any(stat => stat == null || stat.Visits == 0))
            {
                plan.Skipped = "the second search did not reach " +
                               string.Join(" and ", plan.Keys.Where((key, i) => found[i] == null || found[i].Visits == 0).ToArray());
                plan.Chosen = current;
                return current;
            }
            plan.Look = found.Select(stat => stat.Mean).ToArray();
            plan.LookVisits = found.Select(stat => stat.Visits).ToArray();
            plan.Chosen = plan.Look[1] > plan.Look[0] ? plan.Keys[1] : plan.Keys[0];
            plan.Changed = plan.Chosen != current;
            return plan.Chosen;
        }
    }
}
