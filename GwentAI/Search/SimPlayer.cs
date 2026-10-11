using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Search
{
    // Chooses each step for the seats of a simulated game. Taken holds the
    // keys of the steps already taken in this response.
    public interface IDecider
    {
        GameAction Decide(Player player, ARequestAction request, ICollection<string> taken);
    }

    // A seat in a simulated game: answers every request at once with the
    // decider's steps, so one Update runs as far as the rules allow.
    public class SimPlayer : Player
    {
        private readonly IDecider m_Decider;

        public SimPlayer(PlayerManager playerManager, EPlayerId id, IDecider decider)
            : base(playerManager, id)
        {
            m_Decider = decider;
        }

        public override bool IsAI()
        {
            return true;
        }

        public override Player Clone(GameController controller)
        {
            SimPlayer player = new SimPlayer(controller.PlayerManager, Id, m_Decider);
            player.Copy(this, controller);
            return player;
        }

        public override void OnRequestReceived(ARequestAction request)
        {
            Answer(request);
        }

        // One mulligan request takes several cards; the engine reports each
        // and the seat answers again while mulligans remain.
        public override void OnCardMulligan(RequestMulliganAction currentRequest, Card outgoingCard, Card incomingCard)
        {
            if (currentRequest.NumMulligans > 0)
            {
                Answer(currentRequest);
            }
        }

        // Steps until the response is complete: several targets or choices
        // are one step each, all queued before the engine acts. The engine
        // files a mirror of each request for the other player's client; only
        // the seat's own copy is answered.
        private void Answer(ARequestAction request)
        {
            if (request.TargetPlayer != Id || request.Expired)
            {
                return;
            }
            List<string> taken = new List<string>();
            while (true)
            {
                GameAction action = m_Decider.Decide(this, request, taken);
                if (action == null)
                {
                    return;
                }
                taken.Add(action.Key);
                if (action.Move == null)
                {
                    continue;
                }
                long applied = PhaseClock.Start();
                action.Move.Apply(this, request);
                PhaseClock.Stop(Phase.Apply, applied);
                if (action.Move.EndsResponse || !IsMultiStep(request))
                {
                    return;
                }
            }
        }

        public static bool IsMultiStep(ARequestAction request)
        {
            switch (request.GetRequestType())
            {
                case ERequestType.TargetCards:
                case ERequestType.TargetLocations:
                case ERequestType.Choice:
                    return true;
                default:
                    return false;
            }
        }
    }
}
