using System.Collections.Generic;
using GwentAI.Search;
using GwentAI.Sim;
using Xunit;

namespace GwentAI.Tests
{
    // The oracle files' text (OracleFormat): each record has a golden line.
    public class OracleFormatTests
    {
        [Fact]
        public void LegalLineJoinsKeysAfterTheVariant()
        {
            Assert.Equal("17 PlayCard P1 full: pass | play 112103:4 P1 Melee 0 | play 112103:4 P1 Melee 1",
                         OracleFormat.Legal(17, "PlayCard", "P1", OracleFormat.Full,
                                            new List<string> { "pass", "play 112103:4 P1 Melee 0", "play 112103:4 P1 Melee 1" }));
            Assert.Equal("17 PlayCard P1 grouped: pass | card 112103:4",
                         OracleFormat.Legal(17, "PlayCard", "P1", OracleFormat.Grouped, new List<string> { "pass", "card 112103:4" }));
        }

        [Fact]
        public void LegalLineWithNoKeyEndsAtTheColon()
        {
            Assert.Equal("3 Choice P2 full:", OracleFormat.Legal(3, "Choice", "P2", OracleFormat.Full, new List<string>()));
        }

        [Fact]
        public void StateRecordsHaveTheirGoldenLines()
        {
            Assert.Equal("D 412 17 PlayCard P1", OracleFormat.DecisionHeader(412, 17, "PlayCard", "P1"));
            Assert.Equal("A 413", OracleFormat.ActionHeader(413));
            Assert.Equal("C 12 112103 1 1 0 4 0 6 2 1024 -1 3 9 1 2",
                         OracleFormat.Card(12, 112103, 1, 1, 0, 4, 0, 6, 2, 1024, -1, 3,
                                           OracleFormat.WaitingToDie | OracleFormat.CanBePlayed, 1, 2));
            Assert.Equal("Z 2 8 31 7 40", OracleFormat.Zone(2, 8, new List<int> { 31, 7, 40 }));
            Assert.Equal("Z 1 32", OracleFormat.Zone(1, 32, new List<int>()));
            Assert.Equal("T 1 4 512", OracleFormat.RowToken(1, 4, 512));
            Assert.Equal("P 1 3 0 1 1 0 255 0 0 - 2.1,2.4 - 12 - -",
                         OracleFormat.Player(1, 3, false, 1, true, 0, 255, 0, false, OracleFormat.List(new int[0]),
                                             OracleFormat.Locations(new[] { new KeyValuePair<int, int>(2, 1), new KeyValuePair<int, int>(2, 4) }),
                                             OracleFormat.List(new int[0]), OracleFormat.List(new[] { 12 }), "-", "-"));
            Assert.Equal("R 1 2 5 1:1:2:7:84:90 2:2:0:3:12:4",
                         OracleFormat.Round(1, 2, 5, new List<int[]> { new[] { 1, 1, 2, 7, 84, 90 }, new[] { 2, 2, 0, 3, 12, 4 } }));
            Assert.Equal("X 17 1 1 1 0 -1", OracleFormat.Request(17, 1, 1, 1, false, -1));
            Assert.Equal("Q 3 2 40 2 1 5 0 41 2 1 -1 0",
                         OracleFormat.Queue(3, new List<int[]> { new[] { 40, 2, 1, 5, 0 }, new[] { 41, 2, 1, -1, 0 } }));
            Assert.Equal("Q 0 0", OracleFormat.Queue(0, new List<int[]>()));
            Assert.Equal("G -7 4294967295", OracleFormat.Generator(-7, uint.MaxValue));
            Assert.Equal("1,2,3", OracleFormat.List(new[] { 1, 2, 3 }));
        }

        [Fact]
        public void SharedObjectsPointAtTheirFirstHolder()
        {
            object a = new object(), b = new object();
            Assert.Equal(new[] { 0, 1, 0, 3, 1 }, OracleFormat.ShareGroups(new List<object> { a, b, a, new object(), b }));
        }

        // The rand-sm draw, computed independently from its definition
        // (SplitMix64.Pick) in Python.
        [Theory]
        [InlineData(1010000, 0, 0, 1000, 379)]
        [InlineData(1010000, 1, 0, 1000, 154)]
        [InlineData(1010000, 0, 1, 1000, 723)]
        [InlineData(-7, 1, 12, 1000, 257)]
        [InlineData(12345, 0, 99, 1000, 895)]
        [InlineData(1010000, 0, 0, 7, 6)]
        public void SeededPickMatchesItsDefinition(int seed, int seat, int decision, int n, int expected)
        {
            Assert.Equal(expected, SplitMix64.Pick(seed, seat, decision, n));
        }
    }
}
