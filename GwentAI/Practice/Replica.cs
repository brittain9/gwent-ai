using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Sim;
using GwentCore;
using GwentGameplay;
using GwentGameplay.Settings;
using RedLogger;

namespace GwentAI.Practice
{
    // A live game rebuilt in another process from its record (LiveGame):
    // the same engine, seed, starting player and decks, with the recorded
    // moves replayed on scripted seats. On the virtual clock a whole game
    // replays in a few milliseconds, so a worker rebuilds the position from
    // scratch for every decision.
    public class Replica : IDisposable
    {
        // Updates without a replayed move after which the replay is stuck:
        // the record and the engine disagree.
        public const int StallUpdates = 5000;

        private readonly GameInstance m_Game;
        private readonly Replay m_Replay;
        private bool m_Ended;

        public GameController Game => m_Game.GameController;
        public int Updates { get; private set; }
        public int Applied => m_Replay.Applied;

        public Replica(GameSharedData shared, IRedLogger logger, int seed, string starting, DeckSpec p1, DeckSpec p2, IEnumerable<string> moves)
        {
            BattleSettings settings = new BattleSettings
            {
                P1 = new PlayerSettings(new PlayerInfo { Name = p1.Name }, p1.ToBattleDeck()),
                P2 = new PlayerSettings(new PlayerInfo { Name = p2.Name }, p2.ToBattleDeck()),
                StartingPlayer = (EStartingPlayer)Enum.Parse(typeof(EStartingPlayer), starting),
                ShowIntro = false,
                EnableTimers = false,
                EnableTimeout = false,
                GameID = (ulong)(uint)seed,
                VisualSeed = seed,
            };
            m_Game = new GameInstance(logger, EGameMode.SinglePlayer, shared, settings);
            GameController controller = m_Game.GameController;
            PlayerManager players = controller.PlayerManager;
            m_Replay = new Replay(controller, moves.Select(Move.Parse));
            controller.Setup(EPlayerId.P1, new ScriptedPlayer(players, EPlayerId.P1) { Replay = m_Replay },
                             new ScriptedPlayer(players, EPlayerId.P2) { Replay = m_Replay });
            controller.RandomGenerator.Copy(new MersenneTwisterRandom(seed));
            controller.EventManager.OnGameEnded.CoreEvent.AddListener((winner, reason) => m_Ended = true);
            m_Game.Start();
        }

        // Replays the moves and runs the engine on until the seat's request
        // of this type is open, between updates, as a seat sees it.
        // Null when the replay stalls or the game ends first; error says why.
        public ARequestAction Reach(EPlayerId seat, ERequestType type, out string error)
        {
            error = null;
            int applied = -1;
            int progressAt = 0;
            while (!m_Ended)
            {
                m_Game.Update();
                Updates++;
                m_Replay.Pump();
                if (m_Replay.Applied != applied)
                {
                    applied = m_Replay.Applied;
                    progressAt = Updates;
                }
                // Once the engine has applied the queued moves: a mulligan
                // request stays open while its card is swapped, and the live
                // seat decides again only after the swap.
                if (m_Replay.Done && !Game.ActionManager.HasActions)
                {
                    ARequestAction request = Replay.OpenRequest(Game, seat, open => open == type);
                    if (request != null)
                    {
                        return request;
                    }
                }
                if (Updates - progressAt > StallUpdates)
                {
                    error = m_Replay.Done
                        ? $"the {type} request never opened after {applied} moves; open: " +
                          string.Join(", ", Game.RequestManager.Requests.Select(open => open.ToString()).ToArray())
                        : $"no request for replayed move {applied}";
                    return null;
                }
            }
            error = $"the game ended after {m_Replay.Applied} moves";
            return null;
        }

        public void Dispose()
        {
            m_Game.Dispose();
        }
    }
}
