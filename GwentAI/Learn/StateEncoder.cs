using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GwentGameplay;

namespace GwentAI.Learn
{
    // One seat's view of a game, as raw integers: what the value net reads
    // (ValueNet), and that code driving the AI can record. The net's input
    // layer turns these integers into floats.
    //
    // Fair by construction: the seat's own hand, deck (as a set: its order
    // is never read), graveyard and leader; both graveyards; the face-up
    // board; and of the opponent's hidden cards only those the seat sees now
    // (Card.IsVisibleTo) or remembers (Belief.Remember). Everything else is a
    // count. A face-down card on the board is "hidden" (template 0, no
    // power), so a determinized copy and the live game encode alike.
    public sealed class EncodedState
    {
        public readonly int[] Globals = new int[StateEncoder.GlobalCount];
        public int CardCount;
        // Per card: StateEncoder.CardFields integers (zone, template, ...).
        public int[] Cards = new int[160 * StateEncoder.CardFields];

        internal void Add(int zone, int template, int power, int basePower, int armor, int tokens, int position)
        {
            int at = CardCount * StateEncoder.CardFields;
            if (at + StateEncoder.CardFields > Cards.Length)
            {
                int[] grown = new int[Cards.Length * 2];
                Array.Copy(Cards, grown, Cards.Length);
                Cards = grown;
            }
            Cards[at] = zone;
            Cards[at + 1] = template;
            Cards[at + 2] = power;
            Cards[at + 3] = basePower;
            Cards[at + 4] = armor;
            Cards[at + 5] = tokens;
            Cards[at + 6] = position;
            CardCount++;
        }

        // {"g":[...],"c":[...]}: globals, then CardFields integers per card.
        public void WriteJson(StringBuilder text)
        {
            text.Append("\"g\":[");
            for (int i = 0; i < Globals.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }
                text.Append(Globals[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append("],\"c\":[");
            int count = CardCount * StateEncoder.CardFields;
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    text.Append(',');
                }
                text.Append(Cards[i].ToString(CultureInfo.InvariantCulture));
            }
            text.Append(']');
        }
    }

    public static class StateEncoder
    {
        // Bump on any change to what is encoded or how; weights carry it and
        // are refused on a mismatch.
        public const int Version = 1;

        // Card zones, from the seat's side.
        public const int OwnHand = 0, OwnDeck = 1, OwnGraveyard = 2, OpponentGraveyard = 3;
        public const int OwnMelee = 4, OwnRanged = 5, OwnSiege = 6, OpponentMelee = 7, OpponentRanged = 8, OpponentSiege = 9;
        public const int OpponentKnownHand = 10, OwnLeader = 11, OpponentLeader = 12;
        public const int ZoneCount = 13;

        // Per card: zone, template id (0: hidden), power, base power, armour,
        // tokens (ECardToken bits), position in its row (0 off the board).
        public const int CardFields = 7;

        // Globals, by index.
        public const int Round = 0;                 // 3, one-hot by round
        public const int Crowns = 3;                // own, opponent
        public const int Started = 5;               // the seat started this round
        public const int ToAct = 6;                 // the seat is the current player
        public const int Passed = 7;                // own, opponent
        public const int RowScores = 9;             // own melee, ranged, siege, then the opponent's
        public const int Totals = 15;               // own, opponent
        public const int HandCounts = 17;           // own, opponent
        public const int DeckCounts = 19;           // own, opponent
        public const int GraveyardCounts = 21;      // own, opponent
        public const int LeaderUnused = 23;         // own, opponent
        public const int RoundStart = 25;           // 1 at a round start, 0 at a turn start
        public const int TurnIndex = 26;            // turns begun this round
        public const int Factions = 27;             // 6 own, 6 opponent, one-hot by FactionBits
        public const int RowTokens = 39;            // 6 rows x 13 ELocationToken bits, rows as RowScores
        public const int MaxPower = 117;            // highest unit power per row, rows as RowScores
        public const int Lead = 123;                // own total minus the opponent's
        public const int GlobalCount = 124;

        public static readonly EFactionId[] FactionBits =
        {
            EFactionId.Neutral, EFactionId.Monsters, EFactionId.Nilfgaard, EFactionId.NorthernRealms, EFactionId.Scoiatael, EFactionId.Skellige,
        };
        public const int LocationTokenBits = 13;    // Frost .. FireSP

