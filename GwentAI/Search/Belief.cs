using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Practice;
using GwentCore;
using GwentGameplay;

namespace GwentAI.Search
{
    // What one seat may believe about the cards it cannot see, and the
    // determinization that turns a copy of the game into one world consistent
    // with that belief.
    //
    // The seat knows its own hand and deck contents (not the deck order), the
    // board, both graveyards, every card revealed so far, and the opponent's
    // leader. It also knows the meta: the decks people commonly play. The
    // opponent's deck is guessed as one of the meta decks for their leader
    // that best fits the cards seen so far; with none, as a random legal deck
    // of their faction built around the cards seen.
    //
    // Fairness rule: nothing here reads a hidden card's template, and the
    // game settings (which hold both real deck lists) are read for the
    // opponent's leader and the observer's own deck only. Abilities that
    // read a starting deck list see the guess (SearchPatches). RevealHand
    // (a cheating reference) is the one exception.
    public class Belief
    {
        private readonly List<MetaDeck> m_Meta;
        // How much likelier a player keeps silvers (HandBias) and golds
        // (squared) in hand than bronzes; 1 is a uniform draw.
        public double HandBias = 1;
        // How much likelier a player keeps a gold or silver answer (card
        // role removal or weather, CardRoles) in hand than the hand bias
        // alone says: players hold Igni and Scorch for the round that
        // decides. 1: off. Reads only
        // the templates of the guessed deck's unseen rest.
        public double AnswerBias = 1;
        // The opponent's hidden hand holds golds and silvers as the recorded
        // games' players keep them, by round (OpponentStyle.HoldWeight, in place of
        // HandBias): the search's opponent model.
        public bool OpponentStyle;
        // With OpponentStyle, the self-play fitted hold weights in place of
        // the recorded games' (OpponentStyle.Select).
        public bool SelfPlayStyle;
        // The observer's own deck is reshuffled by moving its cards, which
        // keep their state (boosts, armour), instead of re-templating them
        // in place, which resets each card to its template's defaults: a
        // buffed deck card (Reaver Scout, Battering Ram) loses its buff in
        // every simulation. False: re-templates in place.
        public bool KeepDeckState;
        // The opponent's deck is drawn from a posterior over every meta deck
        // for their leader (none: every meta deck of their faction), each
        // weighted by its prior weight times MisfitWeight per seen card it
        // cannot hold, instead of from the best fits alone with a random
        // deck past UnknownDeckMismatches. False: the best fits alone.
        public bool PosteriorDecks;
        public const double MisfitWeight = 0.1;
        private static readonly System.Reflection.FieldInfo s_Blacklist = HarmonyLib.AccessTools.Field(typeof(RequestMulliganAction), "m_BlacklistedTemplates");
        private static readonly System.Reflection.FieldInfo s_CopiesCount = HarmonyLib.AccessTools.Field(typeof(HasNCopiesOfDefinitions), "Count");
        private static readonly Dictionary<int, int> s_CopyRules = new Dictionary<int, int>();
        private const int DeckGolds = 4;
        private const int DeckSilvers = 6;
        private const int BronzeCopies = 3;
        // Seen cards the best-fitting meta deck cannot hold before the
        // opponent's deck counts as unknown. One or two are tech swaps.
        private const int UnknownDeckMismatches = 3;
        // How much likelier a guessed card is to be from the faction than
        // neutral: real decks lean on their faction.
        private const int FactionWeight = 3;

        internal sealed class MetaDeck
        {
            public string Name;
            public string Faction;
            public int Leader;
            public double Weight;
            public Dictionary<int, int> Counts;
        }

        // The wire (WorkerMessage) rebuilds a belief from its public fields
        // alone; the decks it believes in travel as the message's Prior.
        [Newtonsoft.Json.JsonConstructor]
        public Belief(IEnumerable<DeckSpec> meta)
        {
            m_Meta = (meta ?? Enumerable.Empty<DeckSpec>()).Select(deck => new MetaDeck
            {
                Name = deck.Name,
                Faction = deck.Faction,
                Leader = deck.Leader.Id,
                Weight = deck.Weight,
                Counts = deck.Cards.GroupBy(card => card.Id).ToDictionary(group => group.Key, group => group.Sum(card => card.Count)),
            }).ToList();
        }

