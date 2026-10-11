using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GwentAI.Learn;
using GwentGameplay;

namespace GwentAI.Search
{
    // The linear Evaluator and a learned value net, mixed in logit space:
    // logit p = (1 - NetWeight) logit p_linear + NetWeight logit p_net.
    // The net reads more of the position; the linear form keeps its
    // judgment of the trade between rounds and cards where the search
    // leaves the positions the net was trained on.
    public class BlendEvaluator : IRoundEvaluator
    {
        private readonly IRoundEvaluator m_Linear;
        private readonly IRoundEvaluator m_Net;
        private readonly double m_NetWeight;
        // Optional: writes leaf values to a file, for a sample of the leaves
        // the search asks about: both values and the seat's encoding (one
        // JSON line each). Its own random generator: sampling never touches
        // the game's. Off unless Dump is set.
        public TextWriter Dump;
        public double DumpRate;
        private readonly Random m_DumpRandom = new Random(1);
        private readonly EncodedState m_DumpState = new EncodedState();

        public IRoundEvaluator Linear => m_Linear;
        public IRoundEvaluator Net => m_Net;
        public double NetWeight => m_NetWeight;

        public BlendEvaluator(IRoundEvaluator linear, IRoundEvaluator net, double netWeight)
        {
            if (netWeight < 0 || netWeight > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(netWeight), "a weight in [0, 1]");
            }
            m_Linear = linear;
            m_Net = net;
            m_NetWeight = netWeight;
        }

        public double WinProbability(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered)
        {
            double linear = m_Linear.WinProbability(game, seat, remembered);
            // A finished match: both give the result.
            if (IsOver(game, seat))
            {
                return linear;
            }
            double net = m_Net.WinProbability(game, seat, remembered);
            if (Dump != null && m_DumpRandom.NextDouble() < DumpRate)
            {
                // A full-state net's leaves are written in its own view.
                bool full = m_Net is ValueNet valueNet && valueNet.FullState;
                StateEncoder.Encode(game, seat, remembered, true, m_DumpState, full);
                StringBuilder text = new StringBuilder(full ? "{\"full\":true,\"lin\":" : "{\"lin\":");
                text.Append(linear.ToString("R", CultureInfo.InvariantCulture)).Append(",\"net\":");
                text.Append(net.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                m_DumpState.WriteJson(text);
                Dump.WriteLine(text.Append('}').ToString());
            }
            return Mix(linear, net, m_NetWeight);
        }

        public static double Mix(double linear, double net, double netWeight)
        {
            double z = (1 - netWeight) * Logit(linear) + netWeight * Logit(net);
            return 1 / (1 + Math.Exp(-z));
        }

        private static double Logit(double p)
        {
            const double Floor = 1e-6;
            p = Math.Min(Math.Max(p, Floor), 1 - Floor);
            return Math.Log(p / (1 - p));
        }

        private static bool IsOver(GameController game, EPlayerId seat)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            return player.Crowns >= 2 || player.OpponentPlayer.Crowns >= 2;
        }
    }
}
