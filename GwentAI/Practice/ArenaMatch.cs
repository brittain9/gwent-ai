using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using GameplayVisuals.Battle;
using GwentAI.Search;
using GwentCore;
using GwentGameplay;
using GwentTools;
using GwentUnity;
using GwentVisuals;
using GwentWebServiceClient.Services;
using GwentUnity.Social;
using HarmonyLib;
using Newtonsoft.Json;
using RedTools;

namespace GwentAI.Practice
{
    // Arena against the fair search (GWENT_AI_ARENA=1; off by default, and
    // then nothing here is patched).
    //
    // The client's arena flow runs as shipped (intro, ticket, draft,
    // encounter) until it starts matchmaking: AppArenaMatchmakingState
    // enters the GOG matchmaking state with GameType arena, whose
    // CheckForUpdate starts the search. In its place this builds a
    // single-player battle the way Practice does
    // (AppMatchSetupState.HandleStartMatch) with the run's drafted deck in
    // the human's seat, and reports the match as found. The arena's own
    // match-found chain (AppArenaMainState.OnMatchFound) then plays it with
    // the match state's target set to the arena, so after the results
    // screen the client returns to the arena, which refreshes the run from
    // the quest service (AppArenaState.TryProgressArenaAfterMatch).
    //
    // BattleSetupFactory.Create calls GameController.SetupHumanVsAI at once,
    // so PracticeAI's postfix takes the pending game (TakeNext): the human's
    // drafted deck, the AI's deck drafted by ArenaDraft, the arena belief,
    // and a record of kind "arena". At the game's end the result goes to
    // the local server's run (POST /internal/arena/{user id}/result on its
    // internal port, server/deploy/arena.py handle_internal), on a
    // background thread; the server then pushes the quest update that moves
    // the client's arena on.
    public static class ArenaMatch
    {
        public const string Variable = "GWENT_AI_ARENA";
        // The local server's internal port (server.py, 8447);
        // GWENT_ARENA_SERVER overrides it.
        public const string DefaultServer = "http://127.0.0.1:8447";
        // The name the run lists for the AI opponent.
        public const string OpponentName = "GwentAI";

        public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

        // An arena game the next SetupHumanVsAI plays.
        public class Game
        {
            public BattleDeck HumanDeck;
            public DeckSpec AiDeck;
            public int WinsBefore;
            public int Lives;
        }

        private static Action<string> s_Log;
        private static ArenaData s_Run;
        private static Game s_Next;
        private static readonly System.Random s_Random = new System.Random();
        private static readonly PropertyInfo s_CurrentState = AccessTools.Property(typeof(BaseMatchmakingState), nameof(BaseMatchmakingState.CurrentState));
        private static readonly FieldInfo s_LeavingToGame = AccessTools.Field(typeof(BaseMatchmakingState), "m_LeavingToGame");

        public static void Install(HarmonyLib.Harmony harmony, Action<string> log)
        {
            s_Log = log;
            harmony.Patch(AccessTools.Method(typeof(AppArenaMatchmakingState), nameof(AppArenaMatchmakingState.OnEnterState)),
                prefix: new HarmonyMethod(typeof(ArenaMatch), nameof(MatchmakingEnterPrefix)));
            harmony.Patch(AccessTools.Method(typeof(BaseMatchmakingState), "CheckForUpdate"),
                prefix: new HarmonyMethod(typeof(ArenaMatch), nameof(CheckForUpdatePrefix)));
            harmony.Patch(AccessTools.Method(typeof(GwentWSFacade), nameof(GwentWSFacade.ConsumeArenaTicket)),
                prefix: new HarmonyMethod(typeof(ArenaMatch), nameof(ConsumeTicketPrefix)));
            log($"Arena: on; arena matches are played against the fair search here, results posted to {Server}/internal/arena/{{user}}/result");
        }

        public static string Server => Environment.GetEnvironmentVariable("GWENT_ARENA_SERVER") ?? DefaultServer;

        // The run a consumed ticket starts reaches the client as a quest_updated
        // push through the Galaxy notification broker, to which the client
        // may not be subscribed (the broker then queues the push and the
        // retriever times out after 15 s: "check internet connection"). The
        // quest manager's own re-download raises the same QuestAdded the
        // retriever waits for, so it is asked for right after the consume
        // succeeds; a push that does arrive as well is a second, harmless
        // update of the same quest.
        private static void ConsumeTicketPrefix(ref Action<GwentWebServiceClient.DTO.ArenaQuestId> successCallback)
        {
            Action<GwentWebServiceClient.DTO.ArenaQuestId> original = successCallback;
            successCallback = id =>
            {
                original?.Invoke(id);
                try
                {
                    Singleton<SocialController>.Instance.QuestSystem.Controller.RedownloadQuest(id.UserQuestId);
                    s_Log($"Arena: ticket consumed, run quest {id.UserQuestId} re-read from the server");
                }
                catch (Exception ex)
                {
                    s_Log("Arena: could not re-read the run quest after the ticket: " + ex);
                }
            };
        }

        // The run whose match the arena's matchmaking is about to search.
        private static void MatchmakingEnterPrefix(AppArenaMatchmakingState __instance)
        {
            s_Run = __instance.CurrentArenaData;
        }

