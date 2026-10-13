using System;

namespace GwentAI.Search
{
    // The draw of the opponent policy sample (SearchSettings.OpponentPolicy),
    // free of the engine: one index in proportion to the weights.
    public static class PriorSample
    {
        // The index of a step drawn with chance weights[i] / sum over the
        // first `count` weights; the array may be longer (PolicyNet reuses
        // its buffers, so the entries past the count are stale). One draw
        // from the generator. Weights that sum to nothing (or to a number
        // that is not finite) draw uniformly, as a prior with no preference.
        public static int Draw(double[] weights, int count, Random random)
        {
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                total += weights[i];
            }
            if (!(total > 0) || double.IsInfinity(total))
            {
                return random.Next(count);
            }
            double draw = random.NextDouble() * total;
            for (int i = 0; i < count; i++)
            {
                draw -= weights[i];
                if (draw < 0)
                {
                    return i;
                }
            }
            return count - 1;
        }
    }
}
