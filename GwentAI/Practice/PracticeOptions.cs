using System;
using System.Collections.Generic;
using GwentAI.Search;
using GwentAI.Sim;
using GwentCore;
using GwentGameplay;
using GwentGameplay.AI;

namespace GwentAI.Practice
{
    // What PracticeAI.Configure replaces; a null or false field keeps the default.
    public class PracticeOptions
    {
        // The search settings in place of PracticeAI.DefaultSettings.
        public SearchSettings Settings;
        // The belief from the prior decks in place of PracticeAI.DefaultBelief.
        public Func<List<DeckSpec>, Belief> Belief;
        // The agent's name in the start line and the game record ("spec").
        public string Label;
        // The belief's prior holds no roster decks.
        public bool NoMeta;
        // The AI seat's player in place of the search (FairSeat).
        public Func<AIPlayer, IPracticeSeat> SeatFactory;
        // Who plays the AI seat, for the start line, when that is not the
        // search; the line then leaves out the search's settings.
        public string SeatLabel;
    }

    // Who answers the AI seat's requests in Practice.
    public interface IPracticeSeat
    {
        // The moves answering the request, from a copy of the live game, on a
        // worker thread. nextSeed gives the search seed of the seat's n-th
        // searched decision.
        List<Move> Answer(GameController copy, Player seat, ARequestAction request, Func<int, int> nextSeed);
        // The game ended (on the main thread).
        void GameEnded(EPlayerId winner);
        // The game was left without a result.
        void Left();
        // How many times a failed answer is asked for again before the first
        // legal answer is played instead.
        int Retries { get; }
        // The searched decisions, for the game record; null: none.
        List<DecisionStat> Stats { get; }
        // The game record's "agent" entry, given the search's.
        Dictionary<string, object> RecordAgent(Dictionary<string, object> search);
    }

    // The default seat: the fair search answers.
    public class FairSeat : IPracticeSeat
    {
        public readonly FairAgent Agent;

        public FairSeat(FairAgent agent)
        {
            Agent = agent;
        }

        public List<Move> Answer(GameController copy, Player seat, ARequestAction request, Func<int, int> nextSeed)
        {
            return Agent.Respond(copy, seat, request, nextSeed);
        }

        public void GameEnded(EPlayerId winner)
        {
        }

        public void Left()
        {
        }

        public int Retries => 0;

        public List<DecisionStat> Stats => Agent.Stats;

        public Dictionary<string, object> RecordAgent(Dictionary<string, object> search)
        {
            return search;
        }
    }
}
