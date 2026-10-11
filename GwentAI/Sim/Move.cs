using System;
using GwentGameplay;

namespace GwentAI.Sim
{
    public enum EMoveKind
    {
        Play,
        Pass,
        Mulligan,
        FinishMulligan,
        Target,
        Untarget,
        TargetRow,
        UntargetRow,
        FinishTargets,
        Choose,
        Unchoose,
        FinishChoice,
    }

    // One player decision, as the engine's Ask* actions carry it. Every way a
    // player acts on a game is one of these, so a seeded game is fully
    // described by its decks, seed and move list. The text form is one move
    // per line, with an optional "# comment" (the card's name, when written
    // by MoveLog):
    //
    //   P1 play 31 P1 Siege 0      card 31 to P1's siege row, index 0
    //   P1 pass
    //   P1 mulligan 8 | P1 finish-mulligan
    //   P1 target 31 | P1 untarget 31 | P1 target-row P2 Ranged | P1 finish-targets
    //   P1 choose 1014 | P1 unchoose 1014 | P1 finish-choice
    public class Move
    {
        public EPlayerId Player;
        public EMoveKind Kind;
        // The card played, mulliganed, targeted or chosen.
        public ushort CardId;
        // Play: where the card goes. TargetRow: the row (index unused).
        public CardPosition Position;

        public static Move FromAction(AAction action)
        {
            switch (action)
            {
                case AskSelectPlayCardAction play:
                    return new Move { Player = play.PlayerId, Kind = EMoveKind.Play, CardId = play.CardToPlayID, Position = play.TargetPosition };
                case AskPassPlayerAction pass:
                    return new Move { Player = pass.PlayerId, Kind = EMoveKind.Pass };
                case AskMulliganAction mulligan:
                    return new Move { Player = mulligan.PlayerId, Kind = EMoveKind.Mulligan, CardId = mulligan.CardToMulliganID };
                case AskFinishMulliganAction finish:
                    return new Move { Player = finish.PlayerId, Kind = EMoveKind.FinishMulligan };
                case AskSelectTargetCardAction target:
                    return new Move { Player = target.PlayerId, Kind = target.Select ? EMoveKind.Target : EMoveKind.Untarget, CardId = target.CardId };
                case AskSelectTargetLocationAction row:
                    return new Move
                    {
                        Player = row.PlayerId,
                        Kind = row.Select ? EMoveKind.TargetRow : EMoveKind.UntargetRow,
                        Position = new CardPosition(row.Location.PlayerId, row.Location.Loc, 0),
                    };
                case AskFinishTargetSelectionAction finish:
                    return new Move { Player = finish.PlayerId, Kind = EMoveKind.FinishTargets };
                case AskSelectChoiceCardAction choice:
                    return new Move { Player = choice.PlayerId, Kind = choice.Select ? EMoveKind.Choose : EMoveKind.Unchoose, CardId = choice.CardId };
                case AskFinishChoiceAction finish:
                    return new Move { Player = finish.PlayerId, Kind = EMoveKind.FinishChoice };
                default:
                    return null;
            }
        }

        // Whether this move answers a request of this type. One finish move
        // ends both card and row targeting.
        public bool Answers(ERequestType type)
        {
            switch (Kind)
            {
                case EMoveKind.Play:
                case EMoveKind.Pass:
                    return type == ERequestType.PlayCard;
                case EMoveKind.Mulligan:
                case EMoveKind.FinishMulligan:
                    return type == ERequestType.Mulligan;
                case EMoveKind.Target:
                case EMoveKind.Untarget:
                    return type == ERequestType.TargetCards;
                case EMoveKind.TargetRow:
                case EMoveKind.UntargetRow:
                    return type == ERequestType.TargetLocations;
                case EMoveKind.FinishTargets:
                    return type == ERequestType.TargetCards || type == ERequestType.TargetLocations;
                default:
                    return type == ERequestType.Choice;
            }
        }

        // Whether this move ends the player's response to the request, so
        // the engine must act before the player's next move. A choice also
        // ends once the maximum number is chosen; Replay counts those.
        public bool EndsResponse => Kind == EMoveKind.Play || Kind == EMoveKind.Pass || Kind == EMoveKind.Mulligan ||
                                    Kind == EMoveKind.FinishMulligan || Kind == EMoveKind.FinishTargets || Kind == EMoveKind.FinishChoice;

