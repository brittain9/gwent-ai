using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The opponent model's draw (SearchSettings.OpponentPolicy) on synthetic weights.
    public class PriorSampleTests
    {
        [Fact]
        public void StepsAreDrawnInProportionToTheirWeights()
        {
            double[] weights = { 0.1, 0.6, 0.3 };
            SplitMixRandom random = new SplitMixRandom(7);
            int[] counts = new int[3];
            const int draws = 20000;
            for (int i = 0; i < draws; i++)
            {
                counts[PriorSample.Draw(weights, weights.Length, random)]++;
            }
            for (int i = 0; i < weights.Length; i++)
            {
                Assert.InRange((double)counts[i] / draws, weights[i] - 0.02, weights[i] + 0.02);
            }
        }

        [Fact]
        public void AStepWithNoWeightIsNeverDrawn()
        {
            double[] weights = { 0, 0.5, 0, 0.5 };
            SplitMixRandom random = new SplitMixRandom(3);
            for (int i = 0; i < 2000; i++)
            {
                Assert.NotEqual(0, PriorSample.Draw(weights, weights.Length, random) % 2);
            }
        }

        [Fact]
        public void WeightsThatDoNotNormaliseDrawUniformly()
        {
            double[] weights = { 0, 0, 0, 0 };
            SplitMixRandom random = new SplitMixRandom(11);
            int[] counts = new int[4];
            for (int i = 0; i < 4000; i++)
            {
                counts[PriorSample.Draw(weights, weights.Length, random)]++;
            }
            Assert.All(counts, count => Assert.InRange(count, 800, 1200));
        }

        [Fact]
        public void AWeightSumNotRelativeToOneStillNormalises()
        {
            // A node's stored means need not sum to one.
            double[] weights = { 2, 6 };
            SplitMixRandom random = new SplitMixRandom(5);
            int second = 0;
            for (int i = 0; i < 8000; i++)
            {
                second += PriorSample.Draw(weights, weights.Length, random);
            }
            Assert.InRange(second / 8000.0, 0.73, 0.77);
        }

        [Fact]
        public void EntriesPastTheCountAreNeverDrawn()
        {
            // PolicyNet.Probabilities returns its whole reused buffer: the
            // entries past the option count are stale from a longer request.
            double[] weights = { 0.4, 0.6, 0.9, 0.9, 0.9, 0.9 };
            SplitMixRandom random = new SplitMixRandom(13);
            int[] counts = new int[2];
            const int draws = 10000;
            for (int i = 0; i < draws; i++)
            {
                counts[PriorSample.Draw(weights, 2, random)]++;
            }
            Assert.InRange((double)counts[0] / draws, 0.38, 0.42);

            double[] none = { 0, 0, 5, 5 };
            for (int i = 0; i < 2000; i++)
            {
                Assert.InRange(PriorSample.Draw(none, 2, random), 0, 1);
            }
        }
    }
}