        // The cards from the opponent's deck the observer has seen, on
        // either side of the board (spies sit on the other side). A card's
        // origin is its instance id: the engine registers P1's leader and
        // deck, then P2's, before anything else, so each deck owns a range of
        // ids, and cards created during the game (Dagon's fog, Adda's cursed
        // units) come after both. Only the public deck sizes are read.
        public static Dictionary<int, int> KnownOpponentCards(GameController game, EPlayerId observer)
        {
            EPlayerId opponent = game.PlayerManager.GetOpponentPlayerId(observer);
            int first, last;
            DeckIds(game, opponent, out first, out last);
            Dictionary<int, int> known = new Dictionary<int, int>();
            const ELocation seen = ELocation.Hand | ELocation.Deck | ELocation.Melee | ELocation.Ranged | ELocation.Siege |
                                   ELocation.Graveyard | ELocation.Void;
            foreach (Card card in game.CardManager.BoardManager.GetCardsInLocation(EPlayerId.P1 | EPlayerId.P2, seen))
            {
                CardTemplate template = card.Template;
                if (card.IsVisibleTo(observer) && card.Id >= first && card.Id <= last && template.Availability != EAvailability.NonOwnable &&
                    template.Tier != ECardTier.Leader)
                {
                    known.TryGetValue(template.Id, out int count);
                    known[template.Id] = count + 1;
                }
            }
            return known;
        }

        // The opponent's deck cards the observer knows, by template: those it
        // sees now (KnownOpponentCards) and the hidden ones it saw before or
        // an open choice shows it (recalled, by card). What the belief fits
        // meta decks against.
        public static Dictionary<int, int> Known(GameController game, EPlayerId observer, IDictionary<ushort, int> remembered, Dictionary<ushort, int> shown,
                                                 out Dictionary<Card, int> recalled)
        {
            EPlayerId opponent = game.PlayerManager.GetOpponentPlayerId(observer);
            Dictionary<int, int> known = KnownOpponentCards(game, observer);
            recalled = new Dictionary<Card, int>();
            if (remembered != null || shown.Count > 0)
            {
                foreach (Card card in game.CardManager.BoardManager.GetCardsInLocation(opponent, ELocation.Hand | ELocation.Deck | ELocation.Melee | ELocation.Ranged | ELocation.Siege))
                {
                    if (!card.IsVisibleTo(observer) && (shown.TryGetValue(card.Id, out int seen) || (remembered != null && remembered.TryGetValue(card.Id, out seen))))
                    {
                        recalled[card] = seen;
                        known.TryGetValue(seen, out int count);
                        known[seen] = count + 1;
                    }
                }
            }
            return known;
        }

        // Recalls the opponent's hidden hand and face-down board cards by their
        // real templates, as Known does for those the observer remembers. Only
        // opponent deck cards (ownable, not the leader) join the known counts
        // the deck guess fits against; a card created in the game is recalled
        // but not counted, as the observer's own memory treats it.
        private static void RevealHand(GameController game, EPlayerId observer, Dictionary<int, int> known, Dictionary<Card, int> recalled)
        {
            EPlayerId opponent = game.PlayerManager.GetOpponentPlayerId(observer);
            DeckIds(game, opponent, out int first, out int last);
            foreach (Card card in game.CardManager.BoardManager.GetCardsInLocation(opponent, ELocation.Hand | ELocation.Melee | ELocation.Ranged | ELocation.Siege))
            {
                if (card.IsVisibleTo(observer) || recalled.ContainsKey(card))
                {
                    continue;
                }
                CardTemplate template = card.Template;
                recalled[card] = template.Id;
                if (card.Id >= first && card.Id <= last && template.Availability != EAvailability.NonOwnable && template.Tier != ECardTier.Leader)
                {
                    known.TryGetValue(template.Id, out int count);
                    known[template.Id] = count + 1;
                }
            }
        }

        // Adds the opponent's deck cards the observer sees now to what it
        // remembers, by instance id: a player remembers a card that went
        // back to its owner's hand or deck (Ciri: Dash, a revealed card).
        public static void Remember(GameController game, EPlayerId observer, IDictionary<ushort, int> seen)
        {
            EPlayerId opponent = game.PlayerManager.GetOpponentPlayerId(observer);
            DeckIds(game, opponent, out int first, out int last);
            const ELocation anywhere = ELocation.Hand | ELocation.Deck | ELocation.Melee | ELocation.Ranged | ELocation.Siege |
                                       ELocation.Graveyard | ELocation.Void;
            foreach (Card card in game.CardManager.BoardManager.GetCardsInLocation(EPlayerId.P1 | EPlayerId.P2, anywhere))
            {
                CardTemplate template = card.Template;
                if (card.IsVisibleTo(observer) && card.Id >= first && card.Id <= last && template.Availability != EAvailability.NonOwnable &&
                    template.Tier != ECardTier.Leader)
                {
                    seen[card.Id] = template.Id;
                }
            }
            foreach (KeyValuePair<ushort, int> shown in Shown(game, observer))
            {
                if (shown.Key >= first && shown.Key <= last)
                {
                    seen[shown.Key] = shown.Value;
                }
            }
        }

