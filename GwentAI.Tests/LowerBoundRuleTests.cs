using System.Collections.Generic;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The final-step rule of LowerBoundRule (a lower confidence bound) on synthetic root statistics.
    public class LowerBoundRuleTests
    {
        // Rewards in {0,1}: a mean m over n visits has summed squares m * n.
        private static RootStat Step(string key, int visits, double mean)
        {
            return new RootStat { Key = key, Visits = visits, Reward = mean * visits, RewardSquares = mean * visits };
        }

        private static int Choose(params RootStat[] ranked)
        {
            return LowerBoundRule.Choose(new List<RootStat>(ranked));
        }

        [Fact]
        public void AWellVisitedStepWithATighterBoundBeatsTheMostVisited()
        {
            // a: se 0.016, bound 0.484. b: se 0.040, bound 0.520.
            Assert.Equal(1, Choose(Step("a", 1000, 0.50), Step("b", 150, 0.56)));
        }

        [Fact]
        public void ALooseHigherMeanDoesNotWin()
        {
            // b has the higher mean but se 0.048: bound 0.472 < a's 0.484.
            Assert.Equal(0, Choose(Step("a", 1000, 0.50), Step("b", 110, 0.52)));
        }

        [Fact]
        public void AStepBelowTheVisitShareIsNeverChosen()
        {
            Assert.Equal(0, Choose(Step("a", 1000, 0.40), Step("b", 99, 0.90)));
            Assert.Equal(1, Choose(Step("a", 1000, 0.40), Step("b", 100, 0.90)));
        }

        [Fact]
        public void ASingleVisitHasNoErrorEstimateAndLosesToTheFirst()
        {
            Assert.Equal(0, Choose(Step("a", 5, 0.2), Step("b", 1, 1.0)));
        }

        [Fact]
        public void EqualBoundsKeepTheMostVisited()
        {
            Assert.Equal(0, Choose(Step("a", 1000, 0.5), Step("b", 1000, 0.5)));
        }
    }
}
