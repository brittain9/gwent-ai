namespace GwentAI.Search
{
    // What the heuristic pass rules read from a position, one fact per
    // property so a caller computes only what a rule asks for (the hand
    // and the played cards cost a card list each).
    public interface IPassPosition
    {
        int RoundIndex { get; }
        bool Resigns { get; }
        bool OpponentPassed { get; }
        int Lead { get; }
        int HandCount { get; }
        bool BeforeFirstPlay { get; }
    }

    // The rules of SearchSettings that forbid a pass: NoEarlyPass,
    // EarlyPassHand, EarlyPassMargin (negative: off) and NoResign.
    // RootOnly confines the first three to the root
    // decision; NoResign prunes a pass that loses for sure, which is right
    // at any node.
    public struct PassRuleSet
    {
        public bool NoEarlyPass;
        public int EarlyPassHand;
        public int EarlyPassMargin;
        public bool NoResign;
        public bool RootOnly;
    }

    public static class PassRules
    {
        public static bool Forbidden<TPosition>(TPosition position, PassRuleSet rules, bool atRoot) where TPosition : IPassPosition
        {
            if (rules.NoResign && position.Resigns)
            {
                return true;
            }
            if ((rules.RootOnly && !atRoot) || position.RoundIndex != 0)
            {
                return false;
            }
            return (rules.NoEarlyPass && position.BeforeFirstPlay) ||
                   (rules.EarlyPassHand > 0 && !position.OpponentPassed && position.HandCount > rules.EarlyPassHand) ||
                   (rules.EarlyPassMargin >= 0 && !position.OpponentPassed && position.Lead >= 0 && position.Lead <= rules.EarlyPassMargin);
        }
    }
}
