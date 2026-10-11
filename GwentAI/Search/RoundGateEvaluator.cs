using System;
using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Search
{
    // The round evaluator scores leaf cuts before round FromRound and a
    // value net scores the cuts into round FromRound or later. A net
    // trained on round starts of one round is read only there; earlier cuts
    // keep the logistic evaluator.
    // The round index decides, not the crowns: a drawn round 1 gives 1-1 at
    // a round-2 start.
    public class RoundGateEvaluator : IRoundEvaluator
    {
        private readonly IRoundEvaluator m_Linear;
        private readonly IRoundEvaluator m_Net;
        private readonly int m_FromRound;

        public IRoundEvaluator Linear => m_Linear;
        public IRoundEvaluator Net => m_Net;
        public int FromRound => m_FromRound;

        public RoundGateEvaluator(IRoundEvaluator linear, IRoundEvaluator net, int fromRound)
        {
            if (fromRound < RoundGate.FirstRound || fromRound > RoundGate.LastRound)
            {
                throw new ArgumentOutOfRangeException(nameof(fromRound), $"a round from {RoundGate.FirstRound} to {RoundGate.LastRound}");
            }
            m_Linear = linear;
            m_Net = net;
            m_FromRound = fromRound;
        }

        public double WinProbability(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            bool net = RoundGate.UsesNet(game.RoundManager.CurrentRoundIndex, m_FromRound, player.Crowns, player.OpponentPlayer.Crowns);
            return (net ? m_Net : m_Linear).WinProbability(game, seat, remembered);
        }
    }
}
