using System.Collections.Generic;
using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // The second look's decision (SecondLookChoice) on synthetic root
    // statistics shaped like near-ties: an attack at 0.769 against 0.759,
    // and a round-1 play-on at 0.805 against a pass at 0.781.
    public class SecondLookTests
    {
        private static RootStat Stat(string key, int visits, double mean)
        {
            return new RootStat { Key = key, Visits = visits, Reward = mean * visits, RewardSquares = mean * mean * visits };
        }

        private static List<RootStat> Wilhelm() => new List<RootStat>
        {
            Stat("play wilhelm", 3100, 0.769), Stat("play other", 2200, 0.759), Stat("pass", 700, 0.50),
        };

        [Fact]
        public void AGapWiderThanTheLimitIsNotLookedAt()
        {
            List<RootStat> root = new List<RootStat> { Stat("play", 4000, 0.805), Stat("pass", 2000, 0.60) };
            Assert.Null(SecondLookChoice.Plan(root, 0.03));
        }

        [Fact]
        public void ARootOfOneStepIsNotLookedAt()
        {
            Assert.Null(SecondLookChoice.Plan(new List<RootStat> { Stat("pass", 6000, 0.5) }, 0.03));
        }

        [Fact]
        public void AGapWithinTheLimitComparesTheTwoMostVisitedSteps()
        {
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            Assert.NotNull(plan);
            Assert.Equal(new[] { "play wilhelm", "play other" }, plan.Keys);
            Assert.Equal(0.769, plan.Main[0], 9);
            Assert.Equal(0.759, plan.Main[1], 9);
        }

        [Fact]
        public void TheMostVisitedStepsAreComparedWhateverTheirOrderInTheList()
        {
            // A tie-break moved a cheaper step to the front: the list is not in visit order.
            List<RootStat> root = Wilhelm();
            root.Insert(0, root[2]);
            root.RemoveAt(3);
            root.Insert(0, root[2]);
            root.RemoveAt(3);
            SecondLookRecord plan = SecondLookChoice.Plan(root, 0.03);
            Assert.Equal(new[] { "play wilhelm", "play other" }, plan.Keys);
        }

        [Fact]
        public void AGapEqualToTheLimitIsLookedAt()
        {
            List<RootStat> root = new List<RootStat> { Stat("a", 3000, 0.5), Stat("b", 2000, 0.5) };
            Assert.NotNull(SecondLookChoice.Plan(root, 0));
        }

        [Fact]
        public void TheSecondSearchCanFlipTheChoice()
        {
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            string chosen = SecondLookChoice.Choose(plan, new List<RootStat> { Stat("play wilhelm", 300, 0.31), Stat("play other", 340, 0.42) }, "play wilhelm");
            Assert.Equal("play other", chosen);
            Assert.Equal("play other", plan.Chosen);
            Assert.True(plan.Changed);
            Assert.Equal(0.31, plan.Look[0], 9);
            Assert.Equal(0.42, plan.Look[1], 9);
            Assert.Equal(new[] { 300, 340 }, plan.LookVisits);
            Assert.Null(plan.Skipped);
        }

        [Fact]
        public void TheSecondSearchCanKeepTheChoice()
        {
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            string chosen = SecondLookChoice.Choose(plan, new List<RootStat> { Stat("play other", 340, 0.42), Stat("play wilhelm", 300, 0.51) }, "play wilhelm");
            Assert.Equal("play wilhelm", chosen);
            Assert.False(plan.Changed);
        }

        [Fact]
        public void AChoiceThatTheTieBreakMovedIsChangedWhenTheSecondSearchPicksAnotherStep()
        {
            // The economy tie-break had played a third step; the second look decides between the two it compared.
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            string chosen = SecondLookChoice.Choose(plan, new List<RootStat> { Stat("play wilhelm", 300, 0.51), Stat("play other", 340, 0.42) }, "pass");
            Assert.Equal("play wilhelm", chosen);
            Assert.True(plan.Changed);
        }

        [Fact]
        public void EqualValuesGoToTheMoreVisitedStepOfTheMainSearch()
        {
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            Assert.Equal("play wilhelm", SecondLookChoice.Choose(plan, new List<RootStat> { Stat("play wilhelm", 300, 0.4), Stat("play other", 300, 0.4) }, "play wilhelm"));
        }

        [Fact]
        public void AStepTheSecondSearchNeverReachedKeepsTheMainChoiceAndSaysWhy()
        {
            SecondLookRecord plan = SecondLookChoice.Plan(Wilhelm(), 0.03);
            string chosen = SecondLookChoice.Choose(plan, new List<RootStat> { Stat("play wilhelm", 600, 0.3) }, "play wilhelm");
            Assert.Equal("play wilhelm", chosen);
            Assert.False(plan.Changed);
            Assert.Contains("play other", plan.Skipped);
            Assert.Null(plan.Look);

            SecondLookRecord unvisited = SecondLookChoice.Plan(Wilhelm(), 0.03);
            SecondLookChoice.Choose(unvisited, new List<RootStat> { Stat("play wilhelm", 600, 0.3), Stat("play other", 0, 0) }, "play wilhelm");
            Assert.NotNull(unvisited.Skipped);
        }
    }
}