        // The hidden cards an open choice shows the observer, by instance id:
        // a pick from its own deck (Wild Hunt Navigator, a Shupe mode, a
        // tutor), or a revealed pick among the opponent's (Xarthisius).
        // The choice shows copies, but a pick resolves to the original
        // instance (RequestCardChoicesAction.OriginalChoices), so those
        // instances must keep the template shown, or a simulated "choose
        // Rider" plays whatever card the reshuffle put there. The engine's
        // own rule decides what is shown: the chooser's own cards, or any
        // card when the choice is revealed.
        public static Dictionary<ushort, int> Shown(GameController game, EPlayerId observer)
        {
            Dictionary<ushort, int> shown = new Dictionary<ushort, int>();
            foreach (ARequestAction request in game.RequestManager.Requests)
            {
                if (!(request is RequestCardChoicesAction choices) || choices.TargetPlayer != observer || choices.PlayerId != observer ||
                    choices.OriginalChoices.Count != choices.ValidChoices.Count)
                {
                    continue;
                }
                for (int i = 0; i < choices.OriginalChoices.Count; i++)
                {
                    Card original = game.CardManager.GetCard(choices.OriginalChoices[i]);
                    if (original != null && (original.GetPlayerId() == observer || choices.RevealChoices))
                    {
                        shown[original.Id] = choices.ValidChoices[i].Definition.TemplateId;
                    }
                }
            }
            return shown;
        }

        // The meta decks the opponent may be playing: those for their leader
        // with the fewest seen cards they cannot hold. Empty when no meta
        // deck has their leader.
        public List<string> Candidates(GameController game, EPlayerId observer, out int mismatches)
        {
            List<MetaDeck> best = BestFits(game, observer, KnownOpponentCards(game, observer), out mismatches);
            return best.Select(deck => deck.Name).ToList();
        }

        private List<MetaDeck> BestFits(GameController game, EPlayerId observer, Dictionary<int, int> known, out int mismatches)
        {
            int leader = OpponentLeader(game, observer);
            List<MetaDeck> best = new List<MetaDeck>();
            mismatches = int.MaxValue;
            foreach (MetaDeck deck in m_Meta)
            {
                if (deck.Leader != leader)
                {
                    continue;
                }
                int misses = Excess(known, deck.Counts).Values.Sum();
                if (misses < mismatches)
                {
                    best.Clear();
                    mismatches = misses;
                }
                if (misses == mismatches)
                {
                    best.Add(deck);
                }
            }
            if (best.Count == 0)
            {
                mismatches = 0;
            }
            return best;
        }

        // The instance ids of a player's leader and deck: P1 holds 1 to
        // n1 + 1, P2 the next n2 + 1.
        private static void DeckIds(GameController game, EPlayerId player, out int first, out int last)
        {
            int p1 = DeckSize(game, EPlayerId.P1);
            first = player == EPlayerId.P1 ? 1 : p1 + 2;
            last = player == EPlayerId.P1 ? p1 + 1 : p1 + DeckSize(game, EPlayerId.P2) + 2;
        }

        // Both players see each deck's size.
        private static int DeckSize(GameController game, EPlayerId player)
        {
            return game.GameInstance.Settings.GetPlayerSettings(player).Deck.Cards.Count;
        }

        // A list drawn from the posterior (PosteriorDecks): the meta decks
        // for the opponent's leader, or, with none, those of its faction,
        // each weighted by how often it is met times MisfitWeight per known
        // card it cannot hold. Null with no candidate.
        private static MetaDeck PosteriorPick(Preparation prepared, Random random, out int mismatches)
        {
            mismatches = 0;
            List<MetaDeck> candidates = prepared.Candidates;
            if (candidates.Count == 0)
            {
                return null;
            }
            double pick = random.NextDouble() * prepared.WeightSum;
            for (int i = 0; i < candidates.Count; i++)
            {
                pick -= prepared.Weights[i];
                if (pick < 0)
                {
                    mismatches = prepared.Misses[i];
                    return candidates[i];
                }
            }
            mismatches = prepared.Misses[candidates.Count - 1];
            return candidates[candidates.Count - 1];
        }

        private static string Normalized(string faction)
        {
            return new string(faction.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        }

        // A best-fitting list, by how often it is met.
        private static MetaDeck Pick(List<MetaDeck> fits, Random random)
        {
            double total = fits.Sum(deck => deck.Weight);
            double pick = random.NextDouble() * total;
            foreach (MetaDeck deck in fits)
            {
                pick -= deck.Weight;
                if (pick < 0)
                {
                    return deck;
                }
            }
            return fits[fits.Count - 1];
        }

        private static int OpponentLeader(GameController game, EPlayerId observer)
        {
            EPlayerId opponent = game.PlayerManager.GetOpponentPlayerId(observer);
            return game.GameInstance.Settings.GetPlayerSettings(opponent).Deck.Leader.TemplateId;
        }

        // Counts in a that b does not cover.
        private static Dictionary<int, int> Excess(Dictionary<int, int> a, Dictionary<int, int> b)
        {
            Dictionary<int, int> excess = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> entry in a)
            {
                b.TryGetValue(entry.Key, out int covered);
                if (entry.Value > covered)
                {
                    excess[entry.Key] = entry.Value - covered;
                }
            }
            return excess;
        }

