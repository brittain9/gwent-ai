using System.Collections.Generic;
using System.Linq;
using GwentAI.Sim;
using GwentGameplay;

namespace GwentAI.Search
{
    // One step a player can take in answer to a request, as a search sees
    // it. The key names the step by what the acting player can see, never by
    // card instance ids, so the same step has the same key in every
    // determinization of one information set:
    //
    //   play <card> <owner> <row> <index>   pass    card <card>
    //   mull <card>                         keep
    //   tgt <target>    row <owner> <row>   ch <card>    done
    //
    // where <card> is template:power (cards alike to the player are one
    // action) and <target> is a board position, or a card in another
    // location by template:power when visible and by slot when not.
    //
    // Multi-card answers (several targets, mulligans, choices) are a
    // sequence of steps, each its own decision, ending with done/keep. A
    // step only queues its action, so the request does not show the steps
    // taken so far in the response; the caller passes their keys.
    //
    // With grouped plays, a play is two steps: "card <card>" chooses the
    // card (a step of the search only, with no move), then "play ..." its
    // position. Every placement of a card then shares the card's
    // statistics, which is what mostly matters (Cowling, Ward and Powley
    // 2012 found this the strongest enhancement for ISMCTS in Magic).
    public class GameAction
    {
        public string Key;
        // Null for a search-only step.
        public Move Move;
        // What the step is, for the policy net's option features
        // (GwentAI.Learn.OptionEncoder): its kind; the card it names (played,
        // mulliganed, chosen or targeted; null when the observer cannot see
        // it); the owner and location of its position or target; the
        // position index, or the slot of a hidden choice; and whether the
        // step names a card the observer cannot see.
        public OptionKind Kind;
        public Card Card;
        public EPlayerId Side;
        public ELocation Location;
        public int Index;
        public bool Hidden;

        public override string ToString()
        {
            return Key;
        }
    }

    // The kinds of step, as Actions.Legal names them.
    public enum OptionKind
    {
        Pass = 0,
        Card = 1,
        Play = 2,
        Target = 3,
        Row = 4,
        Choice = 5,
        Mulligan = 6,
        Keep = 7,
        Done = 8,
    }

    public static class Actions
    {
        public const string CardStep = "card ";

        // Play steps for a card include each row's end positions only (but
        // see Positions): placement inside a row matters only to adjacency
        // effects, and each extra position multiplies the branching factor
        // by the hand size.
        // Empty when the response is already complete (the last choice
        // taken).
        // Observer: the seat whose search names the steps. When another
        // player acts in its tree, steps the observer cannot see are named by
        // what it does see (a face-down play, a mulligan, a hidden pick), so
        // its tree does not split on cards it could not know. None: the
        // acting player's own view.
        public static List<GameAction> Legal(ARequestAction request, Player player, ICollection<string> taken, bool groupPlays = false,
                                             EPlayerId observer = 0)
        {
            bool hidden = observer != 0 && observer != player.Id;
            List<GameAction> actions = new List<GameAction>();
            switch (request)
            {
                case RequestPlayCardAction play:
                    string card = null;
                    foreach (string key in taken)
                    {
                        if (key.StartsWith(CardStep, System.StringComparison.Ordinal))
                        {
                            card = key.Substring(CardStep.Length);
                        }
                    }
                    if (card != null)
                    {
                        AddPlays(actions, play, player, only: card, withPass: false, hidden);
                    }
                    else if (groupPlays)
                    {
                        AddCards(actions, play, player, hidden);
                    }
                    else
                    {
                        AddPlays(actions, play, player, only: null, withPass: true, hidden);
                    }
                    break;
                case RequestMulliganAction mulligan:
                    AddMulligans(actions, mulligan, player, hidden);
                    break;
                case RequestCardTargetsAction targets:
                    AddTargets(actions, targets, player, taken, hidden ? observer : player.Id);
                    break;
                case RequestLocationTargetsAction rows:
                    AddRows(actions, rows, player, taken);
                    break;
                case RequestCardChoicesAction choices:
                    AddChoices(actions, choices, player, taken, hidden ? observer : 0);
                    break;
            }
            return actions;
        }

