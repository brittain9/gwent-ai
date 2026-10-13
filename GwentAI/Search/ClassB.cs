namespace GwentAI.Search
{
    // Engine-free rule for the class-B state: in round 2, the mover has won
    // round 1 and the opponent no round, and has not passed, and holds at least
    // one card fewer than the opponent. Hand sizes are public and count the
    // cards in the hand without the leader.
    public static class ClassB
    {
        // roundIndex: 0-based (1 is round 2). ownHand, opponentHand: cards in
        // hand, without the leader.
        public static bool Is(int roundIndex, int ownCrowns, int opponentCrowns, bool ownPassed, int ownHand, int opponentHand)
        {
            return roundIndex == 1 && ownCrowns == 1 && opponentCrowns == 0 && !ownPassed && ownHand < opponentHand;
        }
    }
}
