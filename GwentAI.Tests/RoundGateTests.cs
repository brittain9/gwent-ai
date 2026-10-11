using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // RoundGate: which evaluator RoundGateEvaluator asks at a leaf
    // cut. The round index is 0-based and already advanced at the cut, so a
    // round-2 search (index 1) reaches the gate with index 2.
    public class RoundGateTests
    {
        [Fact]
        public void CutsBeforeTheRoundKeepTheLinearEvaluator()
        {
            // With fromRound 3, a round-1 search cuts at index 1 (round 2's start) and a
            // round-1 search with the two-round horizon cuts at index 2.
            Assert.False(RoundGate.UsesNet(roundIndex: 1, fromRound: 3, ownCrowns: 1, opponentCrowns: 0));
            Assert.False(RoundGate.UsesNet(roundIndex: 1, fromRound: 3, ownCrowns: 0, opponentCrowns: 1));
            Assert.False(RoundGate.UsesNet(roundIndex: 0, fromRound: 2, ownCrowns: 0, opponentCrowns: 0));
        }

        [Fact]
        public void CutsFromTheRoundOnUseTheNet()
        {
            Assert.True(RoundGate.UsesNet(roundIndex: 2, fromRound: 3, ownCrowns: 1, opponentCrowns: 1));
            Assert.True(RoundGate.UsesNet(roundIndex: 1, fromRound: 2, ownCrowns: 1, opponentCrowns: 0));
            Assert.True(RoundGate.UsesNet(roundIndex: 2, fromRound: 2, ownCrowns: 1, opponentCrowns: 1));
        }

        [Fact]
        public void TheRoundIndexDecidesNotTheCrowns()
        {
            // A drawn round 1 gives 1-1 at a round-2 start: still before round 3.
            Assert.False(RoundGate.UsesNet(roundIndex: 1, fromRound: 3, ownCrowns: 1, opponentCrowns: 1));
            // Round 2 won by one seat and drawn gives the same crowns at index 2.
            Assert.True(RoundGate.UsesNet(roundIndex: 2, fromRound: 3, ownCrowns: 0, opponentCrowns: 0));
        }

        [Theory]
        [InlineData(2, 0)]
        [InlineData(0, 2)]
        [InlineData(2, 1)]
        [InlineData(1, 2)]
        public void AFinishedMatchGoesToTheLinearEvaluatorForEitherSeat(int ownCrowns, int opponentCrowns)
        {
            Assert.False(RoundGate.UsesNet(roundIndex: 2, fromRound: 3, ownCrowns: ownCrowns, opponentCrowns: opponentCrowns));
            Assert.False(RoundGate.UsesNet(roundIndex: 3, fromRound: 2, ownCrowns: ownCrowns, opponentCrowns: opponentCrowns));
        }

        [Fact]
        public void TheDecisionIsTheSameFromBothSeats()
        {
            for (int round = 0; round < 4; round++)
            {
                for (int own = 0; own < 3; own++)
                {
                    for (int other = 0; other < 3; other++)
                    {
                        Assert.Equal(RoundGate.UsesNet(round, 3, own, other), RoundGate.UsesNet(round, 3, other, own));
                    }
                }
            }
        }
    }
}