        // Fills `into` with the seat's view. Remembered: opponent cards the
        // seat saw, by instance id (Belief.Remember); may be null.
        public static void Encode(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered, bool roundStart, EncodedState into)
        {
            Encode(game, seat, remembered, roundStart, into, false);
        }

        // fullState: the full-state view a full-state value net reads at a
        // determinized leaf. The opponent's
        // whole hand is listed in OpponentKnownHand with full card data, and
        // the seat's deck contents are left out (its count stays), so the
        // view and its Mirror hold the same information.
        public static void Encode(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered, bool roundStart, EncodedState into,
                                  bool fullState)
        {
            BoardManager board = game.CardManager.BoardManager;
            Player player = game.PlayerManager.GetPlayer(seat);
            Player opponent = player.OpponentPlayer;
            BoardSide own = board.BoardSides[seat.ToIndex()];
            BoardSide other = board.BoardSides[opponent.Id.ToIndex()];
            int[] g = into.Globals;
            Array.Clear(g, 0, g.Length);
            into.CardCount = 0;

            int round = game.RoundManager.CurrentRoundIndex;
            if (round >= 0 && round < 3)
            {
                g[Round + round] = 1;
            }
            g[Crowns] = player.Crowns;
            g[Crowns + 1] = opponent.Crowns;
            RoundInfo current = game.RoundManager.CurrentRound;
            g[Started] = current != null && current.StartingPlayerId == seat ? 1 : 0;
            g[ToAct] = game.PlayerManager.CurrentPlayerId == seat ? 1 : 0;
            g[Passed] = player.HasPassed ? 1 : 0;
            g[Passed + 1] = opponent.HasPassed ? 1 : 0;
            g[Totals] = player.CurrentScore;
            g[Totals + 1] = opponent.CurrentScore;
            g[Lead] = player.CurrentScore - opponent.CurrentScore;
            g[RoundStart] = roundStart ? 1 : 0;
            g[TurnIndex] = current != null ? current.TurnIndex : 0;
            g[Factions + FactionIndex(player.FactionId)] = 1;
            g[Factions + 6 + FactionIndex(opponent.FactionId)] = 1;

            // Hand: own cards known; the opponent's counted, and listed when
            // seen or remembered.
            g[HandCounts] = AddZone(into, own.Hand, OwnHand, seat, null, Known.All, false);
            g[HandCounts + 1] = AddZone(into, other.Hand, OpponentKnownHand, seat, remembered, fullState ? Known.All : Known.SeenOnly, false);
            g[DeckCounts] = fullState ? Count(own.Deck) : AddZone(into, own.Deck, OwnDeck, seat, null, Known.All, false);
            g[DeckCounts + 1] = Count(other.Deck);
            g[GraveyardCounts] = AddZone(into, own.Graveyard, OwnGraveyard, seat, null, Known.Visible, false);
            g[GraveyardCounts + 1] = AddZone(into, other.Graveyard, OpponentGraveyard, seat, null, Known.Visible, false);
            g[LeaderUnused] = AddLeader(into, own.Leader, OwnLeader);
            g[LeaderUnused + 1] = AddLeader(into, other.Leader, OpponentLeader);

            for (int r = 0; r < 6; r++)
            {
                BoardSide side = r < 3 ? own : other;
                Location row = r % 3 == 0 ? side.Melee : r % 3 == 1 ? side.Ranged : side.Siege;
                g[RowScores + r] = row.GetScore();
                int tokens = (int)row.Token;
                for (int bit = 0; bit < LocationTokenBits; bit++)
                {
                    g[RowTokens + r * LocationTokenBits + bit] = (tokens >> bit) & 1;
                }
                AddZone(into, row, OwnMelee + r, seat, null, Known.Visible, true);
                g[MaxPower + r] = MaxVisiblePower(row, seat);
            }
        }

        // The zone each zone becomes when the view is seen from the other side.
        private static readonly int[] MirrorZones = { OpponentKnownHand, OwnDeck, OpponentGraveyard, OwnGraveyard, OpponentMelee, OpponentRanged,
                                                      OpponentSiege, OwnMelee, OwnRanged, OwnSiege, OwnHand, OpponentLeader, OwnLeader };

