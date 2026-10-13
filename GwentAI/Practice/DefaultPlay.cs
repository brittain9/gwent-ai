namespace GwentAI.Practice
{
    // The values the AI plays with when nothing sets them: Practice's
    // search and belief (PracticeAI).
    public static class DefaultPlay
    {
        // No resigning pass in a lost round 3 (SearchSettings.NoResign).
        public const bool NoResign = true;
        // Ties among the best plays go to the economical step within this
        // gap (SearchSettings.EconomyTieBreak; negative: off).
        public const double EconomyTieBreak = GwentAI.Search.EconomyOrder.DefaultGap;
        // The opponent style fitted on recorded games (Search.OpponentStyle),
        // as bits: 1 the belief's hold weights (Belief.OpponentStyle), 2 the
        // opponent seat's rollout play (SearchSettings.OpponentStyle), 4 its
        // answer cards (SearchSettings.OpponentAnswers), 8 the self-play
        // fitted set in place of the recorded games' (SelfPlayStyle). 0: off.
        public const int OpponentStyle = 7;
        // The round-1 winner's chance to pass a dry round in a rollout
        // (SearchSettings.DryPass).
        public const double DryPass = 0.5;
        // Rollouts answer a threat with removal or weather at this chance
        // (SearchSettings.ThreatAware; 0: off).
        public const double ThreatAware = 0.8;
        // The score margin's weight in a finished simulation's reward
        // (SearchSettings.Margin; 0: off).
        public const double Margin = 0.05;
        // The name of the policy net embedded as eval/policy.pnet, the PUCT
        // prior (PracticeBudget.LoadDefaultPrior).
        public const string PolicyNet = "pol2";
        // The PUCT constant (SearchSettings.CPuct).
        public const double CPuct = 1;
        // Practice's adaptive budget: settled or immaterial decisions stop
        // from half of the time budget; open ones run on to ThinkExtend
        // times it (SearchSettings.Extend).
        public const double ThinkExtend = 1.5;
        // Each mulligan's budget is 1.5 plays' (15 s, floor 7.5 s, at most
        // 22.5 s at a 10 s play budget): a mulligan decided at a play's
        // budget is noisy (SearchSettings.MulliganScale).
        public const double ThinkMulliganScale = 1.5;
        // The value net that scores the search's leaf cuts, embedded as eval/value.vnet
        // (PracticeBudget.WithDefaultValueNet), and the round it scores from (0: at every
        // cut; the round evaluator before it).
        public const string ValueNet = "fta";
        public const int NetFromRound = 2;
    }
}
