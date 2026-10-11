using System;
using System.Collections.Generic;
using GwentAI.Learn;
using GwentAI.Sim;
using GwentGameplay;

namespace GwentAI.Search
{
    // The default policy below the tree: random play shaped by the pass
    // rules every competent player follows, so a simulated round ends the
    // way real rounds do and pass decisions in the tree see sensible
    // futures. Uniform random play never passes while it holds cards, which
    // makes every simulated round a full dump of both hands; passing at
    // random loses rounds for nothing (Majer 2024 saw both make rollouts
    // misleading).
    public class Rollout
    {
        private readonly Random m_Random;
        public const double DefaultPassWhenAhead = 0.15;
        // Chance to pass while ahead, per decision, when the opponent has not
        // passed: a light stand-in for "stop spending cards on a won round".
        public double PassWhenAhead = DefaultPassWhenAhead;
        // Once the opponent has passed, a seat that can afford to lose the
        // round chases a deficit of at most this many points (about two
        // average cards) and passes otherwise.
        public const int DefaultChaseLimit = 16;
        public int ChaseLimit = DefaultChaseLimit;
        // Chance that the winner of round 1 passes round 2 before playing.
        // In stock self-play the round-1 winner scores 0 in round 2 in 73%
        // of games.
        public const double DefaultDryPass = 0.75;
        public double DryPass = DefaultDryPass;
        // Negative (the default): golds and the leader wait while neither
        // seat has a round, and a seat holding nothing else plays them.
        // Zero or more: they wait in every round the seat can afford to lose
        // (round 1; round 2 for the round-1 winner), as players keep them
        // for the round that decides the match, and a seat holding nothing
        // else passes with this chance instead of spending one. Uniform play
        // spends the leader in round 1 in about 70% of simulated games.
        public double KeepPremium = -1;
        // Threat-aware play: when the
        // opponent has a unit of ThreatUnit power or a row of ThreatRow
        // points, a seat holding a removal or weather card (CardRoles)
        // plays one with this chance; and a removal or weather card's
        // targets go, with this chance, to the opponent's tallest unit or
        // highest row. Random play answers threats only by accident, so the
        // search does not see a stacked row or a tall unit punished. 0: off.
        public double ThreatAware;
        // The opponent seat (not the Observer) plays as the recorded games' players do
        // while the observer is still playing (OpponentStyle: when it
        // passes, and which tier it plays); after the observer passed, it
        // keeps the rules below. The observer's own play never changes.
        public bool OpponentStyle;
        // With OpponentStyle: the opponent seat's answer cards (removal,
        // weather) weigh by whether the observer's board holds a threat, and
        // aim at the observer's tallest unit or highest row (Answer).
        public bool OpponentAnswers;
        // With OpponentStyle, the self-play fitted set in place of the recorded
        // games' (OpponentStyle.Select).
        public bool SelfPlayStyle;
        public const int ThreatUnit = 15;
        public const int ThreatRow = 40;
        // Aimed targets (Aim): a card that may aim at either side
        // and only hurts (CardRoles.Polarity -1: "deal 5 damage") picks among
        // the opponent's units, one that only helps (+1: "boost a unit by
        // 10") among its own, uniformly, when that side has a legal pick.
        // Uniform picks put about half of such effects on the wrong side, so
        // the search valued a removal or boost card held for later near
        // zero and spent it at once, on its own leader if need be (Viper
        // Witcher into an empty board).
        public bool Aim;
        // The search's observer: steps are named as its tree names them, so
        // a response the tree began (a hidden card step, a hidden pick)
        // continues under the same names (see Actions.Legal).
        public EPlayerId Observer;
        // Picks cards, targets and options by their kinds' results so far
        // in the search; null: uniformly.
        public Mast Mast;
        // Samples plays (pass included), targets and options from the policy
        // net's softmax at Temperature, from the mover's own view; null: the
        // rules above. Remembered: the observer's memory of opponent cards.
        public PolicyNet Policy;
        // The round index (0-2) from which Policy acts; earlier rounds use
        // the rules above.
        public int PolicyFromRound;
        public double Temperature = 1;
        // Policy's forward pass through its folded tables (PolicyNet.FoldedScores).
        public bool Folded;
        public IDictionary<ushort, int> Remembered;
        private readonly List<Card> m_Pool = new List<Card>();
        private readonly List<CardPosition> m_Positions = new List<CardPosition>();