        // The cards the player may play in answer to the request: the forced
        // card, if an effect forces one, otherwise the playable hand and
        // leader. Pass is allowed unless a card is forced or restrictions
        // forbid it.
        public static List<Card> PlayableCards(RequestPlayCardAction request, Player player, out bool canPass)
        {
            GameController game = player.CurrentGameController;
            BattleRestrictions restrictions = player.Restrictions;
            Card forced = game.CardManager.GetCard(request.CardIdToPlay);
            canPass = forced == null && !restrictions.IsDisabled(EPlayerAction.Pass);
            if (forced != null)
            {
                return new List<Card> { forced };
            }
            if (restrictions.ForcePass || restrictions.IsDisabled(EPlayerAction.PlayCard))
            {
                return new List<Card>();
            }
            return player.GetPlayableCardsFromHand(includeLeader: true, restrictions);
        }

        // Where the card may go: each row's end positions, or every position
        // for a card whose place in the row matters (see Positional). Fills
        // `positions`.
        public static void Positions(Card card, Player player, List<CardPosition> positions)
        {
            positions.Clear();
            GameController game = player.CurrentGameController;
            game.CardManager.BoardManager.GetAvailablePositions(positions, card);
            bool anywhere = Positional(game, card.Template.Id);
            List<RowSpan> rows = anywhere ? null : RowSpans(positions);
            // List.RemoveAll with a closure per call, written out: this runs
            // for every card a rollout considers.
            int kept = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                CardPosition position = positions[i];
                if (player.Restrictions.CanPlayOnPosition(position) && (anywhere || IsRowEnd(rows, position)))
                {
                    positions[kept++] = position;
                }
            }
            positions.RemoveRange(kept, positions.Count - kept);
        }

        // Whether a card's ability reads the cards beside it or places cards
        // there (Toruviel boosts its neighbours, Poor F'ing Infantry spawns
        // its flanks, Fringilla Vigo copies the unit to her left): its
        // ability graph holds a shape or left/right neighbour node. Read
        // from the client's loaded abilities once per template.
        private static readonly Dictionary<int, bool> s_Positional = new Dictionary<int, bool>();

        private static bool Positional(GameController game, int template)
        {
            lock (s_Positional)
            {
                if (s_Positional.TryGetValue(template, out bool known))
                {
                    return known;
                }
                bool positional = game.GameInstance.SharedData.SharedRuntimeTemplates.TryGetValue(template, out RuntimeCardTemplate runtime) &&
                                  runtime.Ability != null &&
                                  runtime.Ability.Nodes.Any(node => node is GetCardsInShapeNode || node is GetCardPositionOnLeftNode ||
                                                                   node is GetCardPositionOnRightNode);
                s_Positional[template] = positional;
                return positional;
            }
        }

        // Whether a play request with no step taken offers at least two
        // grouped steps (Legal with groupPlays): AddCards' listing, counted
        // up to the second step with no step built. Rollouts need no more to
        // know they choose.
        [System.ThreadStatic]
        private static List<CardPosition> s_ChoicePositions;

        public static bool HasChoice(RequestPlayCardAction request, Player player, EPlayerId observer = 0)
        {
            bool hidden = observer != 0 && observer != player.Id;
            List<Card> cards = PlayableCards(request, player, out bool canPass);
            List<CardPosition> positions = s_ChoicePositions ?? (s_ChoicePositions = new List<CardPosition>());
            int steps = canPass ? 1 : 0;
            // The one card counted so far, when pass is not allowed: a card
            // alike to it adds no step.
            Card counted = null;
            foreach (Card card in cards)
            {
                if (counted != null && SamePlayName(counted, card, hidden))
                {
                    continue;
                }
                Positions(card, player, positions);
                if (positions.Count > 0 && ++steps >= 2)
                {
                    return true;
                }
                counted = positions.Count > 0 ? card : counted;
            }
            return false;
        }