        // Replaces everything the observer cannot see in a copy of the game
        // with a sample from the belief:
        // - the opponent's unseen hand, deck and face-down (ambush) cards
        //   become the unseen rest of a guessed deck, in random order;
        // - the observer's own deck is reshuffled;
        // - the random generator is re-seeded, since a copied generator
        //   would foretell draws and random effects.
        // Remembered (see Remember): hidden opponent cards the observer saw
        // before keep the template it saw.
        public Determinization Determinize(GameController copy, EPlayerId observer, Random random, IDictionary<ushort, int> remembered = null)
        {
            return Determinize(copy, observer, random, Prepare(copy, observer, remembered));
        }

        // What the observer's view of a game fixes for all the
        // determinizations of its copies, read once (Prepare): a search
        // copies one live game again and again, and a copy holds the live
        // game's state when it is determinized.
        public sealed class Preparation
        {
            internal Dictionary<ushort, int> Shown;
            internal Dictionary<int, int> Known;
            // The hidden opponent cards the observer remembers or is shown
            // (Known's recalled), by instance id.
            internal Dictionary<ushort, int> Recalled;
            // The best-fitting meta decks (empty past UnknownDeckMismatches)
            // and the seen cards they cannot hold; or, with PosteriorDecks,
            // the candidates, their weights and the weights' sum.
            internal List<MetaDeck> Fits;
            internal int Mismatches;
            internal List<MetaDeck> Candidates;
            internal int[] Misses;
            internal double[] Weights;
            internal double WeightSum;
            // The observer's own deck cards' states, sorted (CardStates).
            internal CardState[] OwnStates;
            // CopyRule, when a determinization first needs it.
            internal int? BronzeRule;
        }

        // revealHand (SearchSettings.HandOracle, a cheating reference): the
        // opponent's hidden hand and face-down cards count as recalled with
        // their real templates, so every determinization keeps them and fits
        // the guessed deck around them. The deck is still a sample.
        public Preparation Prepare(GameController game, EPlayerId observer, IDictionary<ushort, int> remembered, bool revealHand = false)
        {
            Preparation prepared = new Preparation { Shown = Shown(game, observer) };
            prepared.Known = Known(game, observer, remembered, prepared.Shown, out Dictionary<Card, int> recalled);
            if (revealHand)
            {
                RevealHand(game, observer, prepared.Known, recalled);
            }
            prepared.Recalled = recalled.ToDictionary(entry => entry.Key.Id, entry => entry.Value);
            if (PosteriorDecks)
            {
                int leader = OpponentLeader(game, observer);
                prepared.Candidates = m_Meta.Where(deck => deck.Leader == leader).ToList();
                if (prepared.Candidates.Count == 0)
                {
                    string faction = Normalized(game.PlayerManager.GetPlayer(game.PlayerManager.GetOpponentPlayerId(observer)).FactionId.ToString());
                    prepared.Candidates = m_Meta.Where(deck => deck.Faction != null && Normalized(deck.Faction) == faction).ToList();
                }
                prepared.Misses = prepared.Candidates.Select(deck => Excess(prepared.Known, deck.Counts).Values.Sum()).ToArray();
                prepared.Weights = prepared.Candidates.Select((deck, i) => deck.Weight * Math.Pow(MisfitWeight, prepared.Misses[i])).ToArray();
                prepared.WeightSum = prepared.Weights.Sum();
            }
            else
            {
                prepared.Fits = BestFits(game, observer, prepared.Known, out prepared.Mismatches);
            }
            prepared.OwnStates = CardStates(game.CardManager.BoardManager.GetLocation(observer, ELocation.Deck).Cards);
            return prepared;
        }

