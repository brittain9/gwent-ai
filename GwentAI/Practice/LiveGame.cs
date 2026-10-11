using System.Collections.Generic;
using System.Linq;
using GwentAI.Sim;
using GwentGameplay;

namespace GwentAI.Practice
{
    // The game being played in this process, as a replayable record kept up
    // to date: its seed, starting player and both decks in deal order, and
    // every move applied so far (MoveLog). Search workers rebuild the
    // position from it (SearchWorker), and the Practice game record is
    // written from it (PracticeAI.GameRecord). One game at a time.
    public class LiveGame
    {
        public class Entry
        {
            public int Round;
            public string Move;
        }

        private static LiveGame s_Current;
        private static int s_Count;

        public static LiveGame Current => s_Current;

        // Unique in this process, so a worker tells one game from the next.
        public readonly string Id;
        public readonly int Seed;
        public readonly string Starting;
        public readonly DeckSpec P1;
        public readonly DeckSpec P2;
        private readonly GameController m_Game;
        private readonly List<Entry> m_Moves = new List<Entry>();
        private bool m_Ended;

        // Starts recording the game's moves; the previous game's record ends.
        // Call once the game is set up and seeded, before its first move.
        public static LiveGame Begin(GameController game, int seed)
        {
            s_Current?.End();
            LiveGame live = new LiveGame(game, seed);
            MoveLog.Applied += live.OnMove;
            s_Current = live;
            return live;
        }

        private LiveGame(GameController game, int seed)
        {
            m_Game = game;
            Seed = seed;
            Id = seed + "-" + ++s_Count;
            GwentGameplay.Settings.BattleSettings settings = game.GameInstance.Settings;
            Starting = settings.StartingPlayer.ToString();
            P1 = Deck(settings.GetPlayerSettings(EPlayerId.P1).Deck);
            P2 = Deck(settings.GetPlayerSettings(EPlayerId.P2).Deck);
        }

        // The deck as the game dealt it, one entry per card in order (the
        // order decides the shuffle and the card instance ids).
        public static DeckSpec Deck(BattleDeck deck)
        {
            return new DeckSpec
            {
                Name = deck.Name,
                Faction = deck.FactionId.ToString(),
                Leader = new CardRef { Id = deck.Leader.TemplateId },
                Cards = deck.Cards.Select(card => new CardRef { Id = card.TemplateId, Count = 1 }).ToList(),
            };
        }

        private void OnMove(Move move)
        {
            lock (m_Moves)
            {
                m_Moves.Add(new Entry { Round = m_Game.RoundManager.CurrentRoundIndex + 1, Move = MoveLog.Describe(m_Game, move) });
            }
        }

        // The moves so far, with their rounds. Safe from any thread.
        public List<Entry> Moves
        {
            get
            {
                lock (m_Moves)
                {
                    return new List<Entry>(m_Moves);
                }
            }
        }

        public List<string> MoveTexts()
        {
            lock (m_Moves)
            {
                return m_Moves.Select(entry => entry.Move).ToList();
            }
        }

        public void End()
        {
            if (m_Ended)
            {
                return;
            }
            m_Ended = true;
            MoveLog.Applied -= OnMove;
            if (s_Current == this)
            {
                s_Current = null;
            }
        }
    }
}
