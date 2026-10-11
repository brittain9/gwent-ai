using System.Collections.Generic;

namespace GwentAI.Search
{
    // What a root step spends, for the economy tie-break.
    public enum EconomyStep
    {
        Pass,
        Bronze,
        Silver,
        Gold,
        Leader,
        // A draw-two spy: one card played, two drawn.
        Spy,
    }

    // The round situations in which the economy tie-break prefers steps.
    public enum EconomyRound
    {
        // No preference: round 3, round 2 for a seat without a round, or
        // round 2 once the opponent has passed.
        None,
        // Round 1.
        First,
        // Round 1 while the seat holds more cards than the opponent.
        FirstWithMoreCards,
        // Round 2 after winning round 1, the opponent still open.
        SecondAfterWin,
    }

    // A root step as the tie-break sees it.
    public struct TieCandidate
    {
        public int Visits;
        public double Mean;
        public EconomyStep Step;
    }

    // The economy tie-break's rule, free
    // of the engine. At a play decision, root steps whose values are within
    // a small gap cost at most that gap in the search's own values. Among
    // them a
    // player who counts card economy keeps
    // the premiums for the round that decides the match:
    //   - round 2 after winning round 1: pass (a won round needs no second
    //     win), then a bronze, a silver, a gold, the leader;
    //   - round 1 with more cards than the opponent: the same order (a
    //     concession that keeps card advantage);
    //   - round 1 otherwise: a bronze, then a silver, a gold, the leader. A
    //     pass is not ranked: the tie-break neither plays it nor replaces it,
    //     so it cannot add a round-1 concession at even cards;
    //   - elsewhere: no preference.
    public static class EconomyOrder
    {
        // A step is tied with the most visited one when its mean is within
        // the gap of it and it has at least MinVisitShare of its visits. The
        // gap is fixed, not widened by the standard error: at 1,000 simulations 2 standard errors are 0.05-0.08,
        // which would tie far more steps than the rule means to.
        public const double MinVisitShare = 0.25;
        // The default gap: the steps that spend more sit 0.02-0.03 above the
        // economical ones at Practice budget.
        public const double DefaultGap = 0.03;

        // Lower spends less; null: not ranked.
        public static double? Cost(EconomyStep step, EconomyRound round)
        {
            switch (round)
            {
                case EconomyRound.First:
                    return step == EconomyStep.Pass ? (double?)null : StepCost(step);
                case EconomyRound.FirstWithMoreCards:
                case EconomyRound.SecondAfterWin:
                    return StepCost(step);
                default:
                    return null;
            }
        }

        // A draw-two spy nets a card (it replaces itself and draws a
        // second), so it ranks between a pass and a bronze.
        public const double SpyCost = 0.5;

        // The five draw-two spies (roles `spy` and `draw`; the catalogue
        // tiers them as silvers).
        public static bool IsDrawTwoSpy(int template)
        {
            return CardRoles.Has(template, "spy") && CardRoles.Has(template, "draw");
        }

        private static double StepCost(EconomyStep step)
        {
            return step == EconomyStep.Spy ? SpyCost : (int)step;
        }

        // The index of the step to play among root steps ranked most visited
        // first: the cheapest ranked step tied with the first; among equally
        // cheap ones, the most visited. 0 when the first is not ranked.
        public static int Choose(IList<TieCandidate> ranked, EconomyRound round, double gap)
        {
            TieCandidate best = ranked[0];
            double? cheapest = Cost(best.Step, round);
            int chosen = 0;
            for (int i = 1; i < ranked.Count && cheapest != null; i++)
            {
                TieCandidate step = ranked[i];
                double? cost = Cost(step.Step, round);
                bool tied = step.Visits >= MinVisitShare * best.Visits && best.Mean - step.Mean <= gap;
                if (tied && cost < cheapest)
                {
                    chosen = i;
                    cheapest = cost;
                }
            }
            return chosen;
        }
    }
}
