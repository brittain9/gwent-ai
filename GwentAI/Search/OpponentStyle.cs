using System;
using GwentGameplay;

namespace GwentAI.Search
{
    // How the opponent is expected to play, in two fitted sets
    // (fitted offline):
    //
    // - Human: the players of recorded human games, fitted on those games.
    //   This AI's own play is the opposite (it spends silvers first), so
    //   self-play never fits this; the search uses it for the opponent only.
    // - Self-play: this AI's own play, fitted on its self-play games with
    //   both seats the same agent; used when the opponent is expected to
    //   play like this AI.
    //
    // What a set holds:
    // - Hold weights (Belief): a gold or silver is this many times as likely
    //   in the opponent's hidden hand as a bronze, by round.
    // - Pass (Rollout, other seat open): a logistic regression on the
    //   situation. Not fitted, as in the rollout: no pass while the seat
    //   must win the round and does not lead (it loses for sure).
    // - Play (Rollout, other seat open): a card's chance is proportional to
    //   exp(its tier's log weight in the context), a bronze's being 0.
    // - Answers: an answer card's weight also depends on
    //   whether the observer's board holds a threat, and its target is the
    //   observer's tallest unit or highest row (Rollout.Answer; not fitted).
    public static class OpponentStyle
    {
        public sealed class Fit
        {
            private readonly double[] m_HoldGold, m_HoldSilver, m_Pass, m_LastCards, m_LastCardsAnswers;
            private readonly double[,] m_Play, m_PlayAnswers;
            private readonly double m_AnswerThreat, m_AnswerCalm;

            // hold: gold and silver weights against a bronze in R1, R2, R3.
            // pass: bias, won R1, dry (won R1 and own score 0), ahead, lead/10
            // (within ±4), card difference (within ±3), hand/10, won x ahead,
            // won x lead/10, must win.
            // play, playAnswers: log weights by context (R1; R2 after winning
            // R1; must win the round) and tier (silver, gold, leader), and
            // lastCards, lastCardsAnswers: with 3 cards or fewer while the
            // round must be won, extra log weights by tier. play and lastCards
            // carry no answer terms; playAnswers and lastCardsAnswers are
            // fitted beside two weights for an answer card (removal or
            // weather), while the other seat holds a threat (answerThreat)
            // and while it holds none (answerCalm).
            internal Fit(double[] holdGold, double[] holdSilver, double[] pass,
                         double[,] play, double[] lastCards,
                         double[,] playAnswers, double[] lastCardsAnswers, double answerThreat, double answerCalm)
            {
                m_HoldGold = holdGold;
                m_HoldSilver = holdSilver;
                m_Pass = pass;
                m_Play = Exp(play);
                m_LastCards = Exp(lastCards);
                m_PlayAnswers = Exp(playAnswers);
                m_LastCardsAnswers = Exp(lastCardsAnswers);
                m_AnswerThreat = Math.Exp(answerThreat);
                m_AnswerCalm = Math.Exp(answerCalm);
            }

            // A hidden hand card's weight against a bronze in round index
            // `round` (0-2; earlier counts as round 1).
            public double HoldWeight(ECardTier tier, int round)
            {
                int r = Math.Max(0, Math.Min(2, round));
                return tier == ECardTier.Gold ? m_HoldGold[r] : tier == ECardTier.Silver ? m_HoldSilver[r] : 1;
            }

            // The chance the seat passes with the other seat open, holding
            // `cards` playable cards (the leader included) against the other's
            // `theirs`; 0 where the pass loses for sure.
            public double PassChance(Player player, int cards, int theirs)
            {
                int lead = player.CurrentScore - player.OpponentScore;
                bool mustWin = MustWin(player);
                if (mustWin && lead <= 0)
                {
                    return 0;
                }
                double won = player.Crowns == 1 && player.OpponentPlayer.Crowns == 0 ? 1 : 0;
                double ahead = lead > 0 ? 1 : 0;
                double scaled = Math.Max(-4, Math.Min(4, lead / 10.0));
                double logit = m_Pass[0] + m_Pass[1] * won + m_Pass[2] * won * (player.CurrentScore == 0 ? 1 : 0) + m_Pass[3] * ahead +
                               m_Pass[4] * scaled + m_Pass[5] * Math.Max(-3, Math.Min(3, cards - theirs)) + m_Pass[6] * cards / 10.0 +
                               m_Pass[7] * won * ahead + m_Pass[8] * won * scaled + m_Pass[9] * (mustWin ? 1 : 0);
                return 1 / (1 + Math.Exp(-logit));
            }