        public Rollout(Random random)
        {
            m_Random = random;
        }

        // Legal: the request's steps as Actions.Legal names them for the
        // observer, if the caller has them; plays choose without them.
        public GameAction Decide(Player player, ARequestAction request, ICollection<string> taken, List<GameAction> legal = null)
        {
            // A play whose card the tree already chose needs only a position.
            if (request is RequestPlayCardAction play && taken.Count == 0)
            {
                return Play(player, play);
            }
            legal = legal ?? Actions.Legal(request, player, taken, observer: Observer);
            if (legal.Count == 0)
            {
                return null;
            }
            if (request is RequestMulliganAction)
            {
                // Keep: a random mulligan is no better on average and costs
                // a draw per card.
                return legal.Find(action => action.Key == "keep");
            }
            // Targets and choices: finish selecting before "done", as
            // players do; ending early wastes the effect.
            List<GameAction> picks = legal.FindAll(action => action.Key != "done");
            if (picks.Count == 0)
            {
                return legal[0];
            }
            if (Aim && picks.Count > 1)
            {
                picks = Aimed(player, request, picks);
            }
            bool aims = OpponentStyle && OpponentAnswers && player.Id != Observer;
            if ((ThreatAware > 0 || aims) && picks.Count > 1 && Answer(player, request, picks, aims) is GameAction answer)
            {
                return answer;
            }
            if (UsesPolicy(player) && picks.Count > 1)
            {
                Policy.Begin();
                foreach (GameAction pick in picks)
                {
                    Policy.Add(pick, player.Id);
                }
                return picks[Sample(player, request, taken.Count)];
            }
            return picks[Mast != null ? Mast.Pick(picks, action => Mast.Kind(player.Id, request, action.Move)) : m_Random.Next(picks.Count)];
        }

        private bool UsesPolicy(Player player)
        {
            return Policy != null && (PolicyFromRound <= 0 || player.CurrentGameController.RoundManager.CurrentRoundIndex >= PolicyFromRound);
        }

        // An index drawn from the policy's softmax over the options added.
        private int Sample(Player player, ARequestAction request, int taken)
        {
            long start = PhaseClock.Start();
            int index = Draw(player, request, taken);
            PhaseClock.Stop(Phase.RolloutPolicy, start);
            return index;
        }

        private int Draw(Player player, ARequestAction request, int taken)
        {
            double[] scores = Policy.Evaluate(player.CurrentGameController, player.Id, player.Id == Observer ? Remembered : null, request, taken, Folded);
            double[] probabilities = Policy.Probabilities(scores, Temperature);
            double u = m_Random.NextDouble();
            int last = Policy.Count - 1;
            for (int i = 0; i < last; i++)
            {
                u -= probabilities[i];
                if (u < 0)
                {
                    return i;
                }
            }
            return last;
        }

        // A play sampled from the policy over pass and the playable cards;
        // the position is uniform among the card's.
        private GameAction PolicyPlay(Player player, RequestPlayCardAction request, List<Card> cards, bool canPass)
        {
            Policy.Begin();
            if (canPass)
            {
                Policy.Add(OptionKind.Pass, null, 0, 0, 0, false, player.Id);
            }
            m_Pool.Clear();
            foreach (Card card in cards)
            {
                Positions(card, player, m_Positions);
                if (m_Positions.Count > 0)
                {
                    m_Pool.Add(card);
                    Policy.Add(OptionKind.Card, card, 0, 0, 0, false, player.Id);
                }
            }
            if (m_Pool.Count == 0)
            {
                return Actions.Pass(player);
            }
            int index = Policy.Count > 1 ? Sample(player, request, 0) : 0;
            if (canPass)
            {
                if (index == 0)
                {
                    return Actions.Pass(player);
                }
                index--;
            }
            Card chosen = m_Pool[index];
            Positions(chosen, player, m_Positions);
            CardPosition position = m_Positions[m_Random.Next(m_Positions.Count)];
            return new GameAction { Move = new Move { Player = player.Id, Kind = EMoveKind.Play, CardId = chosen.Id, Position = position } };
        }

