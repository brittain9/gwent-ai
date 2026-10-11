using System;
using System.Collections.Generic;
using System.Linq;
using GwentGameplay;

namespace GwentAI.Search
{
    // The economy tie-break (SearchSettings.EconomyTieBreak) over a
    // search's root choice: at the first step of a play decision, a tied
    // root step that spends less (EconomyOrder) replaces the most visited
    // one. It wraps the whole search (this process's Ismcts or the worker
    // coordinator), so the root statistics it ranks are the summed ones of
    // every tree and worker, and the fairness re-check sees the same rule.
    // It reads only what the seat sees: its own hand, both crowns, hand
    // counts and the opponent's pass.
    public class EconomyTieBreak : ISearch
    {
        private readonly ISearch m_Search;
        private readonly SearchSettings m_Settings;

        public EconomyTieBreak(ISearch search, SearchSettings settings)
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
            Player seat = live.PlayerManager.GetPlayer(observer);
            EconomyRound round = Round(seat);
            if (!(request is RequestPlayCardAction) || prefix.Count > 0 || result.Root.Count < 2 || round == EconomyRound.None)
            {
                return result;
            }
            List<GameAction> legal = Actions.Legal(request, seat, new List<string>(), m_Settings.GroupPlays);
            Actions.RemoveEarlyPass(legal, seat, m_Settings);
            List<TieCandidate> candidates = result.Root.Select(stat => new TieCandidate
            {
                Visits = stat.Visits, Mean = stat.Mean, Step = Step(RootChoice.Legal(legal, stat.Key), m_Settings.SpyEconomy),
            }).ToList();
            int index = EconomyOrder.Choose(candidates, round, m_Settings.EconomyTieBreak);
            if (index == 0)
            {
                return result;
            }
            RootStat first = result.Root[0];
            RootChoice.Promote(result, index, request, seat, m_Settings, legal);
            RootStat chosen = result.Root[0];
            result.TieBreak = $"{round}: {first.Key} {first.Visits} {Mean(first)} -> {chosen.Key} {chosen.Visits} {Mean(chosen)}";
            return result;
        }

        public static EconomyRound Round(Player seat)
        {
            Player opponent = seat.OpponentPlayer;
            switch (seat.CurrentGameController.RoundManager.CurrentRoundIndex)
            {
                case 0:
                    return seat.GetCardsInLocation(ELocation.Hand).Count > opponent.GetCardsInLocation(ELocation.Hand).Count
                        ? EconomyRound.FirstWithMoreCards : EconomyRound.First;
                case 1:
                    return seat.Crowns == 1 && opponent.Crowns == 0 && !opponent.HasPassed ? EconomyRound.SecondAfterWin : EconomyRound.None;
                default:
                    return EconomyRound.None;
            }
        }

        // As FairAgent prints root means: the same text on Mono and .NET.
        private static string Mean(RootStat stat)
        {
            return $"{Math.Round((decimal)stat.Mean, 3, MidpointRounding.AwayFromZero):F3}";
        }

        private static EconomyStep Step(GameAction action, bool spyAware)
        {
            if (action.Kind == OptionKind.Pass)
            {
                return EconomyStep.Pass;
            }
            if (action.Card.Position.Location == ELocation.Leader)
            {
                return EconomyStep.Leader;
            }
            if (spyAware && EconomyOrder.IsDrawTwoSpy(action.Card.Template.Id))
            {
                return EconomyStep.Spy;
            }
            switch (action.Card.Template.Tier)
            {
                case ECardTier.Gold:
                    return EconomyStep.Gold;
                case ECardTier.Silver:
                    return EconomyStep.Silver;
                default:
                    return EconomyStep.Bronze;
            }
        }
    }
}
