using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Search;
using GwentAI.Sim;
using GwentGameplay;
using GwentGameplay.Vanity.Taunts;
using GwentUnity;
using GwentUnity.Vanity;
using GwentUnity.Vanity.Taunts;
using GwentVisuals;
using RedTools;

namespace GwentAI.Practice
{
    // Shows the taunts Emotes decides in the client, as an online opponent's
    // taunt is shown: the game's OnTaunt event with the AI's seat as the
    // taunting player, which is all the engine's TauntAction does where it
    // has authority (Practice). The event goes straight to the client's
    // TauntsManager (speech bubble, voice line, the player's mute), so no
    // action enters the game's action queue and the game the search copies
    // is the same with emotes on and off. Everything runs on the main thread,
    // after the AI's decision is made; a failure here is logged and never
    // stops a play.
    internal sealed class PracticeEmotes
    {
        private readonly GameController m_Game;
        private readonly EPlayerId m_Ai;
        private readonly EPlayerId m_Human;
        private readonly Action<string> m_Log;
        public readonly Emotes Rules;
        private bool m_Resolved;
        private TauntPackDefinition m_Pack;
        private bool m_ReportedFailure;

        private PracticeEmotes(GameController game, EPlayerId human, int seed, Action<string> log)
        {
            m_Game = game;
            m_Human = human;
            m_Ai = game.PlayerManager.GetOpponentPlayerId(human);
            m_Log = log;
            Rules = new Emotes(seed);
        }

        // A new Practice game's emotes; null when GWENT_AI_EMOTES is off.
        // Before the game record's Begin, so the end-of-game taunt is in it.
        public static PracticeEmotes Begin(GameController game, EPlayerId human, int seed, Action<string> log)
        {
            if (!Emotes.Enabled)
            {
                return null;
            }
            PracticeEmotes emotes = new PracticeEmotes(game, human, seed, log);
            game.EventManager.OnGameEnded.CoreEvent.AddListener(emotes.OnGameEnded);
            log($"Practice AI emotes: persona {emotes.Rules.Kind}");
            return emotes;
        }

        // The AI's seat answered a request; its moves are not applied yet, so
        // the board is the one it decided on. Only play decisions count.
        public void Decided(Player ai, ARequestAction request, List<Move> moves, DecisionStat stat)
        {
            try
            {
                if (ai.Id != m_Ai || !(request is RequestPlayCardAction))
                {
                    return;
                }
                Player human = m_Game.PlayerManager.GetPlayer(m_Human);
                RoundInfo[] finished = m_Game.RoundManager.Rounds.Where(round => round != null && round.Finished).ToArray();
                int round = m_Game.RoundManager.CurrentRoundIndex + 1;
                Move play = moves?.FirstOrDefault(move => move.Kind == EMoveKind.Play);
                Card card = play == null ? null : m_Game.CardManager.GetCard(play.CardId);
                Emotes.Moment now = new Emotes.Moment
                {
                    Round = round,
                    RoundsFinished = finished.Length,
                    LastRoundMargin = finished.Length == 0 ? 0 : Margin(finished[finished.Length - 1]),
                    ScoreLead = ai.CurrentScore - human.CurrentScore,
                    CardsInHand = ai.GetCardsInLocation(ELocation.Hand).Count,
                    HumanPassed = human.HasPassed,
                    // A searched decision of this round (a pass-to-win or a
                    // fallback answer has no fresh search).
                    Value = stat != null && stat.Visits > 0 && stat.Round == round ? stat.Value : (double?)null,
                    GoldPlay = card != null && card.Template.Tier == ECardTier.Gold,
                    Muted = Muted(),
                };
                Show(Rules.Decide(now));
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        private void OnGameEnded(EPlayerId winner, EEndGameReason reason)
        {
            try
            {
                Show(Rules.Ended(winner == m_Human, m_Game.RoundManager.CurrentRoundIndex + 1, Muted()));
            }
            catch (Exception ex)
            {
                Failed(ex);
            }
        }

        // The AI's score minus the human's in a finished round
        // (RoundInfo.PlayerScores follows PlayerManager.Players).
        private int Margin(RoundInfo round)
        {
            Player[] players = m_Game.PlayerManager.Players;
            int ai = 0;
            int human = 0;
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] == null)
                {
                    continue;
                }
                if (players[i].Id == m_Ai)
                {
                    ai = round.PlayerScores[i];
                }
                else if (players[i].Id == m_Human)
                {
                    human = round.PlayerScores[i];
                }
            }
            return ai - human;
        }

