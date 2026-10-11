using System;
using System.Collections.Generic;
using GwentGameplay;

namespace GwentAI.Sim
{
    // Answers every request synchronously with a uniformly random legal
    // choice. Used to drive cloned games to the end, so it never waits on
    // timers or threads. Mirrors the stock AIModule_Random, minus its bug of
    // mulliganing again after finishing the mulligan.
    public class RandomPlayer : Player
    {
        private readonly Random m_Random;

        public RandomPlayer(PlayerManager playerManager, EPlayerId id, Random random)
            : base(playerManager, id)
        {
            m_Random = random;
        }

        public override bool IsAI()
        {
            return true;
        }

        public override Player Clone(GameController controller)
        {
            RandomPlayer player = new RandomPlayer(controller.PlayerManager, Id, m_Random);
            player.Copy(this, controller);
            return player;
        }

        public override void OnCardMulligan(RequestMulliganAction currentRequest, Card outgoingCard, Card incomingCard)
        {
            if (currentRequest.NumMulligans > 0)
            {
                Mulligan(currentRequest);
            }
        }

        public override void OnRequestReceived(ARequestAction request)
        {
            switch (request.GetRequestType())
            {
                case ERequestType.PlayCard:
                    PlayCard((RequestPlayCardAction)request);
                    break;
                case ERequestType.Mulligan:
                    Mulligan((RequestMulliganAction)request);
                    break;
                case ERequestType.TargetCards:
                    TargetCards((RequestCardTargetsAction)request);
                    break;
                case ERequestType.TargetLocations:
                    TargetLocations((RequestLocationTargetsAction)request);
                    break;
                case ERequestType.Choice:
                    Choose((RequestCardChoicesAction)request);
                    break;
            }
        }

        private void PlayCard(RequestPlayCardAction request)
        {
            CardManager cardManager = request.GameController.CardManager;
            // A non-zero id means an effect forces this card to be played and
            // only its placement is open.
            Card card = cardManager.GetCard(request.CardIdToPlay);
            if (card == null)
            {
                List<Card> playable = GetPlayableCardsFromHand(includeLeader: true, Restrictions);
                if (playable.Count == 0)
                {
                    AskPass();
                    return;
                }
                card = playable[m_Random.Next(playable.Count)];
            }
            List<CardPosition> positions = new List<CardPosition>();
            cardManager.BoardManager.GetAvailablePositions(positions, card);
            if (positions.Count == 0)
            {
                AskPass();
                return;
            }
            AskPlayCard(card, positions[m_Random.Next(positions.Count)]);
        }

        private void Mulligan(RequestMulliganAction request)
        {
            List<Card> cards = GetMulliganableCards();
            if (cards.Count == 0 || m_Random.Next(request.NumMulligans + 1) >= request.NumMulligans)
            {
                AskFinishMulligan();
                return;
            }
            AskMulliganCard(cards[m_Random.Next(cards.Count)]);
        }

        private void TargetCards(RequestCardTargetsAction request)
        {
            List<ushort> picked = new List<ushort>();
            int count = Math.Min(m_Random.Next(request.MinTargets, request.MaxTargets + 1), request.GetNumValidTargets());
            while (picked.Count < count)
            {
                ushort id = request.ValidTargets[m_Random.Next(request.GetNumValidTargets())];
                if (!picked.Contains(id))
                {
                    AskSelectTargetCard(request, request.GetTarget(id), select: true);
                    picked.Add(id);
                }
            }
            AskFinishTargetSelection(request);
        }

        private void TargetLocations(RequestLocationTargetsAction request)
        {
            List<Location> picked = new List<Location>();
            int count = Math.Min(m_Random.Next(request.MinTargets, request.MaxTargets + 1), request.GetNumValidTargets());
            while (picked.Count < count)
            {
                Location location = request.ValidTargets[m_Random.Next(request.GetNumValidTargets())];
                if (!picked.Contains(location))
                {
                    AskSelectTargetLocation(request, location, select: true);
                    picked.Add(location);
                }
            }
            AskFinishTargetSelection(request);
        }

        private void Choose(RequestCardChoicesAction request)
        {
            List<ushort> picked = new List<ushort>();
            int count = Math.Min(m_Random.Next(request.MinChoices, request.MaxChoices + 1), request.ValidChoices.Count);
            while (picked.Count < count)
            {
                Card card = request.ValidChoices[m_Random.Next(request.ValidChoices.Count)];
                if (!picked.Contains(card.Id))
                {
                    AskSelectChoice(request, card, select: true);
                    picked.Add(card.Id);
                }
            }
            // Reaching MaxChoices completes the selection on its own.
            if (picked.Count < request.MaxChoices)
            {
                AskFinishChoiceSelection(request);
            }
        }
    }
}
