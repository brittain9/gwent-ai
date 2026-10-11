using GwentAI.Search;
using Xunit;

namespace GwentAI.Tests
{
    // PassRules.Forbidden: which passes the heuristic rules take out, and where
    // in the tree (PassRuleSet.RootOnly confines the pass rules to the root decision).
    public class PassRulesTests
    {
        private struct Position : IPassPosition
        {
            public int RoundIndex { get; set; }
            public bool Resigns { get; set; }
            public bool OpponentPassed { get; set; }
            public int Lead { get; set; }
            public int HandCount { get; set; }
            public bool BeforeFirstPlay { get; set; }
        }

        private static PassRuleSet SevenCardHandRule(bool rootOnly)
        {
            return new PassRuleSet { EarlyPassHand = 7, EarlyPassMargin = -1, NoResign = true, RootOnly = rootOnly };
        }

        private static Position RoundOne(int hand)
        {
            return new Position { HandCount = hand };
        }

        [Fact]
        public void TheRootLosesThePassWithEightCardsUnderTheSevenCardHandRuleWhetherOrNotRootOnly()
        {
            Assert.True(PassRules.Forbidden(RoundOne(8), SevenCardHandRule(rootOnly: false), atRoot: true));
            Assert.True(PassRules.Forbidden(RoundOne(8), SevenCardHandRule(rootOnly: true), atRoot: true));
        }

        [Fact]
        public void SevenCardsMayPass()
        {
            Assert.False(PassRules.Forbidden(RoundOne(7), SevenCardHandRule(rootOnly: true), atRoot: true));
        }

        [Fact]
        public void OnePlyDownThePassStaysLegalOnlyWhenRootOnlyIsSet()
        {
            Assert.True(PassRules.Forbidden(RoundOne(8), SevenCardHandRule(rootOnly: false), atRoot: false));
            Assert.False(PassRules.Forbidden(RoundOne(8), SevenCardHandRule(rootOnly: true), atRoot: false));
        }

        [Fact]
        public void TheMarginAndFirstPlayRulesBindOnlyTheRootWhenRootOnlyIsSet()
        {
            PassRuleSet rules = new PassRuleSet { NoEarlyPass = true, EarlyPassMargin = 5, RootOnly = true };
            Position position = new Position { BeforeFirstPlay = true, Lead = 3, HandCount = 3 };
            Assert.True(PassRules.Forbidden(position, rules, atRoot: true));
            Assert.False(PassRules.Forbidden(position, rules, atRoot: false));
            position.BeforeFirstPlay = false;
            Assert.True(PassRules.Forbidden(position, rules, atRoot: true));
            Assert.False(PassRules.Forbidden(position, rules, atRoot: false));
        }

        [Fact]
        public void ASurelyLosingPassIsPrunedOnePlyDownWhenRootOnlyIsSet()
        {
            Position losing = new Position { Resigns = true, RoundIndex = 2, HandCount = 2 };
            Assert.True(PassRules.Forbidden(losing, SevenCardHandRule(rootOnly: true), atRoot: false));
            Assert.True(PassRules.Forbidden(losing, SevenCardHandRule(rootOnly: true), atRoot: true));
        }

        [Fact]
        public void RoundsAfterTheFirstAreNotBoundByTheHandRule()
        {
            Position round2 = new Position { RoundIndex = 1, HandCount = 9 };
            Assert.False(PassRules.Forbidden(round2, SevenCardHandRule(rootOnly: false), atRoot: true));
        }

        [Fact]
        public void AnOpponentWhoPassedLiftsTheHandRule()
        {
            Position position = new Position { HandCount = 9, OpponentPassed = true };
            Assert.False(PassRules.Forbidden(position, SevenCardHandRule(rootOnly: true), atRoot: true));
        }
    }
}
