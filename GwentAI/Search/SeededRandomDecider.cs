using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Search
{
    // The `rand-sm` agent: seeded choices that depend only on the seed, the
    // seat, the decision count and the legal list. Each step is
    // legal[SplitMix64.Pick(seed, seat, decision, n)] over Actions.Legal with
    // grouped plays off, in list order, where decision counts this seat's
    // steps from 0 (every step of a multi-step answer and every mulligan
    // re-ask is one). Played through SimPlayer, which answers at delivery as
    // RandomPlayer does.
    public sealed class SeededRandomDecider : IDecider
    {
        private readonly int m_Seed;
        private readonly int m_Seat;
        private int m_Decisions;

        public SeededRandomDecider(int seed, EPlayerId seat)
        {
            m_Seed = seed;
            m_Seat = seat == EPlayerId.P1 ? 0 : 1;
        }

        public GameAction Decide(Player player, ARequestAction request, ICollection<string> taken)
        {
            List<GameAction> legal = Actions.Legal(request, player, taken);
            if (legal.Count == 0)
            {
                return null;
            }
            return legal[SplitMix64.Pick(m_Seed, m_Seat, m_Decisions++, legal.Count)];
        }
    }
}
