namespace GwentAI.Search
{
    // The rule of RoundGateEvaluator, free of the engine so a unit test can
    // run it. RoundIndex is 0-based (0 is round 1) and, at a leaf cut, is
    // already the round the cut opens: a round-2 search (index 1) reaches
    // its cut with index 2, the start of round 3.
    public static class RoundGate
    {
        public const int FirstRound = 2;
        public const int LastRound = 3;

        // Whether the net scores the position. FromRound is 1-based: the net
        // scores cuts into round FromRound or later. A finished match is the
        // linear evaluator's, which returns the result.
        public static bool UsesNet(int roundIndex, int fromRound, int ownCrowns, int opponentCrowns)
        {
            if (ownCrowns >= 2 || opponentCrowns >= 2)
            {
                return false;
            }
            return roundIndex >= fromRound - 1;
        }
    }
}
