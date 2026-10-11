using System.Collections.Generic;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // RootStat.BreakTie (SearchSettings.TieMargin) on synthetic root
    // statistics shaped like a Swallow decision (three targets at 0.386,
    // 0.384 and 0.383), with the search's
    // constants (SearchSettings.Material and Confidence).
    public class TieMarginTests
    {
        private const double Material = 0.01, Confidence = 2;

        private static RootStat Stat(string key, int visits, double mean, double sd, double margin, double marginSd)
        {
            return new RootStat
            {
                Key = key, Visits = visits, Reward = mean * visits, RewardSquares = visits * (sd * sd + mean * mean),
                Margin = margin * visits, MarginSquares = visits * (marginSd * marginSd + margin * margin), MarginCount = visits,
            };
        }

        private static List<RootStat> Swallow() => new List<RootStat>
        {
            Stat("tgt P1 Melee 0", 911, 0.386, 0.1, -41, 12),
            Stat("tgt P1 Ranged 0", 885, 0.384, 0.1, -41, 12),
            Stat("tgt P2 Melee 0", 864, 0.383, 0.1, -21, 12),
        };

        [Fact]
        public void ATiedTargetWithAClearlyBetterMarginIsTaken()
        {
            List<RootStat> ranked = Swallow();
            Assert.NotNull(RootStat.BreakTie(ranked, Material, Confidence));
            Assert.Equal("tgt P2 Melee 0", ranked[0].Key);
        }

        [Fact]
        public void AClearlyBetterWinningChanceIsKeptWhateverTheMargin()
        {
            List<RootStat> ranked = Swallow();
            ranked[2] = Stat("tgt P2 Melee 0", 400, 0.30, 0.1, -21, 12);
            Assert.Null(RootStat.BreakTie(ranked, Material, Confidence));
            Assert.Equal("tgt P1 Melee 0", ranked[0].Key);
        }

        [Fact]
        public void AMarginWithinNoiseKeepsTheMostVisitedStep()
        {
            List<RootStat> ranked = Swallow();
            ranked[2] = Stat("tgt P2 Melee 0", 864, 0.383, 0.1, -40, 12);
            Assert.Null(RootStat.BreakTie(ranked, Material, Confidence));
        }

        [Fact]
        public void WithoutMarginStatisticsTheMostVisitedStepStands()
        {
            List<RootStat> ranked = Swallow();
            ranked[0].MarginCount = 0;
            Assert.Null(RootStat.BreakTie(ranked, Material, Confidence));
        }

        [Fact]
        public void EveryStepLostTiesAndTheMarginDecides()
        {
            // Thirteen targets, every simulation lost.
            List<RootStat> ranked = new List<RootStat>
            {
                Stat("tgt P2 Melee 3", 745, 0, 0, -30, 8),
                Stat("tgt P2 Melee 5", 744, 0, 0, -30, 8),
                Stat("tgt P1 Ranged 1", 743, 0, 0, -19, 8),
            };
            Assert.NotNull(RootStat.BreakTie(ranked, Material, Confidence));
            Assert.Equal("tgt P1 Ranged 1", ranked[0].Key);
        }
    }
}