        // Arena matchmaking: a local game in place of the GOG search.
        private static bool CheckForUpdatePrefix(BaseMatchmakingState __instance)
        {
            // EGameType.arena (by name: the enum lives in a client assembly this
            // file need not import).
            if (__instance.GameType.ToString() != "arena" || s_Run == null)
            {
                return true;
            }
            ArenaData run = s_Run;
            s_Run = null;
            try
            {
                StartLocal(__instance, run);
                return false;
            }
            catch (Exception ex)
            {
                s_Next = null;
                s_Log("Arena: could not start the local game; the client's own matchmaking runs (cancel it with B): " + ex);
                return true;
            }
        }

        private static void StartLocal(BaseMatchmakingState matchmaking, ArenaData run)
        {
            // The arena's deck keeps the Neutral faction it was made with
            // (GOGArenaDataRetriever never sets one; SetLeaderCard does
            // not): the human's faction, and its passive, is the leader's.
            BattleDeck drafted = run.DeckData.ToBattleDeck();
            EFactionId faction = GameSharedData.Instance.SharedRuntimeTemplates[drafted.Leader.TemplateId].Template.FactionId;
            BattleDeck human = new BattleDeck(faction, drafted.Leader, drafted.Cards, "arena");
            DeckSpec ai = DraftDeck();
            s_Log($"Arena match: human deck {human.FactionId} leader {human.Leader.TemplateId} ({human.Cards.Count} cards), " +
                  $"run at {run.GetMatchWins()} wins, {run.LivesCount} lives; AI deck {ai.Name}");
            BattleDefinition battle = ASingleton<BattleDefinitionHolder>.Instance.GetRandomPracticeBattle();
            battle = BattleDefinitionFactory.Create(battle).SetPlayerInfoGOG().SetVanityCurrent();
            BattleDefinitionFactory.Create(battle).SetDeck(EPlayerId.P1, human);
            s_Next = new Game { HumanDeck = human, AiDeck = ai, WinsBefore = run.GetMatchWins(), Lives = run.LivesCount };
            ASingleton<GwentApp>.Instance.SetBattle(BattleSetupFactory.Create(EGameplaySetupMode.SinglePlayer, battle));
            if (s_Next != null)
            {
                s_Next = null;
                throw new InvalidOperationException("the arena battle did not reach SetupHumanVsAI");
            }
            // Found, and leaving to the game: the arena state's leave then
            // cancels nothing, and the battle is kept (OnLeaveState).
            s_CurrentState.SetValue(matchmaking, BaseMatchmakingState.MatchmakingState.Found, null);
            s_LeavingToGame.SetValue(matchmaking, true);
            matchmaking.OnMatchFound.Invoke();
        }

        // The pending arena game, once (PracticeAI's SetupHumanVsAI postfix).
        public static Game TakeNext()
        {
            Game next = s_Next;
            s_Next = null;
            return next;
        }

        // The AI's deck: ArenaDraft over the client's collectible cards.
        private static DeckSpec DraftDeck()
        {
            List<int> Ids(ECardTier tier) => Belief.ArenaPool(tier).Select(template => template.Id).ToList();
            ArenaDraft.Result draft;
            lock (s_Random)
            {
                draft = ArenaDraft.Draft(Ids(ECardTier.Gold), Ids(ECardTier.Silver), Ids(ECardTier.Bronze), Ids(ECardTier.Leader), s_Random);
            }
            CardTemplate leader = Belief.ArenaPool(ECardTier.Leader).First(template => template.Id == draft.Leader);
            return new DeckSpec
            {
                Name = "arena draft " + leader.FactionId + " " + draft.Leader,
                Faction = leader.FactionId.ToString(),
                Leader = new CardRef { Id = draft.Leader },
                Cards = draft.Cards.GroupBy(id => id).Select(group => new CardRef { Id = group.Key, Count = group.Count() }).ToList(),
                Kind = "arena",
            };
        }

        // Posts the game's result to the run without holding the frame; a
        // failure is logged, never thrown. A draw counts for neither side of
        // the run (the run's objectives are wins and losses), so nothing is
        // posted for it.
        public static void Report(EPlayerId winner, EPlayerId human, int seed, string record)
        {
            if (winner == EPlayerId.AllPlayers)
            {
                s_Log($"Arena result: draw (seed {seed}), not posted: a draw counts for neither wins nor losses");
                return;
            }
            bool won = winner == human;
            ulong? user = UserId();
            if (user == null)
            {
                s_Log($"Arena result not posted ({(won ? "win" : "loss")}, seed {seed}): no signed-in user");
                return;
            }
            string url = $"{Server}/internal/arena/{user}/result";
            string json = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["won"] = won,
                ["opponent_name"] = OpponentName,
                ["seed"] = seed,
                ["record"] = record,
            });
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (WebClient client = new WebClient())
                    {
                        client.Headers[HttpRequestHeader.ContentType] = "application/json";
                        string answer = client.UploadString(url, "POST", json);
                        s_Log($"Arena result posted: {(won ? "win" : "loss")} to {url}: {answer}");
                    }
                }
                catch (Exception ex)
                {
                    s_Log($"Arena result not posted ({(won ? "win" : "loss")} to {url}): {ex.Message}");
                }
            });
        }

        private static ulong? UserId()
        {
            try
            {
                return Singleton<ProfileManager>.Instance.CurrentUser?.Id;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