        // Pass and one step per distinct playable card with somewhere to go;
        // or, given enough, the first that many of them.
        private static void AddCards(List<GameAction> actions, RequestPlayCardAction request, Player player, bool hidden, int enough = int.MaxValue)
        {
            List<Card> cards = PlayableCards(request, player, out bool canPass);
            if (canPass)
            {
                actions.Add(Pass(player));
            }
            HashSet<string> seen = new HashSet<string>();
            List<CardPosition> positions = new List<CardPosition>();
            foreach (Card card in cards)
            {
                string name = PlayName(card, hidden);
                if (seen.Contains(name))
                {
                    continue;
                }
                Positions(card, player, positions);
                if (positions.Count > 0)
                {
                    seen.Add(name);
                    bool faceDown = hidden && card.Template.IsAmbush;
                    actions.Add(new GameAction { Key = CardStep + name, Kind = OptionKind.Card, Card = faceDown ? null : card, Hidden = faceDown });
                    if (actions.Count >= enough)
                    {
                        return;
                    }
                }
            }
            if (actions.Count == 0)
            {
                actions.Add(Pass(player));
            }
        }

        private static void AddPlays(List<GameAction> actions, RequestPlayCardAction request, Player player, string only, bool withPass,
                                     bool hidden)
        {
            List<Card> cards = PlayableCards(request, player, out bool canPass);
            if (canPass && withPass)
            {
                actions.Add(Pass(player));
            }
            HashSet<string> seen = new HashSet<string>();
            List<CardPosition> positions = new List<CardPosition>();
            foreach (Card card in cards)
            {
                string name = PlayName(card, hidden);
                if ((only != null && name != only) || !seen.Add(name))
                {
                    continue;
                }
                Positions(card, player, positions);
                foreach (CardPosition position in positions)
                {
                    bool faceDown = hidden && card.Template.IsAmbush;
                    actions.Add(new GameAction
                    {
                        Key = "play " + name + " " + Position(position) + " " + position.Index,
                        Move = new Move { Player = player.Id, Kind = EMoveKind.Play, CardId = card.Id, Position = position },
                        Kind = OptionKind.Play, Card = faceDown ? null : card, Hidden = faceDown,
                        Side = position.PlayerId, Location = position.Location, Index = position.Index,
                    });
                }
            }
            // A forced card with nowhere to go leaves pass as the only answer.
            if (actions.Count == 0 && withPass)
            {
                actions.Add(Pass(player));
            }
        }

        // Round 1, and none of the player's cards is on the board or in its
        // graveyard yet: it has not played a card this game. (A card of its
        // own milled by the opponent first would count as played; rare.)
        public static bool BeforeFirstPlay(Player player)
        {
            return player.CurrentGameController.RoundManager.CurrentRoundIndex == 0 &&
                   player.GetCardsInLocation(ELocation.Active | ELocation.Graveyard).Count == 0;
        }

        // Takes the pass out of a play request's steps while the settings'
        // rules forbid it (SearchSettings.NoEarlyPass, EarlyPassHand,
        // EarlyPassMargin, NoResign; PassRules), unless it is the only step.
        // atRoot is false for the tree's own steps below the root decision,
        // where SearchSettings.PassRulesAtRoot lifts the first three.
        public static void RemoveEarlyPass(List<GameAction> legal, Player player, SearchSettings settings, bool atRoot = true)
        {
            if (legal.Count > 1 && legal.Exists(action => action.Key == PassKey) && PassForbidden(player, settings, atRoot))
            {
                legal.RemoveAll(action => action.Key == PassKey);
            }
        }

        public static bool PassForbidden(Player player, SearchSettings settings, bool atRoot = true)
        {
            PassRuleSet rules = new PassRuleSet
            {
                NoEarlyPass = settings.NoEarlyPass,
                EarlyPassHand = settings.EarlyPassHand,
                EarlyPassMargin = settings.EarlyPassMargin,
                NoResign = settings.NoResign,
                RootOnly = settings.PassRulesAtRoot,
            };
            return PassRules.Forbidden(new SeatPassPosition(player), rules, atRoot);
        }

