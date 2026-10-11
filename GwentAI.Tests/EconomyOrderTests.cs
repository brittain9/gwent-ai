using System.Collections.Generic;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The economy tie-break's rule (SearchSettings.EconomyTieBreak) on synthetic root statistics.
    public class EconomyOrderTests
    {
        private static TieCandidate Step(EconomyStep step, int visits, double mean)
        {
            return new TieCandidate { Step = step, Visits = visits, Mean = mean };
        }

        private static int Choose(EconomyRound round, double gap, params TieCandidate[] ranked)
        {
            return EconomyOrder.Choose(new List<TieCandidate>(ranked), round, gap);
        }

        [Fact]
        public void AfterWinningRoundOneATiedPassBeatsACard()
        {
            // Clan Corsair 0.519 against pass 0.514 in round 2.
            Assert.Equal(1, Choose(EconomyRound.SecondAfterWin, 0.01, Step(EconomyStep.Bronze, 900, 0.519), Step(EconomyStep.Pass, 800, 0.514)));
        }

        [Fact]
        public void AStepOutsideTheGapIsNotTied()
        {
            Assert.Equal(0, Choose(EconomyRound.SecondAfterWin, 0.01, Step(EconomyStep.Bronze, 900, 0.53), Step(EconomyStep.Pass, 800, 0.514)));
            // At the Practice budget: Clan Corsair 0.506 against pass 0.484.
            Assert.Equal(0, Choose(EconomyRound.SecondAfterWin, 0.02, Step(EconomyStep.Bronze, 10000, 0.506), Step(EconomyStep.Pass, 4800, 0.484)));
            Assert.Equal(1, Choose(EconomyRound.SecondAfterWin, 0.03, Step(EconomyStep.Bronze, 10000, 0.506), Step(EconomyStep.Pass, 4800, 0.484)));
        }

        [Fact]
        public void ARarelyVisitedStepIsNotTied()
        {
            Assert.Equal(0, Choose(EconomyRound.SecondAfterWin, 0.05, Step(EconomyStep.Bronze, 1000, 0.52), Step(EconomyStep.Pass, 249, 0.53)));
            Assert.Equal(1, Choose(EconomyRound.SecondAfterWin, 0.05, Step(EconomyStep.Bronze, 1000, 0.52), Step(EconomyStep.Pass, 250, 0.53)));
        }

        [Fact]
        public void RoundOneKeepsTheLeaderAndGoldsForLater()
        {
            // The leader on turn 1 at 0.577 against a bronze at 0.570.
            Assert.Equal(2, Choose(EconomyRound.First, 0.01,
                Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Gold, 1500, 0.575), Step(EconomyStep.Bronze, 1400, 0.570)));
            Assert.Equal(1, Choose(EconomyRound.First, 0.01, Step(EconomyStep.Gold, 2000, 0.577), Step(EconomyStep.Silver, 1500, 0.575)));
        }

        [Fact]
        public void EquallyCheapStepsKeepTheMostVisited()
        {
            Assert.Equal(1, Choose(EconomyRound.First, 0.01,
                Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Bronze, 1500, 0.575), Step(EconomyStep.Bronze, 1400, 0.576)));
        }

        [Fact]
        public void RoundOnePassAtEvenCardsIsNeitherPlayedNorReplaced()
        {
            // No new concession at even cards, and no change to an existing one.
            Assert.Equal(0, Choose(EconomyRound.First, 0.01, Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Pass, 1500, 0.576)));
            Assert.Equal(2, Choose(EconomyRound.First, 0.01,
                Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Pass, 1500, 0.576), Step(EconomyStep.Bronze, 1400, 0.575)));
            Assert.Equal(0, Choose(EconomyRound.First, 0.01, Step(EconomyStep.Pass, 2000, 0.577), Step(EconomyStep.Bronze, 1500, 0.576)));
        }

        [Fact]
        public void RoundOnePassWithMoreCardsKeepsTheCardAdvantage()
        {
            // The leader at 7-6 cards, pass tied.
            Assert.Equal(1, Choose(EconomyRound.FirstWithMoreCards, 0.01, Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Pass, 1500, 0.576)));
        }

        [Fact]
        public void ADrawTwoSpyBeatsABronzeInATieAndLosesToAPass()
        {
            // The spy nets a card, so it ranks between a pass and a bronze.
            Assert.Equal(1, Choose(EconomyRound.First, 0.03, Step(EconomyStep.Bronze, 900, 0.52), Step(EconomyStep.Spy, 600, 0.51)));
            Assert.Equal(0, Choose(EconomyRound.First, 0.03, Step(EconomyStep.Spy, 900, 0.52), Step(EconomyStep.Bronze, 600, 0.52)));
            Assert.Equal(1, Choose(EconomyRound.SecondAfterWin, 0.03, Step(EconomyStep.Spy, 900, 0.52), Step(EconomyStep.Pass, 600, 0.51)));
            // Outside the gap or the visit share the spy is not tied.
            Assert.Equal(0, Choose(EconomyRound.First, 0.03, Step(EconomyStep.Bronze, 900, 0.56), Step(EconomyStep.Spy, 600, 0.51)));
            Assert.Equal(0, Choose(EconomyRound.First, 0.03, Step(EconomyStep.Bronze, 1000, 0.52), Step(EconomyStep.Spy, 200, 0.52)));
            // Other rounds keep the search choice.
            Assert.Equal(0, Choose(EconomyRound.None, 0.03, Step(EconomyStep.Silver, 900, 0.52), Step(EconomyStep.Spy, 600, 0.52)));
        }

        [Fact]
        public void TheFiveDrawTwoSpiesAreTheSpiesThatDraw()
        {
            foreach (int id in new[] { 122203, 132204, 142203, 152214, 162210 })
            {
                Assert.True(EconomyOrder.IsDrawTwoSpy(id), id.ToString());
            }
            // Dijkstra tutors, Letho removes, a plain bronze is not a spy.
            foreach (int id in new[] { 122105, 162101, 162205, 112101 })
            {
                Assert.False(EconomyOrder.IsDrawTwoSpy(id), id.ToString());
            }
        }

        [Fact]
        public void OtherRoundsKeepTheSearchChoice()
        {
            Assert.Equal(0, Choose(EconomyRound.None, 0.05, Step(EconomyStep.Leader, 2000, 0.577), Step(EconomyStep.Pass, 1500, 0.576)));
        }
    }
}
