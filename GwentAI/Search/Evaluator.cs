using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GwentGameplay;
using Newtonsoft.Json;

namespace GwentAI.Search
{
    // What the search scores a simulated game with at its round horizon:
    // the chance that the seat wins the match. Remembered: the opponent
    // cards the seat saw (Belief.Remember), for evaluators that read them.
    public interface IRoundEvaluator
    {
        double WinProbability(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered);
    }

    // The chance that a seat wins the match, judged at the start of a round
    // (after the last round is scored, cards are drawn and carryover is on
    // the board). Between rounds a Gwent position reduces to a few numbers:
    // rounds won, cards in hand and their strength, carryover, the leader,
    // and who plays first.
    //
    // The model is logistic, one per round state, fitted offline on self-play games.
    // Points scored in a round count only through who won it: a large win
    // is worth no more than a narrow one.
    public class Evaluator : IRoundEvaluator
    {
        // The features, each from the seat's side (seat minus opponent).
        public static readonly string[] Names =
        {
            "hand",         // cards in hand
            "gold",         // golds in hand
            "silver",       // silvers in hand
            "hand_power",   // power of the units in hand, in tens
            "board",        // score on the board (carryover), in tens
            "leader",       // leader still unused
            "deck",         // cards left in deck, in fives
            "start",        // +1 when the seat plays first this round, -1 otherwise
        };

        public class Model
        {
            public double Bias;
            public double[] Weights;
            // Optional (fitted offline): a weight per template for
            // each copy in hand (the playable leader included), seat minus
            // opponent, on top of the tier counts.
            public Dictionary<int, double> Cards;
        }

        // By round state: "10" (the seat has won a round, the opponent has
        // not) and "11". "01" is "10" seen from the other side.
        private readonly Dictionary<string, Model> m_Models;

        public Evaluator(Dictionary<string, Model> models)
        {
            foreach (KeyValuePair<string, Model> entry in models)
            {
                if (entry.Value.Weights == null || entry.Value.Weights.Length != Names.Length)
                {
                    throw new ArgumentException($"Model {entry.Key} needs {Names.Length} weights");
                }
            }
            m_Models = models;
        }

        public static Evaluator Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        // The weights' source text, kept so a search worker can parse the
        // same text (SearchProtocol: models cross the wire as their files).
        public string Json { get; private set; }

        public static Evaluator Parse(string json)
        {
            return new Evaluator(JsonConvert.DeserializeObject<Dictionary<string, Model>>(json)) { Json = json };
        }

        // P(seat wins the match). The game must be between rounds or later:
        // in a finished game, the result.
        public double WinProbability(GameController game, EPlayerId seat)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            Player opponent = player.OpponentPlayer;
            int mine = player.Crowns;
            int theirs = opponent.Crowns;
            if (mine >= 2 || theirs >= 2)
            {
                return mine == theirs ? 0.5 : mine > theirs ? 1 : 0;
            }
            double[] features = Features(game, seat);
            if (mine == theirs)
            {
                // Symmetric: no side is favoured but by the features.
                Model even = m_Models["11"];
                return Logistic(Dot(even.Weights, features) + CardSum(even, game, seat));
            }
            Model ahead = m_Models["10"];
            if (mine > theirs)
            {
                return Logistic(ahead.Bias + Dot(ahead.Weights, features) + CardSum(ahead, game, seat));
            }
            return 1 - Logistic(ahead.Bias - Dot(ahead.Weights, features) - CardSum(ahead, game, seat));
        }

        double IRoundEvaluator.WinProbability(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered)
        {
            return WinProbability(game, seat);
        }

        public static double[] Features(GameController game, EPlayerId seat)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            EPlayerId other = player.OpponentPlayer.Id;
            double[] mine = Side(game, seat);
            double[] theirs = Side(game, other);
            double[] features = new double[Names.Length];
            for (int i = 0; i < features.Length; i++)
            {
                features[i] = mine[i] - theirs[i];
            }
            features[Names.Length - 1] = game.PlayerManager.CurrentPlayerId == seat ? 1 : -1;
            return features;
        }

        private static double[] Side(GameController game, EPlayerId id)
        {
            Player player = game.PlayerManager.GetPlayer(id);
            List<Card> hand = player.GetCardsInLocation(ELocation.Hand);
            return new[]
            {
                hand.Count,
                hand.Count(card => card.Template.Tier == ECardTier.Gold),
                hand.Count(card => card.Template.Tier == ECardTier.Silver),
                hand.Sum(card => card.Power) / 10.0,
                player.CurrentScore / 10.0,
                // A played leader leaves an unplayable copy in its slot.
                player.GetCardsInLocation(ELocation.Leader, includeOnlyCardsThatCanBePlayed: true).Count,
                player.GetCardsInLocation(ELocation.Deck).Count / 5.0,
                0,
            };
        }

        // The card weights of the seat's hand minus the opponent's.
        private static double CardSum(Model model, GameController game, EPlayerId seat)
        {
            if (model.Cards == null)
            {
                return 0;
            }
            Player player = game.PlayerManager.GetPlayer(seat);
            return HandValue(model.Cards, player) - HandValue(model.Cards, player.OpponentPlayer);
        }

        internal static double HandValue(Dictionary<int, double> cards, Player player)
        {
            double sum = 0;
            foreach (Card card in player.GetCardsInLocation(ELocation.Hand)
                         .Concat(player.GetCardsInLocation(ELocation.Leader, includeOnlyCardsThatCanBePlayed: true)))
            {
                if (cards.TryGetValue(card.Template.Id, out double value))
                {
                    sum += value;
                }
            }
            return sum;
        }

        private static double Dot(double[] weights, double[] features)
        {
            double sum = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i] * features[i];
            }
            return sum;
        }

        private static double Logistic(double x)
        {
            return 1 / (1 + Math.Exp(-x));
        }
    }
}
