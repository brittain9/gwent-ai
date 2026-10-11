using System.Collections.Generic;

namespace GwentAI.Search
{
    // The final-step rule of the lower confidence bound choice, free of the engine. Among the root
    // steps with at least VisitShare of the most visited step's visits, the
    // one with the highest mean minus Confidence standard errors (the
    // standard error of the mean from the summed rewards and squares,
    // RootStat.MeanVariance). A step with one visit has no error estimate and
    // is never chosen over the most visited one.
    public static class LowerBoundRule
    {
        public const double VisitShare = 0.10;
        public const double Confidence = 1.0;

        // The index of the step to play among root steps ranked most visited
        // first; ties keep the lower index.
        public static int Choose(IList<RootStat> ranked)
        {
            int chosen = 0;
            double bestBound = double.NegativeInfinity;
            for (int i = 0; i < ranked.Count; i++)
            {
                RootStat stat = ranked[i];
                if (i > 0 && stat.Visits < VisitShare * ranked[0].Visits)
                {
                    continue;
                }
                double bound = stat.Mean - Confidence * System.Math.Sqrt(stat.MeanVariance);
                if (bound > bestBound)
                {
                    bestBound = bound;
                    chosen = i;
                }
            }
            return chosen;
        }
    }
}
