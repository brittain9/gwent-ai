using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace GwentAI.Practice
{
    // The deck population (the roster tool) as Practice uses it: the
    // decks the AI plays, dealt like a ladder deals opponents, and the decks
    // its belief expects people to play.
    public class Roster
    {
        // The layers file (GwentAI-layers.json, beside the roster):
        // each layer's share of games, and the layers of the AI's prior.
        private class Layers
        {
            public Dictionary<string, double> Shares = new Dictionary<string, double>();
            public List<string> Prior = new List<string>();
            // Optional: the only decks the AI may play (names), e.g. the
            // tier 1-2 set. The prior keeps the whole roster. Null: every roster deck.
            public List<string> AiDecks;
        }

        private readonly List<DeckSpec> m_Decks;
        private readonly Layers m_Layers;
        private string m_Last;

        public int Count => m_Decks.Count;

        private Roster(List<DeckSpec> decks, Layers layers)
        {
            m_Decks = decks;
            m_Layers = layers;
        }

        // Every deck file in the directory, with the layers file; empty when
        // either is missing.
        public static Roster Load(string directory, string layersPath)
        {
            if (!Directory.Exists(directory) || !File.Exists(layersPath))
            {
                return new Roster(new List<DeckSpec>(), new Layers());
            }
            return new Roster(Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal)
                                  .Select(path => JsonConvert.DeserializeObject<DeckSpec>(File.ReadAllText(path)))
                                  .ToList(),
                              JsonConvert.DeserializeObject<Layers>(File.ReadAllText(layersPath)));
        }

        // The prior layers: what a player knows people play. A deck tagged
        // with the local profile's name (DeckSpec.Owner) is never in the
        // prior when that profile plays.
        public List<DeckSpec> Prior(string human)
        {
            return m_Decks.Where(deck => m_Layers.Prior.Contains(deck.Kind) && !Owned(deck, human)).ToList();
        }

        // An opponent deck: a layer by its share, a deck by its weight within
        // the layer; never a deck tagged with the playing profile's name or
        // the previous opponent.
        // Null when no deck qualifies (the client's own deck then stays).
        public DeckSpec Draw(Random random, string human)
        {
            List<DeckSpec> open = m_Decks.Where(deck => !Owned(deck, human) && deck.Name != m_Last && m_Layers.Shares.ContainsKey(deck.Kind ?? "") &&
                                                    (m_Layers.AiDecks == null || m_Layers.AiDecks.Contains(deck.Name))).ToList();
            if (open.Count == 0)
            {
                return null;
            }
            Dictionary<string, double> totals = open.GroupBy(deck => deck.Kind).ToDictionary(group => group.Key, group => group.Sum(deck => deck.Weight));
            double pick = random.NextDouble() * totals.Keys.Sum(kind => m_Layers.Shares[kind]);
            foreach (DeckSpec deck in open)
            {
                pick -= m_Layers.Shares[deck.Kind] * deck.Weight / totals[deck.Kind];
                if (pick < 0)
                {
                    m_Last = deck.Name;
                    return deck;
                }
            }
            m_Last = open[open.Count - 1].Name;
            return open[open.Count - 1];
        }

        private static bool Owned(DeckSpec deck, string human)
        {
            return deck.Owner != null && string.Equals(deck.Owner, human, StringComparison.OrdinalIgnoreCase);
        }
    }
}