        // A full-state view seen from the opponent's side: zones and globals
        // swapped side for side (the fitting mirrors alike).
        public static void Mirror(EncodedState from, EncodedState into)
        {
            int[] g = from.Globals, m = into.Globals;
            Array.Copy(g, m, g.Length);
            Swap(g, m, Crowns, 1);
            Swap(g, m, Passed, 1);
            Swap(g, m, RowScores, 3);
            Swap(g, m, Totals, 1);
            Swap(g, m, HandCounts, 1);
            Swap(g, m, DeckCounts, 1);
            Swap(g, m, GraveyardCounts, 1);
            Swap(g, m, LeaderUnused, 1);
            Swap(g, m, Factions, 6);
            Swap(g, m, RowTokens, 3 * LocationTokenBits);
            Swap(g, m, MaxPower, 3);
            m[Started] = 1 - g[Started];
            m[ToAct] = 1 - g[ToAct];
            m[Lead] = -g[Lead];
            int count = from.CardCount * CardFields;
            if (into.Cards.Length < count)
            {
                into.Cards = new int[from.Cards.Length];
            }
            Array.Copy(from.Cards, into.Cards, count);
            for (int at = 0; at < count; at += CardFields)
            {
                into.Cards[at] = MirrorZones[from.Cards[at]];
            }
            into.CardCount = from.CardCount;
        }

        // Exchanges the block of `width` globals at `at` with the next one.
        private static void Swap(int[] from, int[] into, int at, int width)
        {
            for (int i = 0; i < width; i++)
            {
                into[at + i] = from[at + width + i];
                into[at + width + i] = from[at + i];
            }
        }

        private enum Known
        {
            All,        // the seat knows every card (its own hand, deck, leader)
            Visible,    // face-up cards; others are hidden
            SeenOnly,   // only cards seen or remembered are listed
        }

        // Adds the zone's cards; returns how many it holds.
        private static int AddZone(EncodedState into, Location location, int zone, EPlayerId seat, IDictionary<ushort, int> remembered, Known known,
                                   bool board)
        {
            List<Card> cards = location.Cards;
            int count = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                if (card == null || card.IsInExecutionStack || card.IsWaitingToDie)
                {
                    continue;
                }
                int position = board ? count : 0;
                count++;
                if (known == Known.All || card.IsVisibleTo(seat))
                {
                    CardData data = card.Data;
                    into.Add(zone, card.Template.Id, data.Power.CurrentPower, data.Power.BasePower, data.Power.CurrentArmor, (int)data.Tokens.Tokens, position);
                }
                else if (known == Known.SeenOnly)
                {
                    if (remembered != null && remembered.TryGetValue(card.Id, out int template))
                    {
                        // Its template only: what happened to it since is hidden.
                        into.Add(zone, template, 0, 0, 0, 0, 0);
                    }
                }
                else
                {
                    into.Add(zone, 0, 0, 0, 0, 0, position);
                }
            }
            return count;
        }

        private static int AddLeader(EncodedState into, Location leader, int zone)
        {
            List<Card> cards = leader.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                // A played leader leaves an unplayable copy in its slot.
                if (card != null && !card.IsInExecutionStack && !card.IsWaitingToDie && card.Data.CanBePlayed)
                {
                    into.Add(zone, card.Template.Id, card.Data.Power.CurrentPower, card.Data.Power.BasePower, card.Data.Power.CurrentArmor,
                             (int)card.Data.Tokens.Tokens, 0);
                    return 1;
                }
            }
            return 0;
        }

        private static int Count(Location location)
        {
            List<Card> cards = location.Cards;
            int count = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                if (card != null && !card.IsInExecutionStack && !card.IsWaitingToDie)
                {
                    count++;
                }
            }
            return count;
        }

        private static int MaxVisiblePower(Location row, EPlayerId seat)
        {
            int best = 0;
            List<Card> cards = row.Cards;
            for (int i = 0; i < cards.Count; i++)
            {
                Card card = cards[i];
                if (card != null && !card.IsInExecutionStack && !card.IsWaitingToDie && card.IsVisibleTo(seat) && card.Power > best)
                {
                    best = card.Power;
                }
            }
            return best;
        }

        private static int FactionIndex(EFactionId faction)
        {
            for (int i = 0; i < FactionBits.Length; i++)
            {
                if (FactionBits[i] == faction)
                {
                    return i;
                }
            }
            return 0;
        }
    }
}
