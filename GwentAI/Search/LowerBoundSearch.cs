using System;
using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Search
{
    // The final-step choice by lower confidence bound (SearchSettings.
    // LowerBoundChoice, LowerBoundRule) over a search's root: at the
    // first step of a play decision, the rule's step replaces the most
    // visited one. It wraps the whole search like EconomyTieBreak, so it
    // ranks the summed statistics of every tree and worker, and the economy
    // tie-break then runs on the new first step.
    public class LowerBoundSearch : ISearch
    {
        private readonly ISearch m_Search;
        private readonly SearchSettings m_Settings;

        public LowerBoundSearch(ISearch search, SearchSettings settings)
        {
            m_Search = search;
            m_Settings = settings;
        }

        public IDictionary<ushort, int> Remembered
        {
            get => m_Search.Remembered;
            set => m_Search.Remembered = value;
        }

        public SearchResult Search(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations = 0)
        {
            SearchResult result = m_Search.Search(live, observer, request, prefix, seed, iterations);
            if (!(request is RequestPlayCardAction) || prefix.Count > 0 || result.Root.Count < 2)
            {
                return result;
            }
            int index = LowerBoundRule.Choose(result.Root);
            if (index == 0)
            {
                return result;
            }
            Player seat = live.PlayerManager.GetPlayer(observer);
            List<GameAction> legal = Actions.Legal(request, seat, new List<string>(), m_Settings.GroupPlays);
            Actions.RemoveEarlyPass(legal, seat, m_Settings);
            RootStat first = result.Root[0];
            RootChoice.Promote(result, index, request, seat, m_Settings, legal);
            RootStat chosen = result.Root[0];
            result.TieBreak = $"lcb: {first.Key} {first.Visits} {Mean(first)} -> {chosen.Key} {chosen.Visits} {Mean(chosen)}";
            return result;
        }

        private static string Mean(RootStat stat)
        {
            return $"{Math.Round((decimal)stat.Mean, 3, MidpointRounding.AwayFromZero):F3}";
        }
    }
}