            // A card's weight among the seat's `cards` playable cards, with the
            // other seat open. With `answers`, `threatened` says whether the
            // other seat's board holds a threat (Rollout.Threatened).
            public double PlayWeight(Card card, Player player, int cards, bool answers, bool threatened)
            {
                int id = card.Template.Id;
                double answer = answers && (CardRoles.Has(id, "removal") || CardRoles.Has(id, "weather")) ? (threatened ? m_AnswerThreat : m_AnswerCalm) : 1;
                int tier = card.Template.Tier == ECardTier.Silver ? 0 : card.Template.Tier == ECardTier.Gold ? 1 : card.Template.Tier == ECardTier.Leader ? 2 : -1;
                if (tier < 0)
                {
                    return answer;
                }
                bool mustWin = MustWin(player);
                int context = mustWin ? 2 : player.Crowns >= 1 ? 1 : 0;
                double last = mustWin && cards <= 3 ? (answers ? m_LastCardsAnswers : m_LastCards)[tier] : 1;
                return answer * (answers ? m_PlayAnswers : m_Play)[context, tier] * last;
            }
        }

        // The players of recorded human games.
        private static readonly Fit s_Human = new Fit(
            new[] { 2.13, 2.68, 2.86 }, new[] { 1.59, 1.75, 1.60 },
            new[] { -1.105, 0.935, 2.251, 0.886, -0.041, -0.021, -1.874, 0.110, 0.248, -1.568 },
            new[,] { { -0.873, -1.179, -0.316 }, { -0.177, -0.247, -0.678 }, { -0.461, -0.325, -0.650 } },
            new[] { 0.270, 0.001, -0.376 },
            new[,] { { -0.806, -1.127, -0.327 }, { -0.138, -0.167, -0.683 }, { -0.408, -0.295, -0.653 } },
            new[] { 0.306, 0.030, -0.350 },
            0.194, -0.348);

        // This AI's own play, from self-play (successive-draw hold weights).
        private static readonly Fit s_SelfPlay = new Fit(
            new[] { 1.66, 1.84, 1.47 }, new[] { 1.30, 1.09, 0.79 },
            new[] { 0.979, -1.299, 2.080, 1.800, -0.091, 0.405, -4.266, -1.196, 0.154, -5.453 },
            new[,] { { -0.951, -1.515, -1.437 }, { -0.653, -1.648, -2.364 }, { 0.228, 0.121, 0.096 } },
            new[] { -0.374, -0.195, 0.120 },
            new[,] { { -0.944, -1.498, -1.444 }, { -0.646, -1.632, -2.364 }, { 0.240, 0.136, 0.087 } },
            new[] { -0.378, -0.186, 0.121 },
            -0.032, -0.084);

        // The set for the opponent model: the self-play set when selfPlay is
        // true, else the recorded games'.
        public static Fit Select(bool selfPlay)
        {
            return selfPlay ? s_SelfPlay : s_Human;
        }

        public static bool MustWin(Player player)
        {
            return player.OpponentPlayer.Crowns >= 1;
        }

        private static double[,] Exp(double[,] logs)
        {
            double[,] values = new double[logs.GetLength(0), logs.GetLength(1)];
            for (int i = 0; i < logs.GetLength(0); i++)
            {
                for (int j = 0; j < logs.GetLength(1); j++)
                {
                    values[i, j] = Math.Exp(logs[i, j]);
                }
            }
            return values;
        }

        private static double[] Exp(double[] logs)
        {
            double[] values = new double[logs.Length];
            for (int i = 0; i < logs.Length; i++)
            {
                values[i] = Math.Exp(logs[i]);
            }
            return values;
        }
    }
}
