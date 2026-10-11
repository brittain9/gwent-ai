using System;
using System.Collections.Generic;
using System.Diagnostics;
using GwentGameplay;

namespace GwentAI.Search
{
    // A search that can run again from the same position, restricted at the
    // root to some steps and with every simulation played to the end of the
    // match (no evaluator, no cutoff). This process's Ismcts and the worker
    // coordinator (GwentAI.Practice.WorkerSearch) are such searches.
    public interface IWholeMatchSearch
    {
        // iterations: the simulations of the whole search, summed over its
        // trees or workers. timeBudgetMs: its clock, 0 for none. Null, with
        // the reason, when the search cannot run now (no worker reached the
        // position); the caller then keeps the main search's choice.
        SearchResult SearchWholeMatch(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed,
                                      int iterations, int timeBudgetMs, ICollection<string> rootKeys, out string unavailable);
    }

    // The second look (SearchSettings.SecondLookGap) over a search's
    // root choice: at a play decision whose two most visited steps have mean
    // values within the gap, a second search from the same position,
    // restricted to those two steps and played to the end of the match,
    // chooses between them by its mean values. The main search scores
    // simulations at the end of the searched round with the evaluator; where
    // two steps are that close, the evaluator's error can exceed their gap,
    // and the whole match is the evidence that does not depend on it. Settled
    // decisions keep the main search's choice.
    //
    // It wraps the whole search (this process's Ismcts, or the worker
    // coordinator after its merge) and the economy tie-break inside it, so
    // it decides on the summed roots of every tree and worker, the fairness
    // re-check sees the same rule, and the long horizon has the last word
    // over the economy tie-break. The second search reads only what the seat sees.
    public class SecondLook : ISearch
    {
        // Mixed into the decision's seed, so the second search draws other
        // worlds than the main one.
        private const int SeedMix = 0x51D0C0DE;

        private readonly ISearch m_Search;
        private readonly IWholeMatchSearch m_Look;
        private readonly SearchSettings m_Settings;

        // search: the main search with its tie-breaks; look: the search that
        // runs the second one.
        public SecondLook(ISearch search, IWholeMatchSearch look, SearchSettings settings)
        {
            m_Search = search;
            m_Look = look;
            m_Settings = settings;
        }

        public IDictionary<ushort, int> Remembered
        {
            get => m_Search.Remembered;
            set => m_Search.Remembered = value;
        }

        public SearchResult Search(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations = 0)
        {
            Stopwatch clock = Stopwatch.StartNew();
            SearchResult result = m_Search.Search(live, observer, request, prefix, seed, iterations);
            if (!(request is RequestPlayCardAction) || prefix.Count > 0)
            {
                return result;
            }
            SecondLookRecord plan = SecondLookChoice.Plan(result.Root, m_Settings.SecondLookGap);
            if (plan == null)
            {
                return result;
            }
            result.SecondLook = plan;
            int timeBudgetMs = 0;
            if (m_Settings.TimeBudgetMs > 0)
            {
                // The clock the main search had at most (its budget, extended
                // for open decisions) is the decision's clock; the second
                // look takes what the main search left of it.
                long left = (long)(m_Settings.TimeBudgetMs * Math.Max(1, m_Settings.Extend)) - clock.ElapsedMilliseconds;
                if (left <= 0)
                {
                    plan.Skipped = $"no clock left: the main search took {clock.ElapsedMilliseconds} ms";
                    plan.Chosen = result.Action.Key;
                    return result;
                }
                timeBudgetMs = (int)left;
            }
            Stopwatch look = Stopwatch.StartNew();
            SearchResult second = m_Look.SearchWholeMatch(live, observer, request, prefix, unchecked(seed ^ SeedMix), m_Settings.SecondLookIterations,
                                                          timeBudgetMs, plan.Keys, out string unavailable);
            plan.Ms = look.Elapsed.TotalMilliseconds;
            if (second == null)
            {
                plan.Skipped = unavailable;
                plan.Chosen = result.Action.Key;
                return result;
            }
            plan.Iterations = second.Iterations;
            string chosen = SecondLookChoice.Choose(plan, second.Root, result.Action.Key);
            if (chosen != result.Action.Key)
            {
                Player seat = live.PlayerManager.GetPlayer(observer);
                List<GameAction> legal = Actions.Legal(request, seat, new List<string>(), m_Settings.GroupPlays);
                Actions.RemoveEarlyPass(legal, seat, m_Settings);
                RootChoice.Promote(result, result.Root.FindIndex(stat => stat.Key == chosen), request, seat, m_Settings, legal);
            }
            return result;
        }
    }
}
