namespace GwentAI.Search
{
    // Engine-free rule for play-outs (Rollout.Play, before the policy pick).
    public static class MatchPoint
    {
        // The opponent has passed, our board strictly leads (a tie is no
        // lead), and winning the round wins the match: a round already won
        // (a drawn round counts for both, so that holds too), or round 3.
        // No legal play beats a certain match win, so passing is exact.
        public static bool PassWins(int lead, int ownCrowns, int roundIndex, bool opponentPassed)
        {
            return opponentPassed && lead > 0 && (ownCrowns >= 1 || roundIndex >= 2);
        }
    }
}