        private struct SeatPassPosition : IPassPosition
        {
            private readonly Player m_Player;

            public SeatPassPosition(Player player)
            {
                m_Player = player;
            }

            public int RoundIndex { get { return m_Player.CurrentGameController.RoundManager.CurrentRoundIndex; } }
            public bool Resigns { get { return Actions.Resigns(m_Player); } }
            public bool OpponentPassed { get { return m_Player.OpponentPlayer.HasPassed; } }
            public int Lead { get { return m_Player.CurrentScore - m_Player.OpponentPlayer.CurrentScore; } }
            public int HandCount { get { return m_Player.GetCardsInLocation(ELocation.Hand).Count; } }
            public bool BeforeFirstPlay { get { return Actions.BeforeFirstPlay(m_Player); } }
        }

        // A pass that loses the match for sure: the opponent holds a round and
        // would pass back, with the seat behind, or level while a drawn round
        // (a round to each) still leaves the opponent ahead in rounds.
        public static bool Resigns(Player player)
        {
            Player opponent = player.OpponentPlayer;
            return opponent.Crowns >= 1 &&
                   (player.CurrentScore < opponent.CurrentScore || (player.CurrentScore == opponent.CurrentScore && player.Crowns < opponent.Crowns));
        }

        public const string PassKey = "pass";

        public static GameAction Pass(Player player)
        {
            return new GameAction { Key = PassKey, Move = new Move { Player = player.Id, Kind = EMoveKind.Pass }, Kind = OptionKind.Pass };
        }

        // The lowest and highest open index of each row among the positions.
        private struct RowSpan
        {
            public EPlayerId PlayerId;
            public ELocation Location;
            public int Lowest;
            public int Highest;
        }

        // Rows are listed in a buffer each thread reuses (Practice searches
        // on a worker thread); the list is valid until the thread's next
        // call.
        [System.ThreadStatic]
        private static List<RowSpan> s_Rows;

        private static List<RowSpan> RowSpans(List<CardPosition> positions)
        {
            List<RowSpan> rows = s_Rows ?? (s_Rows = new List<RowSpan>());
            rows.Clear();
            foreach (CardPosition position in positions)
            {
                int row = RowOf(rows, position);
                if (row < 0)
                {
                    rows.Add(new RowSpan { PlayerId = position.PlayerId, Location = position.Location, Lowest = position.Index, Highest = position.Index });
                    continue;
                }
                RowSpan span = rows[row];
                span.Lowest = System.Math.Min(span.Lowest, position.Index);
                span.Highest = System.Math.Max(span.Highest, position.Index);
                rows[row] = span;
            }
            return rows;
        }

