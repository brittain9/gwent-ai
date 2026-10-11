using System;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The search's generator against the faults of the engine's
    // MersenneTwisterRandom: correlated
    // successive draws and a Next(2) that repeats 13% of the time.
    public class SplitMixRandomTests
    {
        private const int Draws = 20000;

        [Theory]
        [InlineData(1)]
        [InlineData(12345)]
        [InlineData(-7)]
        public void SuccessiveDoublesAreUncorrelated(int seed)
        {
            SplitMixRandom random = new SplitMixRandom(seed);
            double[] values = new double[Draws];
            for (int index = 0; index < Draws; index++)
            {
                values[index] = random.NextDouble();
            }
            Assert.InRange(LagOneCorrelation(values), -0.02, 0.02);
            Assert.InRange(Mean(values), 0.49, 0.51);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(12345)]
        [InlineData(-7)]
        public void ABinaryDrawRepeatsHalfTheTime(int seed)
        {
            SplitMixRandom random = new SplitMixRandom(seed);
            int previous = random.Next(2);
            int repeats = 0;
            for (int index = 1; index < Draws; index++)
            {
                int next = random.Next(2);
                repeats += next == previous ? 1 : 0;
                previous = next;
            }
            Assert.InRange(repeats / (double)(Draws - 1), 0.47, 0.53);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(10)]
        [InlineData(1000)]
        public void BoundedDrawsCoverTheRangeEvenly(int bound)
        {
            SplitMixRandom random = new SplitMixRandom(99);
            int[] counts = new int[bound];
            int draws = bound * 5000;
            for (int index = 0; index < draws; index++)
            {
                counts[random.Next(bound)]++;
            }
            // Five thousand expected per value; the 5-sigma band is +-7%.
            Assert.All(counts, count => Assert.InRange(count, 4650, 5350));
        }

        [Fact]
        public void BoundsAreHonored()
        {
            SplitMixRandom random = new SplitMixRandom(5);
            bool[] seen = new bool[10];
            for (int index = 0; index < Draws; index++)
            {
                int value = random.Next(-5, 5);
                Assert.InRange(value, -5, 4);
                seen[value + 5] = true;
                Assert.InRange(random.Next(), 0, int.MaxValue - 1);
                Assert.InRange(random.Next(int.MinValue, int.MaxValue), int.MinValue, int.MaxValue - 1);
                Assert.InRange(random.NextDouble(), 0.0, 0.9999999999999999);
            }
            Assert.All(seen, Assert.True);
            Assert.Equal(0, random.Next(0));
            Assert.Equal(7, random.Next(7, 7));
            Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(2, 1));
        }

        [Fact]
        public void NextBytesFillsTheWholeBuffer()
        {
            byte[] bytes = new byte[1001];
            new SplitMixRandom(3).NextBytes(bytes);
            Assert.InRange(Array.FindAll(bytes, value => value != 0).Length, 950, 1001);
        }

        [Fact]
        public void AdjacentSeedsGiveUnrelatedStreams()
        {
            // SplitMix64 streams from adjacent seeds are one another shifted
            // by one draw. The search seeds are adjacent.
            SplitMixRandom first = new SplitMixRandom(1);
            SplitMixRandom second = new SplitMixRandom(2);
            double[] a = new double[Draws];
            double[] b = new double[Draws];
            for (int index = 0; index < Draws; index++)
            {
                a[index] = first.NextDouble();
                b[index] = second.NextDouble();
            }
            for (int shift = 0; shift <= 2; shift++)
            {
                int matches = 0;
                for (int index = 0; index + shift < Draws; index++)
                {
                    matches += a[index + shift] == b[index] || a[index] == b[index + shift] ? 1 : 0;
                }
                Assert.Equal(0, matches);
            }
        }

        [Fact]
        public void ASeedGivesTheSameStream()
        {
            SplitMixRandom first = new SplitMixRandom(42);
            SplitMixRandom second = new SplitMixRandom(42);
            for (int index = 0; index < 100; index++)
            {
                Assert.Equal(first.Next(1000), second.Next(1000));
            }
        }

        private static double Mean(double[] values)
        {
            double sum = 0;
            foreach (double value in values)
            {
                sum += value;
            }
            return sum / values.Length;
        }

        private static double LagOneCorrelation(double[] values)
        {
            double mean = Mean(values);
            double covariance = 0;
            double variance = 0;
            for (int index = 0; index < values.Length; index++)
            {
                variance += (values[index] - mean) * (values[index] - mean);
                if (index > 0)
                {
                    covariance += (values[index] - mean) * (values[index - 1] - mean);
                }
            }
            return covariance / variance;
        }
    }
}
