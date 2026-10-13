using System;
using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Sim
{
    // Plays a move list into a live game in its recorded order, on scripted
    // seats. A seat answers a request when the engine delivers it, as
    // RandomPlayer does, so each move lands at the same point in the action
    // sequence as in the recorded game; keeping that order keeps the shared
    // random generator's calls in their original order. A request delivered
    // before its move came up (the other seat's moves were still pending) is
    // answered by Pump after the update instead.
    public class Replay
    {
        private readonly GameController m_Game;
        private readonly Queue<Move> m_Moves;

        public int Applied { get; private set; }
        // Called when a scripted seat receives a request, before it answers:
        // the caller can record the position there (a replayed seat
        // answers inside the engine's update, before the caller looks).
        public Action<ARequestAction> BeforeAnswer;
        public bool Done => m_Moves.Count == 0;

        public Replay(GameController game, IEnumerable<Move> moves)
        {
            m_Game = game;
            m_Moves = new Queue<Move>(moves);
        }

        // Answers a request delivered to a scripted seat with the next
        // moves, if they are that seat's response to it.
        public void Answer(EPlayerId player, ARequestAction request)
        {
            if (request.TargetPlayer != player || request.Expired)
            {
                return;
            }
            ERequestType type = request.GetRequestType();
            int chosen = 0;
            // Targets already selected on this request (a response resumed by
            // Pump after the engine applied its first targets).
            int selected = request is RequestCardTargetsAction cards ? cards.SelectedTargets.Count
                : request is RequestLocationTargetsAction rows ? rows.SelectedTargets.Count : 0;
            while (m_Moves.Count > 0)
            {
                Move move = m_Moves.Peek();
                if (move.Player != player || !move.Answers(type))
                {
                    return;
                }
                // A forced play (an effect chose the card, only its placement
                // is open) takes only that card's move, or a pass when the
                // card has no place. With one placement the engine resolves
                // it itself while the request is still listed, and the next
                // recorded play or pass belongs to the request that follows
                // (Sile: Marching Orders, then its pick; Vicovaro Novice's
                // pick, then a pass on the next turn).
                if (request is RequestPlayCardAction forced && forced.CardIdToPlay != 0 && !AnswersForced(forced, move))
                {
                    return;
                }
                m_Moves.Dequeue();
                move.Apply(m_Game.PlayerManager.GetPlayer(player), request);
                Applied++;
                switch (move.Kind)
                {
                    case EMoveKind.Choose: chosen++; break;
                    case EMoveKind.Unchoose: chosen--; break;
                    case EMoveKind.Target: case EMoveKind.TargetRow: selected++; break;
                    case EMoveKind.Untarget: case EMoveKind.UntargetRow: selected--; break;
                }
                // Reaching MaxChoices completes a choice without a finish move.
                if (move.EndsResponse ||
                    (move.Kind == EMoveKind.Choose && chosen == ((RequestCardChoicesAction)request).MaxChoices))
                {
                    return;
                }
                // Reaching MaxTargets completes a targeting too. The client's
                // UI then sends no finish move, while the search agents do
                // (the engine ignores it); a finish that follows belongs to
                // this request, anything else to the request that follows
                // (Sentry asks for its two targets one request at a time).
                if ((move.Kind == EMoveKind.Target || move.Kind == EMoveKind.TargetRow) && selected == ((ARequestTargetsAction)request).MaxTargets)
                {
                    if (m_Moves.Count > 0 && m_Moves.Peek().Player == player && m_Moves.Peek().Kind == EMoveKind.FinishTargets)
                    {
                        m_Moves.Dequeue().Apply(m_Game.PlayerManager.GetPlayer(player), request);
                        Applied++;
                    }
                    return;
                }
            }
        }

        private bool AnswersForced(RequestPlayCardAction forced, Move move)
        {
            if (move.Kind == EMoveKind.Play)
            {
                return move.CardId == forced.CardIdToPlay;
            }
            if (move.Kind != EMoveKind.Pass)
            {
                return true;
            }
            Card card = m_Game.CardManager.GetCard(forced.CardIdToPlay);
            List<CardPosition> positions = new List<CardPosition>();
            if (card != null)
            {
                m_Game.CardManager.BoardManager.GetAvailablePositions(positions, card);
            }
            return positions.Count == 0;
        }

        public void Pump()
        {
            if (m_Moves.Count > 0)
            {
                Move move = m_Moves.Peek();
                ARequestAction request = OpenRequest(m_Game, move.Player, move.Answers);
                if (request != null)
                {
                    Answer(move.Player, request);
                }
            }
        }

        // Whether the engine has applied every action it queued. A request can
        // open while some are still queued: the engine kills a unit that an
        // ability has reduced to 0 power only when its queue is empty, and the
        // pushes that do it can come after the request of a forced play is
        // open (Seltkirk's duel, then Marching Orders' pick), where its update
        // stops. A replica reads a position only once this holds
        // (Replica.Reach), so a live seat decides only once it holds too: a
        // copy taken earlier differs from every replay by those actions
        // (Fingerprint's `pending`), and no worker could search it.
        public static bool Settled(GameController game)
        {
            return !game.ActionManager.HasActions;
        }

        // The request a player must answer, if one of a matching type is
        // open. The engine also files a mirror of each request for the other
        // player's client; only the copy targeted at the player is answered.
        public static ARequestAction OpenRequest(GameController game, EPlayerId player, Predicate<ERequestType> type)
        {
            foreach (ARequestAction request in game.RequestManager.Requests)
            {
                if (request.PlayerId == player && request.TargetPlayer == player && !request.Expired &&
                    type(request.GetRequestType()))
                {
                    return request;
                }
            }
            return null;
        }
    }

    // A seat driven by a Replay. It never decides anything itself.
    public class ScriptedPlayer : Player
    {
        public Replay Replay;

        public ScriptedPlayer(PlayerManager playerManager, EPlayerId id)
            : base(playerManager, id)
        {
        }

        public override bool IsAI()
        {
            return true;
        }

        public override Player Clone(GameController controller)
        {
            ScriptedPlayer player = new ScriptedPlayer(controller.PlayerManager, Id);
            player.Copy(this, controller);
            return player;
        }

        public override void OnRequestReceived(ARequestAction request)
        {
            Replay?.BeforeAnswer?.Invoke(request);
            Replay?.Answer(Id, request);
        }

        // One mulligan request takes several cards; the engine reports each
        // and the seat answers again while mulligans remain.
        public override void OnCardMulligan(RequestMulliganAction currentRequest, Card outgoingCard, Card incomingCard)
        {
            if (currentRequest.NumMulligans > 0)
            {
                Replay?.Answer(Id, currentRequest);
            }
        }
    }
}
