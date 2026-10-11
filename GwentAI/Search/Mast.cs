using System;
using System.Collections.Generic;
using GwentAI.Sim;
using GwentGameplay;

namespace GwentAI.Search
{
    // Move-Average Sampling (Finnsson and Björnsson 2008): the search keeps,
    // for each kind of move, the mean result of the simulated games it was
    // played in, and the rollout prefers kinds that did well. A move's kind
    // is what it means rather than where it lands: the card played, or the
    // side a card's ability aimed at, or the option it took. So within one
    // search the rollout learns that a damage card hits enemies, a boost
    // allies, and which of Shupe's modes pays off, with no table of cards.
    public class Mast
    {
        private readonly Dictionary<string, double[]> m_Stats = new Dictionary<string, double[]>();
        private readonly List<KeyValuePair<EPlayerId, string>> m_Played = new List<KeyValuePair<EPlayerId, string>>();
        private readonly Random m_Random;
        // Chance of a uniform pick instead of the best kind.
        private readonly double m_Epsilon;

        public Mast(double epsilon, Random random)
        {
            m_Epsilon = epsilon;
            m_Random = random;
        }

        // Kind -> (summed result, plays), for diagnostics.
        public IDictionary<string, double[]> Stats => m_Stats;

        // A new simulated game.
        public void Begin()
        {
            m_Played.Clear();
            Trace?.Add(new List<string>());
        }

        // When set, every recorded kind, per simulated game, for diagnostics.
        public List<List<string>> Trace;

        public void Record(EPlayerId player, ARequestAction request, Move move)
        {
            string kind = Kind(player, request, move);
            Trace?[Trace.Count - 1].Add(player + " " + (kind ?? move.ToString()));
            if (kind != null)
            {
                m_Played.Add(new KeyValuePair<EPlayerId, string>(player, kind));
            }
        }

        // The simulated game's result for the observer; each player's moves
        // score its own result.
        public void End(EPlayerId observer, double reward)
        {
            foreach (KeyValuePair<EPlayerId, string> played in m_Played)
            {
                if (!m_Stats.TryGetValue(played.Value, out double[] stat))
                {
                    stat = m_Stats[played.Value] = new double[2];
                }
                stat[0] += played.Key == observer ? reward : 1 - reward;
                stat[1]++;
            }
            m_Played.Clear();
        }

        // Epsilon-greedy over the items' kinds; a kind not yet tried counts
        // as a win, so each is tried once. Ties break at random.
        public int Pick<T>(IList<T> items, Func<T, string> kind)
        {
            if (m_Random.NextDouble() < m_Epsilon)
            {
                return m_Random.Next(items.Count);
            }
            int best = -1;
            int ties = 0;
            double bestValue = double.NegativeInfinity;
            for (int i = 0; i < items.Count; i++)
            {
                string key = kind(items[i]);
                double value = key != null && m_Stats.TryGetValue(key, out double[] stat) ? stat[0] / stat[1] : 1;
                if (value > bestValue)
                {
                    bestValue = value;
                    best = i;
                    ties = 1;
                }
                else if (value == bestValue && m_Random.Next(++ties) == 0)
                {
                    best = i;
                }
            }
            return best;
        }

        public static string PlayKind(Card card)
        {
            return "p " + card.Template.Id;
        }

        // Null for moves without a kind (pass, done, mulligans).
        public static string Kind(EPlayerId player, ARequestAction request, Move move)
        {
            switch (move.Kind)
            {
                case EMoveKind.Play:
                    Card played = request.GetCard(move.CardId);
                    return played == null ? null : PlayKind(played);
                case EMoveKind.Target:
                    ARequestTargetsAction targets = (ARequestTargetsAction)request;
                    Card target = request.GetCard(move.CardId);
                    return target == null ? null : Source(request, targets.RequesterId, targets.AIHintId) + " t " + Side(player, target.GetPlayerId()) + " " + Zone(target.Position.Location);
                case EMoveKind.TargetRow:
                    ARequestTargetsAction rows = (ARequestTargetsAction)request;
                    return Source(request, rows.RequesterId, rows.AIHintId) + " r " + Side(player, move.Position.PlayerId);
                case EMoveKind.Choose:
                    RequestCardChoicesAction choices = (RequestCardChoicesAction)request;
                    Card choice = choices.GetChoiceFromId(move.CardId);
                    return choice == null ? null : Source(request, choices.RequesterId, choices.AIHintId) + " c " + (choice.IsVisibleTo(player) ? choice.Template.Id.ToString() : "?");
                default:
                    return null;
            }
        }

        private static string Source(ARequestAction request, ushort requester, int hint)
        {
            Card source = request.GetCard(requester);
            return (source == null ? "0" : source.Template.Id.ToString()) + "/" + hint;
        }

        private static string Side(EPlayerId player, EPlayerId owner)
        {
            return owner == player ? "ally" : "enemy";
        }

        private static string Zone(ELocation location)
        {
            switch (location)
            {
                case ELocation.Melee:
                case ELocation.Ranged:
                case ELocation.Siege:
                    return "board";
                default:
                    return location.ToString();
            }
        }
    }
}
