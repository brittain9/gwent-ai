using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using GwentGameplay;

namespace GwentAI.Search
{
    // The chance that a seat wins the match from a turn start inside a
    // round: the round evaluator's view (cards, golds, carryover, leader,
    // deck) plus what the round itself has come to (the score so far, who
    // has passed). It lets a simulation stop a few turns past the search
    // tree (early playout termination; Lorentz 2016) instead of playing the
    // round out. Fitted offline on turn records of self-play games.
    public class MidEvaluator
    {
        public static readonly string[] Names =
        {
            "hand", "gold", "silver", "hand_power", "score", "leader", "deck", "mover",
            "i_passed",          // the seat has passed this round
            "they_passed",       // the opponent has
            "lead_they_passed",  // score lead (in tens) when the opponent has passed
            "lead_i_passed",     // score lead (in tens) when the seat has passed
        };

        public class Model
        {
            public double Bias;
            public double[] Weights;
            public Dictionary<int, double> Cards;
        }

        // By crowns, from the seat's side: "00", "10" (the seat leads), "11".
        // "01" is "10" from the other side.
        private readonly Dictionary<string, Model> m_Models;

        public MidEvaluator(Dictionary<string, Model> models)
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

        // The weights' source text, kept for search workers (see Evaluator.Json).
        public string Json { get; private set; }

        public static MidEvaluator Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        public static MidEvaluator Parse(string json)
        {
            return new MidEvaluator(JsonConvert.DeserializeObject<Dictionary<string, Model>>(json)) { Json = json };
        }

        public double WinProbability(GameController game, EPlayerId seat)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            Player opponent = player.OpponentPlayer;
            if (player.Crowns >= 2 || opponent.Crowns >= 2)
            {
                return player.Crowns == opponent.Crowns ? 0.5 : player.Crowns > opponent.Crowns ? 1 : 0;
            }
            if (player.Crowns < opponent.Crowns)
            {
                return 1 - Score(m_Models["10"], game, opponent.Id);
            }
            string state = player.Crowns > opponent.Crowns ? "10" : player.Crowns == 1 ? "11" : "00";
            return Score(m_Models[state], game, seat);
        }

        private static double Score(Model model, GameController game, EPlayerId seat)
        {
            double[] x = Features(game, seat);
            double z = model.Bias;
            for (int i = 0; i < x.Length; i++)
            {
                z += model.Weights[i] * x[i];
            }
            if (model.Cards != null)
            {
                Player player = game.PlayerManager.GetPlayer(seat);
                z += Evaluator.HandValue(model.Cards, player) - Evaluator.HandValue(model.Cards, player.OpponentPlayer);
            }
            return 1 / (1 + Math.Exp(-z));
        }

        // From the seat's side; must match the offline fit.
        public static double[] Features(GameController game, EPlayerId seat)
        {
            double[] round = Evaluator.Features(game, seat);
            Player player = game.PlayerManager.GetPlayer(seat);
            Player opponent = player.OpponentPlayer;
            double lead = (player.CurrentScore - opponent.CurrentScore) / 10.0;
            double[] x = new double[Names.Length];
            Array.Copy(round, x, round.Length);
            x[8] = player.HasPassed ? 1 : 0;
            x[9] = opponent.HasPassed ? 1 : 0;
            x[10] = opponent.HasPassed ? lead : 0;
            x[11] = player.HasPassed ? lead : 0;
            return x;
        }
    }
}