        // Plays without naming steps: rollouts take most of the search's
        // time, and only tree steps need keys.
        private GameAction Play(Player player, RequestPlayCardAction request)
        {
            long playable = PhaseClock.Start();
            List<Card> cards = Actions.PlayableCards(request, player, out bool canPass);
            PhaseClock.Stop(Phase.RolloutPlayable, playable);
            // Exact: no play beats a certain match win, with or without the net.
            if (canPass && cards.Count > 0 && MatchPoint.PassWins(player.CurrentScore - player.OpponentScore, player.Crowns,
                    player.CurrentGameController.RoundManager.CurrentRoundIndex, player.OpponentPlayer.HasPassed))
            {
                return Actions.Pass(player);
            }
            if (UsesPolicy(player))
            {
                return PolicyPlay(player, request, cards, canPass);
            }
            if (OpponentStyle && player.Id != Observer && !player.OpponentPlayer.HasPassed)
            {
                return StylePlay(player, cards, canPass);
            }
            if (canPass && cards.Count > 0 && ShouldPass(player))
            {
                return Actions.Pass(player);
            }
            // Golds and the leader wait for a round that decides the match,
            // as players keep them; random play would spend them in round 1.
            Player opponent = player.OpponentPlayer;
            bool deciding = KeepPremium < 0 ? player.Crowns >= 1 || opponent.Crowns >= 1 : opponent.Crowns >= 1;
            if (ThreatAware > 0 && !opponent.HasPassed && Threatened(opponent) && m_Random.NextDouble() < ThreatAware)
            {
                // Premiums included: a gold answer is what it is kept for.
                List<Card> answers = cards.FindAll(Answers);
                while (answers.Count > 0)
                {
                    int index = m_Random.Next(answers.Count);
                    Positions(answers[index], player, m_Positions);
                    if (m_Positions.Count > 0)
                    {
                        CardPosition position = m_Positions[m_Random.Next(m_Positions.Count)];
                        return new GameAction { Move = new Move { Player = player.Id, Kind = EMoveKind.Play, CardId = answers[index].Id, Position = position } };
                    }
                    answers.RemoveAt(index);
                }
            }
            List<Card> pool = deciding ? cards : cards.FindAll(card => !Premium(card));
            if (pool.Count == 0)
            {
                if (KeepPremium >= 0 && canPass && m_Random.NextDouble() < KeepPremium)
                {
                    return Actions.Pass(player);
                }
                pool = cards;
            }
            List<CardPosition> positions = new List<CardPosition>();
            while (pool.Count > 0)
            {
                int index = opponent.HasPassed ? Chase(pool, opponent.CurrentScore - player.CurrentScore)
                    : Mast != null ? Mast.Pick(pool, Mast.PlayKind) : m_Random.Next(pool.Count);
                Card card = pool[index];
                Positions(card, player, positions);
                if (positions.Count > 0)
                {
                    CardPosition position = positions[m_Random.Next(positions.Count)];
                    return new GameAction { Move = new Move { Player = player.Id, Kind = EMoveKind.Play, CardId = card.Id, Position = position } };
                }
                pool.RemoveAt(index);
            }
            return Actions.Pass(player);
        }

        // The style's own generator, seeded from the rollout's on first use,
        // so the fitted chances are realized (see SplitMix64).
        private SplitMix64 m_StyleRandom;
        private SplitMix64 StyleRandom => m_StyleRandom ?? (m_StyleRandom = new SplitMix64(m_Random.Next()));

