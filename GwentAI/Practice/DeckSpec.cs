using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Practice
{
    // A deck as its JSON file holds it (the roster's deck files, the decks
    // the search workers receive): the leader and the cards by template id.
    public class DeckSpec
    {
        public string Name;
        public string Faction;
        public CardRef Leader;
        public List<CardRef> Cards = new List<CardRef>();
        // How often the deck is met, relative to others of its layer: the
        // belief's prior weight.
        public double Weight = 1;
        // Deck population fields (the roster tool): the layer (meta,
        // netdeck, low or meme) and an optional profile name: a deck tagged
        // with the local profile's name is never in the AI's prior when that
        // profile plays.
        public string Kind;
        public string Owner;

        public BattleDeck ToBattleDeck()
        {
            List<CardDefinition> cards = new List<CardDefinition>();
            foreach (CardRef card in Cards)
            {
                for (int i = 0; i < card.Count; i++)
                {
                    cards.Add(new CardDefinition(card.Id, isPremium: false));
                }
            }
            EFactionId faction = (EFactionId)System.Enum.Parse(typeof(EFactionId), Faction);
            return new BattleDeck(faction, new CardDefinition(Leader.Id, isPremium: false), cards, Name);
        }
    }

    public class CardRef
    {
        public int Id;
        public string Name;
        public int Count = 1;
    }
}
