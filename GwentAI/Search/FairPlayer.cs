using GwentAI.Sim;
using GwentGameplay;

namespace GwentAI.Search
{
    // A seat played by the fair search. It answers its open request
    // when the owner calls Think, between engine updates: the search copies
    // the live game, and a copy taken in the middle of an update would hold
    // a half-applied action.
    public class FairPlayer : Player
    {
        private readonly int m_MatchSeed;
        private ARequestAction m_Pending;

        public FairAgent Agent { get; }

        public FairPlayer(PlayerManager playerManager, EPlayerId id, FairAgent agent, int matchSeed)
            : base(playerManager, id)
        {
            Agent = agent;
            m_MatchSeed = matchSeed;
        }

        public override bool IsAI()
        {
            return true;
        }

        // The engine files a mirror of each request for the other player's
        // client; only the seat's own is answered.
        public override void OnRequestReceived(ARequestAction request)
        {
            if (request.TargetPlayer == Id)
            {
                m_Pending = request;
            }
        }

        // One mulligan request takes several cards; each is its own decision,
        // made after the previous card is replaced and the new one seen.
        public override void OnCardMulligan(RequestMulliganAction currentRequest, Card outgoingCard, Card incomingCard)
        {
            if (currentRequest.NumMulligans > 0)
            {
                m_Pending = currentRequest;
            }
        }

        // Answers the open request, if any; true when it answered. Call
        // between engine updates. A request that opened with actions still
        // queued (Replay.Settled) waits for the next update: the workers
        // replay the game with them applied.
        public bool Think()
        {
            ARequestAction request = m_Pending;
            GameController live = CurrentGameController;
            if (request == null || request.Expired || !live.RequestManager.Requests.Contains(request))
            {
                m_Pending = null;
                return false;
            }
            if (!Replay.Settled(live))
            {
                return false;
            }
            m_Pending = null;
            foreach (Move move in Agent.Respond(live, this, request, decision => FairAgent.Seed(m_MatchSeed, Id, decision)))
            {
                move.Apply(this, request);
            }
            return true;
        }
    }
}