        // The opponent seat's play with the observer still open (see
        // GwentAI.Search.OpponentStyle): pass with the fitted chance, or a
        // card drawn by its tier's weight.
        private GameAction StylePlay(Player player, List<Card> cards, bool canPass)
        {
            if (cards.Count == 0)
            {
                return Actions.Pass(player);
            }
            Player opponent = player.OpponentPlayer;
            Search.OpponentStyle.Fit style = Search.OpponentStyle.Select(SelfPlayStyle);
            if (canPass)
            {
                int theirs = opponent.GetCardsInLocation(ELocation.Hand).Count +
                             opponent.GetCardsInLocation(ELocation.Leader, includeOnlyCardsThatCanBePlayed: true).Count;
                double chance = style.PassChance(player, cards.Count, theirs);
                if (chance > 0 && StyleRandom.NextDouble() < chance)
                {
                    return Actions.Pass(player);
                }
            }
            List<Card> pool = new List<Card>(cards);
            List<double> weights = new List<double>(pool.Count);
            bool threatened = OpponentAnswers && Threatened(opponent);
            double total = 0;
            foreach (Card card in pool)
            {
                weights.Add(style.PlayWeight(card, player, cards.Count, OpponentAnswers, threatened));
                total += weights[weights.Count - 1];
            }
            List<CardPosition> positions = new List<CardPosition>();
            while (pool.Count > 0)
            {
                double pick = StyleRandom.NextDouble() * total;
                int index = pool.Count - 1;
                for (int i = 0; i < pool.Count - 1; i++)
                {
                    pick -= weights[i];
                    if (pick < 0)
                    {
                        index = i;
                        break;
                    }
                }
                Positions(pool[index], player, positions);
                if (positions.Count > 0)
                {
                    CardPosition position = positions[StyleRandom.Next(positions.Count)];
                    return new GameAction { Move = new Move { Player = player.Id, Kind = EMoveKind.Play, CardId = pool[index].Id, Position = position } };
                }
                total -= weights[index];
                pool.RemoveAt(index);
                weights.RemoveAt(index);
            }
            return Actions.Pass(player);
        }

        // The picks on the side the request's card favours (Aim), or all of
        // them when it favours none or that side has no legal pick.
        private static List<GameAction> Aimed(Player player, ARequestAction request, List<GameAction> picks)
        {
            int polarity = CardRoles.Polarity(OptionEncoder.SourceTemplate(request));
            if (polarity == 0)
            {
                return picks;
            }
            EPlayerId side = polarity > 0 ? player.Id : player.OpponentPlayer.Id;
            List<GameAction> aimed = picks.FindAll(pick => (pick.Kind == OptionKind.Target || pick.Kind == OptionKind.Row) && pick.Side == side);
            return aimed.Count > 0 ? aimed : picks;
        }

        private static bool Answers(Card card)
        {
            return CardRoles.Has(card.Template.Id, "removal") || CardRoles.Has(card.Template.Id, "weather");
        }

        // The opponent's tallest unit and highest row (rows: melee, ranged,
        // siege by index 0-2).
        private static void Board(Player owner, out int tallest, out int[] rows)
        {
            BoardSide side = owner.CurrentGameController.CardManager.BoardManager.BoardSides[owner.Id.ToIndex()];
            Location[] locations = { side.Melee, side.Ranged, side.Siege };
            tallest = 0;
            rows = new int[3];
            for (int r = 0; r < 3; r++)
            {
                rows[r] = locations[r].GetScore();
                foreach (Card card in locations[r].Cards)
                {
                    if (card != null && !card.IsInExecutionStack && !card.IsWaitingToDie && card.Power > tallest)
                    {
                        tallest = card.Power;
                    }
                }
            }
        }

        // Whether the seat's board holds a threat (a unit or a row at the
        // threat sizes). Public so callers can read it.
        public static bool Threatened(Player opponent)
        {
            Board(opponent, out int tallest, out int[] rows);
            return tallest >= ThreatUnit || rows[0] >= ThreatRow || rows[1] >= ThreatRow || rows[2] >= ThreatRow;
        }

