using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // MatchPoint.PassWins: the exact pass-to-win rule that runs before the
    // policy pick in play-outs.
    public class PassWinsMatchTests
    {
        [Fact]
        public void PassesWhenOpponentPassedWeLeadAndTheRoundWinsTheMatch()
        {
            Assert.True(MatchPoint.PassWins(lead: 5, ownCrowns: 1, roundIndex: 1, opponentPassed: true));
            Assert.True(MatchPoint.PassWins(lead: 1, ownCrowns: 1, roundIndex: 2, opponentPassed: true));
        }

        [Fact]
        public void RoundThreeAtOneAllIsDecidingWithoutOwnCrowns()
        {
            Assert.True(MatchPoint.PassWins(lead: 3, ownCrowns: 0, roundIndex: 2, opponentPassed: true));
        }

        [Fact]
        public void RoundOneWithNoRoundWonIsNotForced()
        {
            Assert.False(MatchPoint.PassWins(lead: 20, ownCrowns: 0, roundIndex: 0, opponentPassed: true));
            Assert.False(MatchPoint.PassWins(lead: 20, ownCrowns: 0, roundIndex: 1, opponentPassed: true));
        }

        [Fact]
        public void ATieIsNotALead()
        {
            Assert.False(MatchPoint.PassWins(lead: 0, ownCrowns: 1, roundIndex: 1, opponentPassed: true));
            Assert.False(MatchPoint.PassWins(lead: -2, ownCrowns: 1, roundIndex: 2, opponentPassed: true));
        }

        [Fact]
        public void TheSmallestLeadDecidesOnlyWhereTheRoundEndsTheMatch()
        {
            Assert.False(MatchPoint.PassWins(lead: 0, ownCrowns: 0, roundIndex: 2, opponentPassed: true));
            Assert.False(MatchPoint.PassWins(lead: 1, ownCrowns: 0, roundIndex: 1, opponentPassed: true));
            Assert.True(MatchPoint.PassWins(lead: 1, ownCrowns: 0, roundIndex: 2, opponentPassed: true));
            Assert.True(MatchPoint.PassWins(lead: 1, ownCrowns: 1, roundIndex: 0, opponentPassed: true));
        }

        [Fact]
        public void AnOpponentStillPlayingIsNotForced()
        {
            Assert.False(MatchPoint.PassWins(lead: 10, ownCrowns: 1, roundIndex: 2, opponentPassed: false));
        }
    }
}
