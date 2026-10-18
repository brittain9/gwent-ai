using System;
using System.Collections.Generic;
using System.Linq;

namespace GwentAI.Practice
{
    // An arena draft for the AI's seat, from plain card pools (no client
    // types, so the tests compile it alone).
    //
    // The rule is the local server's draft for the human's run
    // (server/deploy/arena.py draft_offers, server/docs/arena.md), so both
    // seats draft alike: 26 card steps whose tiers are a shuffled schedule
    // of 9 gold, 8 silver and 9 bronze; each step offers 4 cards of its
    // tier; the offered cards are drawn without replacement over the whole
    // draft, from every faction and neutral, so the deck has no repeats.
    // Then a leader, one of 4 offered. The AI takes a random card of each
    // offer: the draft is the deck's source, not a decision the search
    // makes. The arena belief guesses the same shape (Belief.ArenaDeck).
    public static class ArenaDraft
    {
        public const int Golds = 9;
        public const int Silvers = 8;
        public const int Bronzes = 9;
        public const int Picks = Golds + Silvers + Bronzes;
        public const int Choices = 4;
        public const int LeaderChoices = 4;

        public enum Tier
        {
            Gold,
            Silver,
            Bronze,
        }

        public class Result
        {
            public int Leader;
            // Template ids in pick order.
            public List<int> Cards = new List<int>();
            public List<Tier> Tiers = new List<Tier>();
        }

        // Pools: collectible template ids of each tier and the leaders.
        public static Result Draft(IList<int> golds, IList<int> silvers, IList<int> bronzes, IList<int> leaders, Random random)
        {
            if (golds.Count == 0 || silvers.Count == 0 || bronzes.Count == 0 || leaders.Count == 0)
            {
                throw new ArgumentException("an arena draft needs cards of every tier and a leader");
            }
            List<Tier> schedule = Enumerable.Repeat(Tier.Gold, Golds)
                .Concat(Enumerable.Repeat(Tier.Silver, Silvers))
                .Concat(Enumerable.Repeat(Tier.Bronze, Bronzes))
                .ToList();
            Shuffle(schedule, random);
            Dictionary<Tier, List<int>> open = new Dictionary<Tier, List<int>>
            {
                [Tier.Gold] = golds.Distinct().ToList(),
                [Tier.Silver] = silvers.Distinct().ToList(),
                [Tier.Bronze] = bronzes.Distinct().ToList(),
            };
            Result result = new Result();
            foreach (Tier tier in schedule)
            {
                List<int> offered = Offer(open[tier], Choices, random);
                if (offered.Count == 0)
                {
                    throw new ArgumentException($"the {tier} pool ran out in an arena draft");
                }
                result.Cards.Add(offered[random.Next(offered.Count)]);
                result.Tiers.Add(tier);
            }
            List<int> leaderOffer = Offer(leaders.Distinct().ToList(), LeaderChoices, random);
            result.Leader = leaderOffer[random.Next(leaderOffer.Count)];
            return result;
        }

        // Up to `size` cards drawn at random and taken out of `open`.
        private static List<int> Offer(List<int> open, int size, Random random)
        {
            List<int> offered = new List<int>();
            while (offered.Count < size && open.Count > 0)
            {
                int index = random.Next(open.Count);
                offered.Add(open[index]);
                open.RemoveAt(index);
            }
            return offered;
        }

        private static void Shuffle<T>(List<T> items, Random random)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T swap = items[i];
                items[i] = items[j];
                items[j] = swap;
            }
        }
    }
}