        public Determinization Determinize(GameController copy, EPlayerId observer, Random random, Preparation prepared)
        {
            BoardManager board = copy.CardManager.BoardManager;
            EPlayerId opponent = copy.PlayerManager.GetOpponentPlayerId(observer);
            Dictionary<ushort, int> shown = prepared.Shown;
            Dictionary<int, int> known = prepared.Known;
            MetaDeck guess;
            int mismatches;
            if (PosteriorDecks)
            {
                guess = PosteriorPick(prepared, random, out mismatches);
            }
            else
            {
                mismatches = prepared.Mismatches;
                // Past UnknownDeckMismatches: not a deck the observer knows,
                // a variant or a home brew.
                guess = prepared.Fits.Count > 0 && mismatches < UnknownDeckMismatches ? Pick(prepared.Fits, random) : null;
            }
            Determinization result = new Determinization { Mismatches = mismatches };
            Dictionary<int, int> deck;
            if (guess != null)
            {
                result.Deck = guess.Name;
                deck = guess.Counts;
            }
            else
            {
                result.Deck = null;
                prepared.BronzeRule = prepared.BronzeRule ?? CopyRule(copy, known);
                deck = RandomDeck(copy.PlayerManager.GetPlayer(opponent).FactionId, DeckSize(copy, opponent), known, prepared.BronzeRule.Value, random);
            }
            List<int> unseen = Expand(Excess(deck, known));
            Shuffle(unseen, random);

            List<KeyValuePair<Card, int>> plan = new List<KeyValuePair<Card, int>>();
            List<Card> hidden = new List<Card>();
            List<Card> faceDown = new List<Card>();
            foreach (Card card in board.GetCardsInLocation(opponent, ELocation.Hand | ELocation.Deck | ELocation.Melee | ELocation.Ranged | ELocation.Siege))
            {
                if (prepared.Recalled.TryGetValue(card.Id, out int seen))
                {
                    plan.Add(new KeyValuePair<Card, int>(card, seen));
                }
                else if (!card.IsVisibleTo(observer))
                {
                    (card.Position.Location == ELocation.Hand || card.Position.Location == ELocation.Deck ? hidden : faceDown).Add(card);
                }
            }
            // Face-down cards are ambushes, so their guesses come from the
            // ambush cards left unseen first.
            foreach (Card card in faceDown)
            {
                int index = unseen.FindIndex(id => Template(id).IsAmbush);
                int template;
                if (index >= 0)
                {
                    template = unseen[index];
                    unseen.RemoveAt(index);
                }
                else
                {
                    template = RandomTemplate(copy.PlayerManager.GetPlayer(opponent).FactionId, random, PoolKind.Ambush);
                    result.Invented++;
                }
                plan.Add(new KeyValuePair<Card, int>(card, template));
            }
            // The hand first: with a hand bias, players' hands hold their
            // golds and silvers more often than a uniform draw would. The
            // opponent style draws its weighted picks from its own generator
            // (SplitMix64), so its fitted weights are realized.
            SplitMix64 style = OpponentStyle ? new SplitMix64(random.Next()) : null;
            foreach (Card card in hidden.Where(c => c.Position.Location == ELocation.Hand).Concat(hidden.Where(c => c.Position.Location != ELocation.Hand)))
            {
                int template;
                if (unseen.Count > 0)
                {
                    int index = card.Position.Location == ELocation.Hand && (HandBias != 1 || AnswerBias != 1 || OpponentStyle)
                        ? Weighted(unseen, style != null ? style.NextDouble() : random.NextDouble(), copy.RoundManager.CurrentRoundIndex) : unseen.Count - 1;
                    template = unseen[index];
                    unseen.RemoveAt(index);
                }
                else
                {
                    // More hidden cards than the guess leaves: effects added
                    // cards to the opponent's hand or deck.
                    template = RandomTemplate(copy.PlayerManager.GetPlayer(opponent).FactionId, random, PoolKind.Bronze);
                    result.Invented++;
                }
                plan.Add(new KeyValuePair<Card, int>(card, template));
            }
            result.Hidden = hidden.Count + faceDown.Count + prepared.Recalled.Count;
            Location ownDeck = board.GetLocation(observer, ELocation.Deck);
            if (!KeepDeckState)
            {
                plan.AddRange(ReshuffleOwnDeck(board.GetCardsInLocation(observer, ELocation.Deck), shown, random));
            }
            result.Changed = Transform(copy, plan);
            if (KeepDeckState)
            {
                ReorderOwnDeck(ownDeck, shown, random);
            }
            result.DeckStatesLost = Lost(prepared.OwnStates, CardStates(ownDeck.Cards));
            // The templates the opponent mulliganed away in an open mulligan
            // are theirs to know.
            foreach (ARequestAction request in copy.RequestManager.Requests)
            {
                if (request is RequestMulliganAction mulligan && request.PlayerId == opponent)
                {
                    ((HashSet<int>)s_Blacklist.GetValue(mulligan)).Clear();
                }
            }
            SyncDeckState(copy, observer);
            SyncDeckState(copy, opponent);
            BattleDeck[] initial = new BattleDeck[2];
            initial[observer.ToIndex()] = copy.GameInstance.Settings.GetPlayerSettings(observer).Deck;
            initial[opponent.ToIndex()] = ToBattleDeck(deck, copy.PlayerManager.GetPlayer(opponent).FactionId,
                                                       OpponentLeader(copy, observer), result.Deck);
            SearchPatches.OverrideInitialDecks(copy, initial);
            copy.RandomGenerator.Copy(new MersenneTwisterRandom(random.Next()));
            return result;
        }

        // The player's current deck list, which some abilities read, made to
        // match the deck's cards after transforms.
        private static void SyncDeckState(GameController copy, EPlayerId id)
        {
            BattleDeck state = copy.PlayerManager.GetPlayer(id).CurrentDeckState;
            if (state == null)
            {
                return;
            }
            state.Cards.Clear();
            foreach (Card card in copy.CardManager.BoardManager.GetCardsInLocation(id, ELocation.Deck))
            {
                state.Cards.Add(card.Definition);
            }
        }