        // A removal or weather card's target, with chance ThreatAware (always
        // for the styled opponent, see OpponentStyle): the
        // opponent's tallest unit among the picks, or its highest row; null
        // when the request is not such a card's or no pick is the opponent's.
        private GameAction Answer(Player player, ARequestAction request, List<GameAction> picks, bool always)
        {
            int source = OptionEncoder.SourceTemplate(request);
            if (source == 0 || !(CardRoles.Has(source, "removal") || CardRoles.Has(source, "weather")))
            {
                return null;
            }
            EPlayerId opponent = player.OpponentPlayer.Id;
            GameAction best = null;
            int bestValue = int.MinValue;
            int[] rows = null;
            foreach (GameAction pick in picks)
            {
                int value;
                if (pick.Kind == OptionKind.Target && pick.Card != null && pick.Side == opponent)
                {
                    value = pick.Card.Power;
                }
                else if (pick.Kind == OptionKind.Row && pick.Side == opponent)
                {
                    if (rows == null)
                    {
                        Board(player.OpponentPlayer, out int _, out rows);
                    }
                    value = pick.Location == ELocation.Melee ? rows[0] : pick.Location == ELocation.Ranged ? rows[1] : pick.Location == ELocation.Siege ? rows[2] : int.MinValue;
                }
                else
                {
                    continue;
                }
                if (value > bestValue)
                {
                    bestValue = value;
                    best = pick;
                }
            }
            return best != null && (always || m_Random.NextDouble() < ThreatAware) ? best : null;
        }

        private static void Positions(Card card, Player player, List<CardPosition> positions)
        {
            long start = PhaseClock.Start();
            Actions.Positions(card, player, positions);
            PhaseClock.Stop(Phase.RolloutPositions, start);
        }

        private static bool Premium(Card card)
        {
            return card.Template.Tier == ECardTier.Gold || card.Template.Tier == ECardTier.Leader;
        }

        // Against a passed opponent: the weakest unit that takes the lead
        // alone, or failing that the strongest.
        private static int Chase(List<Card> pool, int deficit)
        {
            int best = -1;
            for (int i = 0; i < pool.Count; i++)
            {
                int power = pool[i].Power;
                if (best < 0)
                {
                    best = i;
                    continue;
                }
                int current = pool[best].Power;
                bool takesLead = power > deficit;
                bool currentTakesLead = current > deficit;
                if (takesLead ? !currentTakesLead || power < current : !currentTakesLead && power > current)
                {
                    best = i;
                }
            }
            return best;
        }

        private bool ShouldPass(Player player)
        {
            int lead = player.CurrentScore - player.OpponentScore;
            Player opponent = player.OpponentPlayer;
            // Losing this round loses the match: the opponent already has a
            // round (a drawn round counts for both).
            bool mustWin = opponent.Crowns >= 1;
            // Having won round 1, give round 2 away for free, as players
            // mostly do: the opponent must then spend cards to win it.
            if (player.Crowns == 1 && opponent.Crowns == 0 && player.CurrentScore == 0 && opponent.CurrentScore == 0 &&
                m_Random.NextDouble() < DryPass)
            {
                return true;
            }
            if (opponent.HasPassed)
            {
                // Win the round at no further cost. Behind, chase when the
                // round must be won or the deficit is small; otherwise keep
                // the cards for later rounds.
                // A tied round gives both players a round, which wins the
                // match for the seat already a round up.
                bool tieWins = lead == 0 && player.Crowns > opponent.Crowns;
                return lead > 0 || tieWins || (!mustWin && -lead > ChaseLimit);
            }
            // Ahead with the opponent still playing: sometimes stop to save
            // cards. Never pass when behind with the round still open, and
            // never give up a won round when the opponent is on match point.
            return lead > 0 && !mustWin && m_Random.NextDouble() < PassWhenAhead;
        }
    }
}