        // The player muted the opponent (or the setting mutes taunts).
        private static bool Muted()
        {
            BoardView board = ASingleton<BoardView>.Instance;
            TauntsManager manager = board == null ? null : board.TauntsManager;
            return manager != null && manager.IsOpponentMuted;
        }

        private void Show(Emotes.Taunt taunt)
        {
            if (taunt == null)
            {
                return;
            }
            ETauntCategoryType category = (ETauntCategoryType)Enum.Parse(typeof(ETauntCategoryType), taunt.Category);
            TauntPackDefinition pack = Pack();
            TauntCategorizedEntry entry = pack == null ? null : pack.GetCategoryEntry(category);
            if (entry == null || entry.TauntKeys == null || entry.TauntKeys.Count == 0)
            {
                m_Log($"Practice AI emote {taunt.Category} ({taunt.Trigger}) not shown: no line for it in the taunt pack");
                return;
            }
            m_Game.EventManager.OnTaunt.Invoke(new TauntNotificationData
            {
                TauntPackId = pack.BaseAssetId,
                CategoryType = category,
                // The line the client's own taunt picker sends for the category.
                VoiceKey = entry.TauntKeys[0],
                TauntingPlayerId = m_Ai,
            });
            m_Log($"Practice AI emote: {taunt.Category} ({taunt.Trigger}), decision {taunt.Decision}, round {taunt.Round}");
        }

        // The taunt pack the client shows for the AI's seat: the one its
        // avatar carries (PlayerGameVanityResourcesManager.LoadVanityData,
        // as TauntsManager and BoardPlayerView load it). The speech bubble
        // needs that pack; without one the local player's pack still gives
        // the voice line, though the AI's seat then shows no bubble.
        private TauntPackDefinition Pack()
        {
            if (m_Resolved)
            {
                return m_Pack;
            }
            m_Resolved = true;
            PlayerGameVanityResourcesManager vanity = Singleton<PlayerGameVanityResourcesManager>.Instance;
            PlayerInfo info = m_Game.PlayerManager.GetPlayer(m_Ai).Info;
            m_Pack = info == null ? null : vanity.LoadVanityData(info.Vanity).TauntPackDefition;
            string source = "the AI seat's avatar " + (info == null ? "(none)" : info.Vanity.AvatarId.ToString());
            if (m_Pack == null)
            {
                m_Pack = vanity.LoadVanityData(Singleton<InventoryManager>.Instance.GetCurrentPlayerVanityData()).TauntPackDefition;
                source = "the local player's avatar (the AI seat has none: voice only, no bubble)";
            }
            m_Log(m_Pack == null
                ? "Practice AI emotes: no taunt pack found; taunts are recorded but not shown"
                : $"Practice AI emotes: taunt pack {m_Pack.Character} ({m_Pack.BaseAssetId}) from {source}");
            return m_Pack;
        }

        private void Failed(Exception ex)
        {
            if (!m_ReportedFailure)
            {
                m_ReportedFailure = true;
                m_Log("Practice AI emotes failed (the game goes on): " + ex);
            }
        }

        // The game record's entries: the persona and every taunt sent.
        public void Record(Dictionary<string, object> game)
        {
            game["persona"] = Rules.Kind.ToString().ToLowerInvariant();
            game["taunts"] = Rules.Sent.Select(taunt => new Dictionary<string, object>
            {
                ["decision"] = taunt.Decision,
                ["round"] = taunt.Round,
                ["category"] = taunt.Category,
                ["trigger"] = taunt.Trigger,
            }).ToList();
        }
    }
}