        private static BattleDeck ToBattleDeck(Dictionary<int, int> counts, EFactionId faction, int leader, string name)
        {
            List<CardDefinition> cards = Expand(counts).Select(id => new CardDefinition(id, isPremium: false)).ToList();
            return new BattleDeck(faction, new CardDefinition(leader, isPremium: false), cards, name);
        }

        // Gives each card its planned template, in two passes. The engine
        // keeps passive triggers in registration order, and a transform moves
        // the card's triggers to the end, but a transform to the template a
        // card already has does nothing. Done in one pass, the trigger order
        // (and so the order passive abilities fire in) would depend on which
        // hidden cards already held their sampled template: on the real
        // cards. Every planned card first becomes a template without passive
        // triggers, then its sample, so the order depends on the plan alone.
        //
        // A transform is bookkeeping here, not a game event, but the engine
        // reacts to it: a card in hand or deck whose power drops to 0 (a unit
        // turned into a special) is queued for banishing. Every action the
        // transforms queue is dropped.
        private static int Transform(GameController copy, List<KeyValuePair<Card, int>> plan)
        {
            List<AAction> queued = copy.ActionManager.Actions;
            HashSet<AAction> before = new HashSet<AAction>(queued);
            int instances = copy.AbilityManager.ActiveInstances.Count;
            int changed = 0;
            CardDefinition placeholder = new CardDefinition(Placeholder(), isPremium: false);
            foreach (KeyValuePair<Card, int> entry in plan)
            {
                changed += entry.Key.Definition.TemplateId == entry.Value ? 0 : 1;
                entry.Key.Transform(placeholder, EResetMode.All);
            }
            foreach (KeyValuePair<Card, int> entry in plan)
            {
                entry.Key.Transform(new CardDefinition(entry.Value, isPremium: false), EResetMode.All);
            }
            queued.RemoveAll(action => !before.Contains(action));
            if (copy.AbilityManager.ActiveInstances.Count != instances)
            {
                throw new InvalidOperationException("Determinization started an ability");
            }
            return changed;
        }

        private static int s_Placeholder;

        // The lowest collectible bronze without passive triggers.
        private static int Placeholder()
        {
            if (s_Placeholder == 0)
            {
                s_Placeholder = GameSharedData.Instance.SharedRuntimeTemplates.Values
                    .Where(runtime => runtime.Template.Availability == EAvailability.BaseSet && runtime.Template.Tier == ECardTier.Bronze &&
                                      (runtime.Ability == null || runtime.Ability.PassiveTriggers.Count == 0))
                    .Min(runtime => runtime.Template.Id);
            }
            return s_Placeholder;
        }

        // The deck's contents are known, its order is not: the templates are
        // sorted before the shuffle, so the result depends on the seed and
        // the contents alone, never on the real order.
        // Shown: deck cards an open choice shows (see Shown) keep the
        // template shown; the rest are shuffled among the other positions.
        private static List<KeyValuePair<Card, int>> ReshuffleOwnDeck(List<Card> cards, Dictionary<ushort, int> shown, Random random)
        {
            List<int> templates = cards.Select(card => card.Definition.TemplateId).OrderBy(id => id).ToList();
            List<KeyValuePair<Card, int>> plan = new List<KeyValuePair<Card, int>>();
            List<Card> free = new List<Card>();
            foreach (Card card in cards)
            {
                if (shown.TryGetValue(card.Id, out int template) && templates.Remove(template))
                {
                    plan.Add(new KeyValuePair<Card, int>(card, template));
                }
                else
                {
                    free.Add(card);
                }
            }
            Shuffle(templates, random);
            plan.AddRange(free.Select((card, i) => new KeyValuePair<Card, int>(card, templates[i])));
            return plan;
        }

        // The deck's order is unknown, its cards are not: each card keeps
        // its state and moves. Cards an open choice shows (see Shown) and
        // cards in motion keep their place; the rest are sorted, so the
        // result depends on the seed and the cards alone, never on the real
        // order, and shuffled into the other places. A draw takes the deck's first cards.
        private static void ReorderOwnDeck(Location deck, Dictionary<ushort, int> shown, Random random)
        {
            List<int> places = new List<int>();
            List<Card> free = new List<Card>();
            for (int i = 0; i < deck.Cards.Count; i++)
            {
                Card card = deck.Cards[i];
                // A card on its way out (in the execution stack, or dying)
                // is tracked by its place: it stays, as the engine's own
                // queries leave it out (Location.GetCards).
                if (!shown.ContainsKey(card.Id) && !card.IsInExecutionStack && !card.IsWaitingToDie)
                {
                    places.Add(i);
                    free.Add(card);
                }
            }
            free.Sort((a, b) => a.Definition.TemplateId != b.Definition.TemplateId ? a.Definition.TemplateId.CompareTo(b.Definition.TemplateId)
                                    : a.Id.CompareTo(b.Id));
            Shuffle(free, random);
            for (int k = 0; k < places.Count; k++)
            {
                deck.Cards[places[k]] = free[k];
            }
            for (int i = 0; i < deck.Cards.Count; i++)
            {
                deck.Cards[i].Position = new CardPosition(deck.PlayerId, ELocation.Deck, i);
            }
        }