        private static int RowOf(List<RowSpan> rows, CardPosition position)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].PlayerId == position.PlayerId && rows[i].Location == position.Location)
                {
                    return i;
                }
            }
            return -1;
        }

        // The lowest or highest open index in the position's row.
        private static bool IsRowEnd(List<RowSpan> rows, CardPosition position)
        {
            RowSpan row = rows[RowOf(rows, position)];
            return position.Index == row.Lowest || position.Index == row.Highest;
        }

        private static void AddMulligans(List<GameAction> actions, RequestMulliganAction request, Player player, bool hidden)
        {
            actions.Add(new GameAction { Key = "keep", Move = new Move { Player = player.Id, Kind = EMoveKind.FinishMulligan }, Kind = OptionKind.Keep });
            if (request.NumMulligans <= 0)
            {
                return;
            }
            HashSet<string> seen = new HashSet<string>();
            foreach (Card card in player.GetMulliganableCards())
            {
                // Which card another player sends back is hidden.
                string name = hidden ? "?" : CardName(card);
                if (seen.Add(name))
                {
                    actions.Add(new GameAction
                    {
                        Key = "mull " + name, Move = new Move { Player = player.Id, Kind = EMoveKind.Mulligan, CardId = card.Id },
                        Kind = OptionKind.Mulligan, Card = hidden ? null : card, Hidden = hidden,
                    });
                }
            }
        }

        // Viewer: whose view names the targets (the observer's when another
        // player acts).
        private static void AddTargets(List<GameAction> actions, RequestCardTargetsAction request, Player player, ICollection<string> taken,
                                       EPlayerId viewer)
        {
            int selected = request.GetNumSelectedTargets() + taken.Count;
            if (selected < request.MaxTargets)
            {
                foreach (KeyValuePair<string, ushort> step in Untaken(request.ValidTargets.Where(id => !request.SelectedTargets.Contains(id))
                                                                         .Select(id => new KeyValuePair<string, ushort>("tgt " + TargetName(request.GetTarget(id), viewer), id)), taken))
                {
                    Card target = request.GetTarget(step.Value);
                    bool visible = target.IsVisibleTo(viewer);
                    actions.Add(new GameAction
                    {
                        Key = step.Key, Move = new Move { Player = player.Id, Kind = EMoveKind.Target, CardId = step.Value },
                        Kind = OptionKind.Target, Card = visible ? target : null, Hidden = !visible,
                        Side = target.Position.PlayerId, Location = target.Position.Location, Index = target.Position.Index,
                    });
                }
            }
            if (selected >= request.MinTargets || actions.Count == 0)
            {
                actions.Add(Done(player, EMoveKind.FinishTargets));
            }
        }

        private static void AddRows(List<GameAction> actions, RequestLocationTargetsAction request, Player player, ICollection<string> taken)
        {
            int selected = request.GetNumSelectedTargets() + taken.Count;
            if (selected < request.MaxTargets)
            {
                foreach (Location row in request.ValidTargets)
                {
                    string key = "row " + EnumName<EPlayerId>.Of(row.PlayerId) + " " + EnumName<ELocation>.Of(row.Loc);
                    if (!request.SelectedTargets.Contains(row) && !taken.Contains(key))
                    {
                        actions.Add(new GameAction
                        {
                            Key = key,
                            Move = new Move { Player = player.Id, Kind = EMoveKind.TargetRow, Position = new CardPosition(row.PlayerId, row.Loc, 0) },
                            Kind = OptionKind.Row, Side = row.PlayerId, Location = row.Loc,
                        });
                    }
                }
            }
            if (selected >= request.MinTargets || actions.Count == 0)
            {
                actions.Add(Done(player, EMoveKind.FinishTargets));
            }
        }

        // Observer: set when another player picks; cards it cannot see are
        // then one hidden pick.
        private static void AddChoices(List<GameAction> actions, RequestCardChoicesAction request, Player player, ICollection<string> taken,
                                       EPlayerId observer)
        {
            int selected = request.SelectedChoices.Count + taken.Count;
            if (selected >= request.MaxChoices)
            {
                return;
            }
            GameController game = player.CurrentGameController;
            // A card hidden from the observer is named by its slot in the
            // offer: the slots of a fixed offer (Shupe's three modes) stay
            // distinct options, and a random offer reveals nothing. A visible
            // card from the other player's pile is marked "foe": alike cards
            // from both graveyards (Assire returns each to its owner's deck)
            // are different picks.
            foreach (KeyValuePair<string, ushort> step in Untaken(request.ValidChoices.Select((card, slot) => new KeyValuePair<string, ushort>(
                                                                         "ch " + (observer != 0 && !card.IsVisibleTo(observer) ? "?" + slot
                                                                                  : CardName(card) + (OriginalChoice(game, request, slot)?.GetPlayerId() is EPlayerId owner && owner != player.Id ? " foe" : "")),
                                                                         card.Id))
                                                                     .Where(step => !request.SelectedChoices.Contains(step.Value)), taken))
            {
                int slot = request.ValidChoices.FindIndex(card => card.Id == step.Value);
                Card choice = request.ValidChoices[slot];
                bool visible = observer == 0 || choice.IsVisibleTo(observer);
                actions.Add(new GameAction
                {
                    Key = step.Key, Move = new Move { Player = player.Id, Kind = EMoveKind.Choose, CardId = step.Value },
                    Kind = OptionKind.Choice, Card = visible ? choice : null, Hidden = !visible, Index = slot,
                });
            }
            // Reaching MaxChoices completes the choice on its own.
            if (selected >= request.MinChoices || actions.Count == 0)
            {
                actions.Add(Done(player, EMoveKind.FinishChoice));
            }
        }

        // The card a choice's slot stands for: the offer shows copies, and a
        // pick resolves to the original (Belief.Shown). Null when the offer
        // has no originals (created cards).
        public static Card OriginalChoice(GameController game, RequestCardChoicesAction request, int slot)
        {
            return request.OriginalChoices.Count == request.ValidChoices.Count ? game.CardManager.GetCard(request.OriginalChoices[slot]) : null;
        }

        // One step per name among the candidates (key, instance id) that is
        // not used up: alike cards are one step, but a name taken n times
        // still offers its next copy, which is the (n+1)-th instance in
        // candidate order. (Two identical cards offered with "choose 2"
        // would otherwise leave only "done", which the engine refuses.)
        private static IEnumerable<KeyValuePair<string, ushort>> Untaken(IEnumerable<KeyValuePair<string, ushort>> candidates, ICollection<string> taken)
        {
            Dictionary<string, int> used = new Dictionary<string, int>();
            foreach (string key in taken)
            {
                used.TryGetValue(key, out int count);
                used[key] = count + 1;
            }
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (KeyValuePair<string, ushort> candidate in candidates)
            {
                seen.TryGetValue(candidate.Key, out int index);
                seen[candidate.Key] = index + 1;
                used.TryGetValue(candidate.Key, out int taken_);
                if (index == taken_)
                {
                    yield return candidate;
                }
            }
        }

        private static GameAction Done(Player player, EMoveKind kind)
        {
            return new GameAction { Key = "done", Move = new Move { Player = player.Id, Kind = kind }, Kind = OptionKind.Done };
        }

        // How a play is named: by the card, or, for another player's ambush
        // card (played face down), by what the observer sees.
        private static string PlayName(Card card, bool hidden)
        {
            return hidden && card.Template.IsAmbush ? "?ambush" : CardName(card);
        }

        // Whether PlayName names the two cards alike.
        private static bool SamePlayName(Card a, Card b, bool hidden)
        {
            if (hidden && (a.Template.IsAmbush || b.Template.IsAmbush))
            {
                return a.Template.IsAmbush && b.Template.IsAmbush;
            }
            return a.Template.Id == b.Template.Id && a.Power == b.Power && System.Math.Max(a.Armor, 0) == System.Math.Max(b.Armor, 0);
        }

        // Two cards with one name are the same action to the player holding
        // them.
        private static string CardName(Card card)
        {
            return card.Template.Id + ":" + card.Power + (card.Armor > 0 ? "+" + card.Armor : "");
        }

        // "<owner> <row>" of a position. Keys are built in every simulated
        // decision, and Enum.ToString reflects on each call (and formats
        // [Flags] enums such as ELocation bit by bit).
        private static string Position(CardPosition position)
        {
            return EnumName<EPlayerId>.Of(position.PlayerId) + " " + EnumName<ELocation>.Of(position.Location);
        }

        private static string TargetName(Card card, EPlayerId viewer)
        {
            CardPosition position = card.Position;
            switch (position.Location)
            {
                case ELocation.Melee:
                case ELocation.Ranged:
                case ELocation.Siege:
                    return Position(position) + " " + position.Index;
                default:
                    return Position(position) + (card.IsVisibleTo(viewer) ? " " + CardName(card) : " ?" + position.Index);
            }
        }
    }

    // Enum.ToString, cached: the same text without reflection. Locked, since
    // Practice searches on a worker thread.
    public static class EnumName<T> where T : struct
    {
        private static readonly Dictionary<T, string> s_Names = new Dictionary<T, string>();

        public static string Of(T value)
        {
            lock (s_Names)
            {
                if (!s_Names.TryGetValue(value, out string name))
                {
                    name = s_Names[value] = value.ToString();
                }
                return name;
            }
        }
    }
}
