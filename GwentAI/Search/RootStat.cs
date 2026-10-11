using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GwentAI.Search
{
    // A root step's statistics, summed over the trees or worker processes
    // of one search (Ismcts.Rank). Engine-free, so GwentAI.Tests checks the
    // tie rule.
    public class RootStat
    {
        public string Key;
        public int Visits;
        // Summed reward for the searching seat, and summed squares.
        public double Reward;
        public double RewardSquares;
        public double Mean => Visits == 0 ? 0 : Reward / Visits;
        // The variance of the mean.
        public double MeanVariance => Visits < 2 ? double.PositiveInfinity
            : Math.Max(0, RewardSquares / Visits - Mean * Mean) / (Visits - 1);
        // With SearchSettings.TieMargin: the searched round's score margin
        // for the searching seat, summed (and squared) over the simulations
        // that finished that round.
        public double Margin;
        public double MarginSquares;
        public int MarginCount;
        public double MeanMargin => MarginCount == 0 ? 0 : Margin / MarginCount;
        public double MeanMarginVariance => MarginCount < 2 ? double.PositiveInfinity
            : Math.Max(0, MarginSquares / MarginCount - MeanMargin * MeanMargin) / (MarginCount - 1);
        // The step's own children, ranked (SearchResult.Root only): with
        // grouped plays, the positions searched under a card.
        public List<RootStat> Children;

        // A step with fewer than this share of the most visited step's
        // visits is not tied with it: the tree already moved away from it.
        public const double TieVisitShare = 0.25;

        // Among the steps tied with the most visited one (ranked[0]): within
        // material of its mean reward or within confidence standard errors
        // of the difference, and at least TieVisitShare of its visits, the
        // one with the best mean margin, when that margin beats the most
        // visited step's by more than confidence standard errors. Moves it
        // to the front of ranked and describes the change; null when the
        // most visited step stands.
        public static string BreakTie(List<RootStat> ranked, double material, double confidence)
        {
            if (ranked.Count < 2 || ranked[0].MarginCount < 2)
            {
                return null;
            }
            RootStat best = ranked[0];
            RootStat pick = best;
            foreach (RootStat stat in ranked.Skip(1))
            {
                bool tied = stat.Visits >= TieVisitShare * best.Visits &&
                            (best.Mean - stat.Mean < material || best.Mean - stat.Mean <= confidence * Math.Sqrt(best.MeanVariance + stat.MeanVariance));
                if (tied && stat.MarginCount >= 2 && stat.MeanMargin > pick.MeanMargin &&
                    stat.MeanMargin - best.MeanMargin > confidence * Math.Sqrt(best.MeanMarginVariance + stat.MeanMarginVariance))
                {
                    pick = stat;
                }
            }
            if (pick == best)
            {
                return null;
            }
            ranked.Remove(pick);
            ranked.Insert(0, pick);
            return $"{pick.Key} {pick.Mean.ToString("F3", CultureInfo.InvariantCulture)} margin {pick.MeanMargin.ToString("F1", CultureInfo.InvariantCulture)} over " +
                   $"{best.Key} {best.Mean.ToString("F3", CultureInfo.InvariantCulture)} margin {best.MeanMargin.ToString("F1", CultureInfo.InvariantCulture)}";
        }
    }
}