        // A card's template, power and armour.
        internal struct CardState : IComparable<CardState>
        {
            public int Template;
            public int Power;
            public int Armor;

            public int CompareTo(CardState other)
            {
                return Template != other.Template ? Template.CompareTo(other.Template)
                    : Power != other.Power ? Power.CompareTo(other.Power) : Armor.CompareTo(other.Armor);
            }
        }

        // The cards' states, sorted.
        private static CardState[] CardStates(List<Card> cards)
        {
            CardState[] states = new CardState[cards.Count];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new CardState { Template = cards[i].Definition.TemplateId, Power = cards[i].Power, Armor = cards[i].Armor };
            }
            Array.Sort(states);
            return states;
        }

        // States in before that after no longer holds (as multisets).
        private static int Lost(CardState[] before, CardState[] after)
        {
            int kept = 0;
            for (int i = 0, j = 0; i < before.Length && j < after.Length;)
            {
                int order = before[i].CompareTo(after[j]);
                kept += order == 0 ? 1 : 0;
                i += order <= 0 ? 1 : 0;
                j += order >= 0 ? 1 : 0;
            }
            return before.Length - kept;
        }

        // A hidden hand card's guess: a gold HandBias squared times, a silver
        // HandBias times as likely as a bronze (with OpponentStyle, the
        // fitted weights of the round); a gold or silver answer AnswerBias
        // times more.
        // Uniform: a draw in [0, 1).
        private int Weighted(List<int> templates, double uniform, int round)
        {
            double[] weights = templates.Select(id => TierWeight(Template(id).Tier, round) * AnswerWeight(id)).ToArray();
            double pick = uniform * weights.Sum();
            for (int i = 0; i < weights.Length; i++)
            {
                pick -= weights[i];
                if (pick < 0)
                {
                    return i;
                }
            }
            return templates.Count - 1;
        }

        private double AnswerWeight(int id)
        {
            if (AnswerBias == 1)
            {
                return 1;
            }
            ECardTier tier = Template(id).Tier;
            bool premium = tier == ECardTier.Gold || tier == ECardTier.Silver;
            return premium && (CardRoles.Has(id, "removal") || CardRoles.Has(id, "weather")) ? AnswerBias : 1;
        }

        private double TierWeight(ECardTier tier, int round)
        {
            if (OpponentStyle)
            {
                return Search.OpponentStyle.Select(SelfPlayStyle).HoldWeight(tier, round);
            }
            return tier == ECardTier.Gold ? HandBias * HandBias : tier == ECardTier.Silver ? HandBias : 1;
        }

        // A deck with the same faction, leader and size but random collectible
        // contents: what a seat may know of the opponent's list, and nothing
        // more (the fairness test's stand-in for the real list).
        public static BattleDeck Scramble(BattleDeck deck, Random random)
        {
            List<CardTemplate> pool = Pool(deck.FactionId, PoolKind.All);
            List<CardDefinition> cards = deck.Cards.Select(_ => new CardDefinition(pool[random.Next(pool.Count)].Id, isPremium: false)).ToList();
            return new BattleDeck(deck.FactionId, deck.Leader, cards, deck.Name);
        }

        private static List<int> Expand(Dictionary<int, int> counts)
        {
            // Sorted, so the sample depends on the seed alone and not on
            // dictionary order.
            List<int> list = new List<int>();
            foreach (KeyValuePair<int, int> entry in counts.OrderBy(entry => entry.Key))
            {
                for (int i = 0; i < entry.Value; i++)
                {
                    list.Add(entry.Key);
                }
            }
            return list;
        }