        // Pushes the move's Ask action, as the player's own Ask* call would.
        public void Apply(Player player, ARequestAction request)
        {
            GameController game = player.CurrentGameController;
            Card card = CardId == 0 ? null : game.CardManager.GetCard(CardId);
            switch (Kind)
            {
                case EMoveKind.Play:
                    player.AskPlayCard(CardId, Position);
                    break;
                case EMoveKind.Pass:
                    player.AskPass();
                    break;
                case EMoveKind.Mulligan:
                    player.AskMulliganCard(CardId);
                    break;
                case EMoveKind.FinishMulligan:
                    player.AskFinishMulligan();
                    break;
                case EMoveKind.Target:
                case EMoveKind.Untarget:
                    player.AskSelectTargetCard((RequestCardTargetsAction)request, card, Kind == EMoveKind.Target);
                    break;
                case EMoveKind.TargetRow:
                case EMoveKind.UntargetRow:
                    player.AskSelectTargetLocation((RequestLocationTargetsAction)request,
                        game.CardManager.BoardManager.GetLocation(Position.PlayerId, Position.Location), Kind == EMoveKind.TargetRow);
                    break;
                case EMoveKind.FinishTargets:
                    player.AskFinishTargetSelection((ARequestTargetsAction)request);
                    break;
                case EMoveKind.Choose:
                case EMoveKind.Unchoose:
                    RequestCardChoicesAction choices = (RequestCardChoicesAction)request;
                    player.AskSelectChoice(choices, choices.GetChoiceFromId(CardId), Kind == EMoveKind.Choose);
                    break;
                case EMoveKind.FinishChoice:
                    player.AskFinishChoiceSelection((RequestCardChoicesAction)request);
                    break;
            }
        }

        public override string ToString()
        {
            string head = Player + " " + KindName(Kind);
            switch (Kind)
            {
                case EMoveKind.Play:
                    return $"{head} {CardId} {Position.PlayerId} {Position.Location} {Position.Index}";
                case EMoveKind.TargetRow:
                case EMoveKind.UntargetRow:
                    return $"{head} {Position.PlayerId} {Position.Location}";
                case EMoveKind.Mulligan:
                case EMoveKind.Target:
                case EMoveKind.Untarget:
                case EMoveKind.Choose:
                case EMoveKind.Unchoose:
                    return $"{head} {CardId}";
                default:
                    return head;
            }
        }

        public static Move Parse(string line)
        {
            int comment = line.IndexOf('#');
            string[] words = (comment < 0 ? line : line.Substring(0, comment)).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
            {
                throw new FormatException("Move needs a player and a kind: " + line);
            }
            Move move = new Move { Player = ParseEnum<EPlayerId>(words[0]), Kind = ParseKind(words[1]) };
            int expected;
            switch (move.Kind)
            {
                case EMoveKind.Play:
                    expected = 6;
                    if (words.Length == expected)
                    {
                        move.CardId = ushort.Parse(words[2]);
                        move.Position = new CardPosition(ParseEnum<EPlayerId>(words[3]), ParseEnum<ELocation>(words[4]), int.Parse(words[5]));
                    }
                    break;
                case EMoveKind.TargetRow:
                case EMoveKind.UntargetRow:
                    expected = 4;
                    if (words.Length == expected)
                    {
                        move.Position = new CardPosition(ParseEnum<EPlayerId>(words[2]), ParseEnum<ELocation>(words[3]), 0);
                    }
                    break;
                case EMoveKind.Mulligan:
                case EMoveKind.Target:
                case EMoveKind.Untarget:
                case EMoveKind.Choose:
                case EMoveKind.Unchoose:
                    expected = 3;
                    if (words.Length == expected)
                    {
                        move.CardId = ushort.Parse(words[2]);
                    }
                    break;
                default:
                    expected = 2;
                    break;
            }
            if (words.Length != expected)
            {
                throw new FormatException($"{KindName(move.Kind)} takes {expected - 2} arguments: {line}");
            }
            return move;
        }

        private static string KindName(EMoveKind kind)
        {
            switch (kind)
            {
                case EMoveKind.FinishMulligan: return "finish-mulligan";
                case EMoveKind.TargetRow: return "target-row";
                case EMoveKind.UntargetRow: return "untarget-row";
                case EMoveKind.FinishTargets: return "finish-targets";
                case EMoveKind.FinishChoice: return "finish-choice";
                default: return kind.ToString().ToLowerInvariant();
            }
        }

        private static EMoveKind ParseKind(string word)
        {
            foreach (EMoveKind kind in Enum.GetValues(typeof(EMoveKind)))
            {
                if (KindName(kind) == word)
                {
                    return kind;
                }
            }
            throw new FormatException("Unknown move kind " + word);
        }

        private static T ParseEnum<T>(string word)
        {
            return (T)Enum.Parse(typeof(T), word);
        }
    }
}