        private static void Shuffle<T>(List<T> list, Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T swap = list[i];
                list[i] = list[j];
                list[j] = swap;
            }
        }

        private static CardTemplate Template(int id)
        {
            return GameSharedData.Instance.GetSharedRuntimeTemplate(id).Template;
        }

        private enum PoolKind
        {
            All,
            Gold,
            Silver,
            Bronze,
            Ambush,
        }

        // The collectible cards of a faction and Neutral of a kind, in id
        // order. Built once per faction and kind (determinizations ask for
        // them often): callers must not change the list.
        private static readonly Dictionary<int, List<CardTemplate>> s_Pools = new Dictionary<int, List<CardTemplate>>();

        private static List<CardTemplate> Pool(EFactionId faction, PoolKind kind)
        {
            int key = (int)faction * 8 + (int)kind;
            lock (s_Pools)
            {
                if (!s_Pools.TryGetValue(key, out List<CardTemplate> pool))
                {
                    pool = s_Pools[key] = GameSharedData.Instance.SharedRuntimeTemplates.Values
                        .Select(runtime => runtime.Template)
                        .Where(template => template.Availability == EAvailability.BaseSet && template.Tier != ECardTier.Leader &&
                                           (template.FactionId == faction || template.FactionId == EFactionId.Neutral) && IsKind(template, kind))
                        .OrderBy(template => template.Id)
                        .ToList();
                }
                return pool;
            }
        }

        private static bool IsKind(CardTemplate template, PoolKind kind)
        {
            switch (kind)
            {
                case PoolKind.Gold:
                    return template.Tier == ECardTier.Gold;
                case PoolKind.Silver:
                    return template.Tier == ECardTier.Silver;
                case PoolKind.Bronze:
                    return template.Tier == ECardTier.Bronze;
                case PoolKind.Ambush:
                    return template.IsAmbush;
                default:
                    return true;
            }
        }

        private static int RandomTemplate(EFactionId faction, Random random, PoolKind kind)
        {
            List<CardTemplate> pool = Pool(faction, kind);
            if (pool.Count == 0)
            {
                pool = Pool(faction, PoolKind.All);
            }
            return pool[random.Next(pool.Count)].Id;
        }

        // A legal deck of the faction and the opponent's (public) size,
        // holding every card seen, filled out at random: 4 golds and 6
        // silvers (the limits, which decks nearly always fill), one copy of
        // each, and bronzes for the rest, up to 3 of each; or exactly
        // `bronzeRule` of each when a seen card pays off only for that shape.
        private static Dictionary<int, int> RandomDeck(EFactionId faction, int size, Dictionary<int, int> known, int bronzeRule, Random random)
        {
            Dictionary<int, int> deck = new Dictionary<int, int>(known);
            Fill(deck, faction, Pool(faction, PoolKind.Gold), DeckGolds, 1, 1, random);
            Fill(deck, faction, Pool(faction, PoolKind.Silver), DeckSilvers, 1, 1, random);
            Fill(deck, faction, Pool(faction, PoolKind.Bronze), Math.Max(size - DeckGolds - DeckSilvers, 0),
                 bronzeRule > 0 ? bronzeRule : 1, bronzeRule > 0 ? bronzeRule : BronzeCopies, random);
            return deck;
        }

        // The copies per card a seen card's ability wants of its owner's
        // starting deck (a HasNCopiesOfDefinitions check: 1 for Shupe's Day
        // Off, 2 for Ciri: Nova), when the seen cards still allow it; 0 for
        // none. Players who run such a card build for it.
        private static int CopyRule(GameController game, Dictionary<int, int> known)
        {
            foreach (int template in known.Keys.OrderBy(id => id))
            {
                int rule;
                lock (s_CopyRules)
                {
                    if (!s_CopyRules.TryGetValue(template, out rule))
                    {
                        HasNCopiesOfDefinitions check = game.GameInstance.SharedData.SharedRuntimeTemplates.TryGetValue(template, out RuntimeCardTemplate runtime) &&
                                                        runtime.Ability != null
                            ? runtime.Ability.Nodes.OfType<HasNCopiesOfDefinitions>().FirstOrDefault()
                            : null;
                        rule = s_CopyRules[template] = check == null ? 0 : ((IntVar)s_CopiesCount.GetValue(check)).Value;
                    }
                }
                if (rule > 0 && known.All(entry => Template(entry.Key).Tier != ECardTier.Bronze || entry.Value <= rule))
                {
                    return rule;
                }
            }
            return 0;
        }

        // Adds cards from the pool until the tier holds `size`: faction cards
        // weighted over neutral ones, minCopies to maxCopies copies each.
        private static void Fill(Dictionary<int, int> deck, EFactionId faction, List<CardTemplate> pool, int size, int minCopies, int maxCopies, Random random)
        {
            int count = pool.Sum(template => deck.TryGetValue(template.Id, out int held) ? held : 0);
            List<CardTemplate> open = pool.Where(template => !deck.ContainsKey(template.Id)).ToList();
            while (count < size && open.Count > 0)
            {
                int total = open.Sum(template => Weight(template, faction));
                int pick = random.Next(total);
                int index = 0;
                while (pick >= Weight(open[index], faction))
                {
                    pick -= Weight(open[index], faction);
                    index++;
                }
                int add = Math.Min(minCopies + random.Next(maxCopies - minCopies + 1), size - count);
                deck[open[index].Id] = add;
                count += add;
                open.RemoveAt(index);
            }
        }

        private static int Weight(CardTemplate template, EFactionId faction)
        {
            return template.FactionId == faction ? FactionWeight : 1;
        }
    }

    public class Determinization
    {
        // The meta deck guessed for the opponent; null for a random deck.
        public string Deck;
        // Seen opponent cards the guessed deck does not hold.
        public int Mismatches;
        // Cards the observer cannot see, and how many changed template.
        public int Hidden;
        public int Changed;
        // Hidden cards guessed from the faction pool because the deck guess
        // had run out.
        public int Invented;
        // The observer's own deck cards whose state (template, power,
        // armour) the determinization did not keep: 0 with KeepDeckState.
        public int DeckStatesLost;
    }
}
