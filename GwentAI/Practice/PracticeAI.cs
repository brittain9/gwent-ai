using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using GwentAI.Learn;
using GwentAI.Search;
using GwentAI.Sim;
using GwentCore;
using GwentGameplay;
using GwentGameplay.Settings;
using GwentGameplay.AI;
using HarmonyLib;
using Newtonsoft.Json;

namespace GwentAI.Practice
{
    // Practice against the fair search instead of the client's AI.
    //
    // The client's AIPlayer stays in its seat, since the visuals expect one,
    // but its requests go to a FairAgent and its own decision code never
    // starts. Like the stock AI, the agent searches on a worker thread: the
    // live game is copied on the main thread when a request opens, the
    // agent answers from that copy, and the answer is applied on the main
    // thread once it is ready and a short, varied think time has passed.
    // Frames never wait on the search.
    //
    // Every game is recorded to GWENT_GAMES_DIR (see GameRecord).
    //
    // With GWENT_SEARCH_WORKERS set (scripts/local_session.py starts the
    // workers), each decision is searched by those worker processes at once
    // and their root statistics summed (WorkerSearch); without them, or when
    // they fail, the search runs on the worker thread here.
    //
    // Configure, before Install, replaces the defaults: the search settings,
    // the belief, or the seat's player (IPracticeSeat), which then answers
    // from the same copy of the live game, on the worker thread, while the
    // client waits. Practice has no turn timer (the engine's state timers
    // and timeouts run only in multiplayer: AGameState.SetupTimer,
    // TurnGameState), so nothing passes for the seat while it thinks.
    public static class PracticeAI
    {
        private static readonly Dictionary<AIPlayer, Seat> s_Seats = new Dictionary<AIPlayer, Seat>();
        private static SearchSettings s_Settings;
        private static Roster s_Roster;
        private static bool s_RosterDecks;
        // The local profile's name: a deck tagged with it (DeckSpec.Owner) is
        // never in the AI's prior when that profile plays.
        private static readonly string Human = Environment.GetEnvironmentVariable("GWENT_PLAYER") ?? "player";
        private static Belief s_Belief;
        // With GWENT_AI_ARENA=1: the belief for a drafted opponent
        // (Belief.ArenaDeck, no meta decks), and whether the current game is
        // an arena game (ArenaMatch).
        private static Belief s_ArenaBelief;
        private static bool s_ArenaGame;
        private static Action<string> s_Log;
        private static string s_GamesDir;
        private static readonly System.Random s_Random = new System.Random();
        private static GameRecord s_Record;
        // The current game's taunts (PracticeEmotes); null unless GWENT_AI_EMOTES=1.
        private static PracticeEmotes s_Emotes;
        private static WorkerSearch s_Workers;
        private static PracticeOptions s_Options = new PracticeOptions();
        private static int s_ThinkMs = DefaultThinkMs;
        // False: the mod leaves Practice to the client's own AI.
        public static bool Enabled = true;
        // The next game's seed when set (code that drives the AI can set
        // it); otherwise drawn.
        public static int? NextGameSeed;
        // The next game's round-1 starter when set; otherwise Starter(seed).
        // Practice draws the starter instead of the client's fixed Top (the AI).
        public static EStartingPlayer? NextGameStarter;

        // A fair coin from the game's seed, so the record replays: even
        // seeds start with the bottom seat (P1), odd with the top (P2). It
        // uses no draw of the game's random generator.
        public static EStartingPlayer Starter(int seed)
        {
            return (seed & 1) == 0 ? EStartingPlayer.Bottom : EStartingPlayer.Top;
        }
        // At most this many simulated games per decision, and about ThinkMs
        // of search for a play; time binds first. Under
        // 10 s is typical; longer only for big plays, up to 30 s.
        private const int DefaultIterations = 50000;
        // A play decision stops at the floor, half of ThinkMs, in most cases.
        // A 10 s budget gives a 5 s floor; open decisions run on to
        // ThinkExtend times it (15 s). GWENT_AI_THINK_MS overrides the budget.
        private const int DefaultThinkMs = 10000;
        // The adaptive budget (settled decisions stop from half of ThinkMs, open
        // ones run on to DefaultPlay.ThinkExtend times it) and the mulligan's
        // scale (DefaultPlay.ThinkMulliganScale) are PracticeBudget's.
        // No first pass in round 1 before the AI has played a card (SearchSettings.NoEarlyPass).
        private const bool NoEarlyPass = false;
        // In round 1, while the opponent has not passed, no pass while the AI holds more than this many cards (0: off).
        private const int EarlyPassHand = 7;
        // Exploration at the opponent's nodes (negative: the same constant as the AI's own).
        private const double OpponentExploration = -1;
        // Rollouts keep golds and the leader in rounds they can afford to lose (negative: off).
        private const double KeepPremium = -1;
        // The own deck is reshuffled by moving cards, which keep their buffs (Belief.KeepDeckState).
        private const bool KeepDeckState = true;
        // Rollouts answer a threat with removal or weather at this chance (0: off).
        private const double ThreatAware = DefaultPlay.ThreatAware;
        // Ties among the best plays go to the economical step within this gap (negative: off).
        private const double EconomyTieBreak = DefaultPlay.EconomyTieBreak;
        // No resigning pass in a lost round 3 (SearchSettings.NoResign).
        private const bool NoResign = DefaultPlay.NoResign;
        // The search's prior is the policy net embedded in the mod (PUCT; PracticeBudget.LoadDefaultPrior).
        private const bool PolicyPrior = true;
        // A match-winning pass is taken at once, with no search (SearchSettings.PassToWin).
        private const bool PassToWin = true;
        // Every draw the search makes comes from SplitMix64 (SearchSettings.SearchRandom).
        private const bool SearchRandom = true;
        // The opponent's deck is drawn from a posterior over the known decks (Belief.PosteriorDecks).
        private const bool PosteriorDecks = true;
        // The score margin's weight in a finished simulation's reward (0: off).
        private const double Margin = DefaultPlay.Margin;
        // A card that may aim at either side aims a damage at the opponent and a boost at itself.
        private const bool RolloutAim = true;
        // The round-1 winner's chance to pass a dry round in a rollout.
        private const double DryPass = DefaultPlay.DryPass;
        // The PUCT constant (SearchSettings.CPuct).
        private const double CPuct = DefaultPlay.CPuct;
        // The embedded value net scores the leaf cuts from round DefaultPlay.NetFromRound on.
        private const bool UseDefaultValueNet = true;
        // The softmax temperature of the policy prior (SearchSettings.PriorTemperature).
        private const double PriorTemperature = DefaultPlay.PriorTemperature;
        public static bool Installed => s_Settings != null;

        // Replaces the defaults (DefaultSettings, DefaultBelief, the search's
        // seat) for this process; before Install.
        public static void Configure(PracticeOptions options)
        {
            if (Installed)
            {
                throw new InvalidOperationException("Practice is configured before it is installed");
            }
            s_Options = options ?? new PracticeOptions();
        }

        public static void Install(HarmonyLib.Harmony harmony, Action<string> log)
        {
            if (Installed)
            {
                return;
            }
            s_Log = log;
            // Every installer, including a configured seat, needs a complete
            // local card table before any game hooks are applied.
            _ = CardRoles.Loaded;
            Assembly assembly = typeof(PracticeAI).Assembly;
            s_ThinkMs = ThinkMs();
            s_Settings = s_Options.Settings ?? DefaultSettings(assembly);
            // local_session.py sets the directory; without it, the project's
            // games directory, as Wine sees it.
            s_GamesDir = Environment.GetEnvironmentVariable("GWENT_GAMES_DIR");
            string project = Environment.GetEnvironmentVariable("GWENT_PROJECT_ROOT");
            if (string.IsNullOrEmpty(s_GamesDir) && !string.IsNullOrEmpty(project))
            {
                s_GamesDir = WinePath(project, ".runtime/human-games");
            }
            // The deck population, installed beside the mod by
            // the roster tool (it includes decks derived from
            // the user's own client, which stay in the client folder).
            string mods = Path.GetDirectoryName(assembly.Location);
            s_Roster = Roster.Load(Path.Combine(mods, "GwentAI-roster"), Path.Combine(mods, "GwentAI-layers.json"));
            s_RosterDecks = s_Roster.Count > 0 && Environment.GetEnvironmentVariable("GWENT_PRACTICE_DECKS") != "practice";
            List<DeckSpec> prior = s_Options.NoMeta ? new List<DeckSpec>() : s_Roster.Prior(Human);
            s_Belief = s_Options.Belief != null ? s_Options.Belief(prior) : DefaultBelief(prior);
            List<string> workers = WorkerSearch.Endpoints();
            if (workers.Count > 0)
            {
                s_Workers = new WorkerSearch(workers, s_Settings, s_Belief, prior, log);
            }
            if (ArenaMatch.Enabled)
            {
                // A failed arena install must not take the search patches
                // below down with it: Practice then plays without arena.
                try
                {
                    s_ArenaBelief = DefaultBelief(new List<DeckSpec>());
                    s_ArenaBelief.ArenaDeck = true;
                    ArenaMatch.Install(harmony, log);
                }
                catch (Exception ex)
                {
                    s_ArenaBelief = null;
                    log("Arena: not installed (Practice plays without it): " + ex);
                }
            }
            SearchPatches.Install(harmony);
            MoveLog.Install(harmony);
            AnswerLog.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(AIPlayer), nameof(AIPlayer.OnRequestReceived)),
                prefix: new HarmonyMethod(typeof(PracticeAI), nameof(OnRequestReceivedPrefix)));
            harmony.Patch(AccessTools.Method(typeof(AIPlayer), nameof(AIPlayer.OnCardMulligan)),
                prefix: new HarmonyMethod(typeof(PracticeAI), nameof(OnCardMulliganPrefix)));
            harmony.Patch(AccessTools.Method(typeof(GameController), nameof(GameController.SetupHumanVsAI)),
                postfix: new HarmonyMethod(typeof(PracticeAI), nameof(SetupHumanVsAIPostfix)));
            if (s_Options.SeatLabel != null)
            {
                log($"Practice AI: {s_Options.SeatLabel}; {s_Roster.Count} roster decks ({(s_RosterDecks ? "AI decks drawn from them" : "client Practice decks")}); " +
                    $"games recorded to {s_GamesDir ?? "(nowhere)"}");
                return;
            }
            log($"Practice AI: fair search{(s_Options.Label == null ? "" : " " + s_Options.Label)}, up to {s_Settings.Iterations} iterations or {s_ThinkMs} ms per decision (hard ones up to {s_ThinkMs * s_Settings.Extend} ms); " +
                $"{s_Roster.Count} roster decks ({(s_RosterDecks ? "AI decks drawn from them" : "client Practice decks")}); " +
                $"belief over {prior.Count} prior decks{(s_Belief.PosteriorDecks ? " (posterior decks)" : "")}{(s_Belief.OpponentStyle ? " (opponent style hand)" : "")}; " +
                (s_Settings.OpponentStyle ? (s_Settings.OpponentAnswers ? "opponent style rollouts with answers; " : "opponent style rollouts; ") : "") +
                $"games recorded to {s_GamesDir ?? "(nowhere)"}; " +
                (s_Workers == null ? "search in this process" : $"search on {s_Workers.Count} worker processes ({string.Join(", ", workers.ToArray())})") +
                "; " + PriorLine(s_Settings.Prior));
        }

        // The prior's name and the first 16 hex digits of the sha256 of the
        // bytes it was read from: for the default prior, the embedded net
        // after its card columns are filled (PolicyModel), which must equal
        // the fitted file's.
        private static string PriorLine(PolicyNet prior)
        {
            if (prior == null)
            {
                return "no policy prior";
            }
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                string hex = BitConverter.ToString(sha.ComputeHash(prior.Bytes)).Replace("-", "").ToLowerInvariant();
                return $"policy prior {prior.Name} sha256 {hex.Substring(0, 16)}";
            }
        }

        // The time budget per decision: GWENT_AI_THINK_MS, else DefaultThinkMs.
        public static int ThinkMs()
        {
            string thinkMs = Environment.GetEnvironmentVariable("GWENT_AI_THINK_MS");
            return string.IsNullOrEmpty(thinkMs) ? DefaultThinkMs : int.Parse(thinkMs);
        }

        // The simulation cap per decision: GWENT_AI_ITERATIONS, else DefaultIterations.
        public static int Iterations()
        {
            string iterations = Environment.GetEnvironmentVariable("GWENT_AI_ITERATIONS");
            return string.IsNullOrEmpty(iterations) ? DefaultIterations : int.Parse(iterations);
        }

        // The round evaluator, with the embedded value net from the default round on unless
        // UseDefaultValueNet is off. DefaultSettings calls it.
        private static IRoundEvaluator Scoring(Evaluator round)
        {
            return UseDefaultValueNet ? PracticeBudget.WithDefaultValueNet(round, loaded => { }) : round;
        }

        // The default search settings: Practice's own budget and the
        // defaults above.
        public static SearchSettings DefaultSettings(Assembly assembly)
        {
            return new SearchSettings
            {
                Iterations = Iterations(),
                TimeBudgetMs = ThinkMs(),
                Extend = DefaultPlay.ThinkExtend,
                MulliganScale = DefaultPlay.ThinkMulliganScale,
                NoEarlyPass = NoEarlyPass,
                EarlyPassHand = EarlyPassHand,
                EarlyPassMargin = -1,
                OpponentExploration = OpponentExploration,
                KeepPremium = KeepPremium,
                ThreatAware = ThreatAware,
                EconomyTieBreak = EconomyTieBreak,
                NoResign = NoResign,
                PassToWin = PassToWin,
                SearchRandom = SearchRandom,
                OpponentStyle = (DefaultPlay.OpponentStyle & 2) != 0,
                OpponentAnswers = (DefaultPlay.OpponentStyle & 4) != 0,
                SelfPlayStyle = (DefaultPlay.OpponentStyle & 8) != 0,
                Margin = Margin,
                RolloutAim = RolloutAim,
                DryPass = DryPass,
                CPuct = CPuct,
                PriorTemperature = PriorTemperature,
                Evaluator = Scoring(Evaluator.Parse(Resource(assembly, "eval/round.json"))),
                Prior = PolicyPrior ? PracticeBudget.LoadDefaultPrior() : null,
            };
        }

        // The opponent style's bits (DefaultPlay.OpponentStyle: 1 the
        // belief's hold weights, 2 the opponent seat's rollout play, 4 its
        // answer cards, 8 the self-play fitted set in place of the recorded
        // games'): DefaultSettings and DefaultBelief both read them.
        public static Belief DefaultBelief(List<DeckSpec> prior)
        {
            return new Belief(prior)
            {
                KeepDeckState = KeepDeckState,
                OpponentStyle = (DefaultPlay.OpponentStyle & 1) != 0,
                SelfPlayStyle = (DefaultPlay.OpponentStyle & 8) != 0,
                PosteriorDecks = PosteriorDecks,
            };
        }

        // The installed search settings, belief and worker search (null:
        // none), for a seat that Configure's SeatFactory builds.
        public static SearchSettings Settings => s_Settings;
        public static Belief BeliefModel => s_Belief;
        public static ISearch Workers => s_Workers;
        // The current game's record file name; null when none is recorded.
        public static string RecordFile => s_Record?.FileName;

        private static string WinePath(string project, string relative)
        {
            return "Z:" + (project + "/" + relative).Replace('/', '\\');
        }

        // Call once per frame, on the main thread.
        public static void Update()
        {
            foreach (KeyValuePair<AIPlayer, Seat> entry in s_Seats.ToList())
            {
                if (entry.Value.Finished)
                {
                    s_Seats.Remove(entry.Key);
                    continue;
                }
                entry.Value.Tick();
            }
        }

        // A text resource embedded in the mod.
        public static string Resource(Assembly assembly, string name)
        {
            using (Stream stream = assembly.GetManifestResourceStream(name))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private static Seat SeatOf(AIPlayer player)
        {
            if (!player.CurrentGameController.IsMainController)
            {
                return null;
            }
            if (!s_Seats.TryGetValue(player, out Seat seat))
            {
                seat = s_Seats[player] = s_Options.SeatFactory != null
                    ? new Seat(player, s_Options.SeatFactory(player))
                    : s_ArenaGame
                        // In this process: the workers hold the roster belief.
                        ? new Seat(player, new FairSeat(new FairAgent(s_Settings, s_ArenaBelief, checkFairness: false, null)))
                        : new Seat(player, new FairSeat(new FairAgent(s_Settings, s_Belief, checkFairness: false, s_Workers)));
            }
            return seat;
        }

        // A seat's deck as dealt: name, faction, leader and cards in deal order.
        public static Dictionary<string, object> DeckOf(GameController game, EPlayerId id)
        {
            BattleDeck deck = game.GameInstance.Settings.GetPlayerSettings(id).Deck;
            return new Dictionary<string, object>
            {
                ["name"] = deck.Name,
                ["faction"] = deck.FactionId.ToString(),
                ["leader"] = deck.Leader.TemplateId,
                ["cards"] = deck.Cards.Select(card => card.TemplateId).ToList(),
            };
        }

        private static bool OnRequestReceivedPrefix(AIPlayer __instance, ARequestAction requestAction)
        {
            Seat seat = SeatOf(__instance);
            if (seat == null)
            {
                return true;
            }
            if (requestAction.TargetPlayer == __instance.Id)
            {
                seat.Pending = requestAction;
            }
            return false;
        }

        private static bool OnCardMulliganPrefix(AIPlayer __instance, RequestMulliganAction currentRequest)
        {
            Seat seat = SeatOf(__instance);
            if (seat == null)
            {
                return true;
            }
            if (currentRequest.NumMulligans > 0)
            {
                seat.Pending = currentRequest;
            }
            return false;
        }

        // A new Practice game: seed its random generator so the record can be
        // replayed, and start recording.
        private static void SetupHumanVsAIPostfix(GameController __instance, EPlayerId humanPlayerID)
        {
            if (!__instance.IsMainController)
            {
                return;
            }
            int seed;
            EStartingPlayer starter;
            lock (s_Random)
            {
                seed = NextGameSeed ?? s_Random.Next();
                starter = NextGameStarter ?? Starter(seed);
                NextGameSeed = null;
                NextGameStarter = null;
            }
            __instance.RandomGenerator.Copy(new MersenneTwisterRandom(seed));
            // Before LiveGame.Begin, which records the starter.
            __instance.GameInstance.Settings.StartingPlayer = starter;
            // An arena match (ArenaMatch, GWENT_AI_ARENA=1): the run's
            // drafted deck in the human's seat, a drafted deck in the AI's.
            ArenaMatch.Game arena = s_ArenaBelief != null ? ArenaMatch.TakeNext() : null;
            s_ArenaGame = arena != null;
            if (arena != null)
            {
                EPlayerId ai = __instance.PlayerManager.GetOpponentPlayerId(humanPlayerID);
                __instance.GameInstance.Settings.GetPlayerSettings(humanPlayerID).Deck = arena.HumanDeck;
                __instance.GameInstance.Settings.GetPlayerSettings(ai).Deck = arena.AiDeck.ToBattleDeck();
                s_Log($"Arena AI deck: {arena.AiDeck.Name}, {arena.AiDeck.Cards.Sum(card => card.Count)} cards");
            }
            else if (s_RosterDecks)
            {
                // The AI's opponent deck from the population, in place of
                // the client's Practice deck, before the game deals it.
                DeckSpec deck;
                lock (s_Random)
                {
                    deck = s_Roster.Draw(s_Random, Human);
                }
                if (deck != null)
                {
                    EPlayerId ai = __instance.PlayerManager.GetOpponentPlayerId(humanPlayerID);
                    __instance.GameInstance.Settings.GetPlayerSettings(ai).Deck = deck.ToBattleDeck();
                    s_Log($"Practice AI deck: {deck.Name} ({deck.Kind})");
                }
            }
            // A game that never reached its end (left without a result) is
            // closed here: its record written as abandoned, its seat dropped.
            s_Record?.End(null, "Abandoned");
            foreach (Seat left in s_Seats.Values)
            {
                left.Leave();
            }
            s_Seats.Clear();
            LiveGame live = LiveGame.Begin(__instance, seed);
            // Before the record, whose game-end listener then sees the last taunt.
            s_Emotes = PracticeEmotes.Begin(__instance, humanPlayerID, seed, s_Log);
            if (s_GamesDir != null)
            {
                s_Record = new GameRecord(__instance, humanPlayerID, live, s_GamesDir, s_Settings, s_Seats, s_Emotes, s_Log, arena);
                s_Record.Begin();
            }
            if (arena != null)
            {
                // After the record's listener: the record file is named by then.
                string record = s_Record?.FileName;
                __instance.EventManager.OnGameEnded.CoreEvent.AddListener((winner, reason) => ArenaMatch.Report(winner, humanPlayerID, seed, record));
            }
        }

        private static int NextSeed()
        {
            lock (s_Random)
            {
                return s_Random.Next();
            }
        }

        private class Seat
        {
            private readonly AIPlayer m_Player;
            // Who answers: the search (FairSeat) or Configure's seat.
            public readonly IPracticeSeat Answerer;
            public ARequestAction Pending;
            private Job m_Job;
            private bool m_ReportedStall;
            // Failed answers for the open request.
            private int m_Retries;

            public bool Finished { get; private set; }

            public Seat(AIPlayer player, IPracticeSeat answerer)
            {
                m_Player = player;
                Answerer = answerer;
                player.CurrentGameController.EventManager.OnGameEnded.CoreEvent.AddListener((winner, reason) =>
                {
                    Finished = true;
                    answerer.GameEnded(winner);
                });
            }

            // Its game was left without a result.
            public void Leave()
            {
                if (Finished)
                {
                    return;
                }
                Finished = true;
                Answerer.Left();
            }

            private class Job
            {
                public ARequestAction Request;
                public readonly Stopwatch Clock = Stopwatch.StartNew();
                public TimeSpan ThinkTime;
                public List<Move> Moves;
                public Exception Error;
                public volatile bool Done;
            }

            public void Tick()
            {
                if (m_Job == null)
                {
                    Start();
                }
                else if (m_Job.Done && m_Job.Clock.Elapsed >= m_Job.ThinkTime)
                {
                    Finish();
                }
            }

            private void Start()
            {
                ARequestAction request = Pending;
                GameController live = m_Player.CurrentGameController;
                if (request == null)
                {
                    return;
                }
                if (request.Expired || !live.RequestManager.Requests.Contains(request))
                {
                    Pending = null;
                    return;
                }
                // As the stock AI does: some effects briefly forbid both
                // playing and passing; wait them out.
                BattleRestrictions restrictions = m_Player.Restrictions;
                if (request is RequestPlayCardAction && !restrictions.CanMakePlayableAction && !restrictions.ForcePass)
                {
                    return;
                }
                // The request can open while the engine still holds queued
                // actions (Replay.Settled). The workers replay the game with
                // them applied, so decide from the same position.
                if (!Replay.Settled(live))
                {
                    return;
                }
                Pending = null;
                if (s_Workers != null)
                {
                    s_Workers.MoveCount = LiveGame.Current?.Moves.Count;
                }
                Playout snapshot = Playout.Clone(live, (players, id) => new Player(players, id));
                ARequestAction copy = snapshot.Game.RequestManager.Requests.First(open => open.Id == request.Id && open.TargetPlayer == m_Player.Id);
                Player seat = snapshot.Game.PlayerManager.GetPlayer(m_Player.Id);
                Job job = m_Job = new Job { Request = request, ThinkTime = ThinkTime(request) };
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        job.Moves = Answerer.Answer(snapshot.Game, seat, copy, decision => NextSeed());
                    }
                    catch (Exception ex)
                    {
                        job.Error = ex;
                    }
                    job.Done = true;
                });
            }

            private void Finish()
            {
                Job job = m_Job;
                m_Job = null;
                ARequestAction request = job.Request;
                if (request.Expired || !m_Player.CurrentGameController.RequestManager.Requests.Contains(request))
                {
                    return;
                }
                List<Move> moves = job.Moves;
                if (job.Error != null && m_Retries++ < Answerer.Retries)
                {
                    // Ask again rather than play for the seat's player.
                    s_Log("Practice AI: the seat's decision failed; asking again: " + job.Error);
                    Pending = request;
                    return;
                }
                m_Retries = 0;
                if (job.Error != null)
                {
                    // Keep the game going and leave the failure in the log.
                    s_Log("Practice AI search failed; playing the first legal answer: " + job.Error);
                    moves = FirstLegal(request);
                }
                // A taunt, if the persona has one for this moment, before the play shows.
                s_Emotes?.Decided(m_Player, request, moves, Answerer.Stats?.LastOrDefault());
                foreach (Move move in moves)
                {
                    move.Apply(m_Player, request);
                }
                DecisionStat last = Answerer.Stats?.LastOrDefault();
                if (!m_ReportedStall && last != null && last.Stall != null)
                {
                    m_ReportedStall = true;
                    s_Log($"Practice AI: {last.Unfinished}/{last.Iterations} simulations did not finish; first: {last.Stall}");
                }
            }

            private List<Move> FirstLegal(ARequestAction request)
            {
                List<string> taken = new List<string>();
                List<Move> moves = new List<Move>();
                while (true)
                {
                    List<GameAction> legal = Actions.Legal(request, m_Player, taken);
                    if (legal.Count == 0)
                    {
                        return moves;
                    }
                    taken.Add(legal[0].Key);
                    moves.Add(legal[0].Move);
                    if (legal[0].Move.EndsResponse || !SimPlayer.IsMultiStep(request))
                    {
                        return moves;
                    }
                }
            }

            // A short, varied pause, so quick answers do not tell anything
            // and the game does not feel instant.
            private static TimeSpan ThinkTime(ARequestAction request)
            {
                double seconds;
                lock (s_Random)
                {
                    seconds = request is RequestPlayCardAction ? 0.8 + s_Random.NextDouble() * 1.4 : 0.4 + s_Random.NextDouble() * 0.6;
                }
                return TimeSpan.FromSeconds(seconds);
            }
        }

        // One human-vs-AI Practice game: each is one self-contained file with
        // everything needed to replay it, plus a line in index.jsonl.
        //
        // Format (schema 1): seats; both decks in deal order (the order
        // decides the shuffle and the card instance ids); seed and match
        // settings; every move in Move text form with its round; result;
        // the AI's build, settings and decisions.
        private class GameRecord
        {
            private const int Schema = 1;
            private readonly GameController m_Game;
            private readonly EPlayerId m_Human;
            private readonly LiveGame m_Live;
            private readonly string m_Directory;
            private readonly string m_Name;
            private readonly SearchSettings m_Settings;
            private readonly Dictionary<AIPlayer, Seat> m_Seats;
            private readonly PracticeEmotes m_Emotes;
            private readonly Action<string> m_Log;
            // The arena match, or null for a Practice game.
            private readonly ArenaMatch.Game m_Arena;
            private readonly DateTime m_Started = DateTime.UtcNow;
            private bool m_Ended;
            private readonly List<Dictionary<string, object>> m_Answers = new List<Dictionary<string, object>>();

            public GameRecord(GameController game, EPlayerId human, LiveGame live, string directory, SearchSettings settings,
                              Dictionary<AIPlayer, Seat> seats, PracticeEmotes emotes, Action<string> log, ArenaMatch.Game arena)
            {
                m_Arena = arena;
                m_Emotes = emotes;
                m_Game = game;
                m_Human = human;
                m_Live = live;
                m_Settings = settings;
                m_Seats = seats;
                m_Log = log;
                m_Directory = directory;
                Directory.CreateDirectory(directory);
                m_Name = m_Started.ToString("yyyyMMdd-HHmmss") + "-" + live.Seed;
            }

            public string FileName => m_Name + ".json";

            public void Begin()
            {
                m_Game.EventManager.OnGameEnded.CoreEvent.AddListener(OnGameEnded);
                AnswerLog.Reset();
                AnswerLog.Logged += OnAnswer;
            }

            // Attacks on units, for measuring answers (AnswerLog).
            private void OnAnswer(Dictionary<string, object> entry)
            {
                lock (m_Answers)
                {
                    m_Answers.Add(entry);
                }
            }

            private void OnGameEnded(EPlayerId winner, EEndGameReason reason)
            {
                End(winner == EPlayerId.AllPlayers ? "draw" : winner.ToString(), reason.ToString());
            }

            // Writes the record once. Winner: "P1", "P2", "draw", or null for
            // a game left without a result.
            public void End(string winner, string reason)
            {
                if (m_Ended)
                {
                    return;
                }
                m_Ended = true;
                AnswerLog.Logged -= OnAnswer;
                try
                {
                    Dictionary<string, object> game = Game(winner, reason);
                    File.WriteAllText(Path.Combine(m_Directory, m_Name + ".json"), JsonConvert.SerializeObject(game, Formatting.Indented));
                    File.AppendAllText(Path.Combine(m_Directory, "index.jsonl"), JsonConvert.SerializeObject(Index(game)) + "\n");
                }
                catch (Exception ex)
                {
                    m_Log("Could not record the Practice game: " + ex);
                }
            }

            private Dictionary<string, object> Game(string winner, string reason)
            {
                GwentGameplay.Settings.BattleSettings settings = m_Game.GameInstance.Settings;
                Seat ai = m_Seats.Where(entry => entry.Key.CurrentGameController == m_Game).Select(entry => entry.Value).FirstOrDefault();
                Assembly build = typeof(PracticeAI).Assembly;
                Dictionary<string, object> search = SearchAgent(build);
                Dictionary<string, object> game = new Dictionary<string, object>
                {
                    ["schema"] = Schema,
                    ["kind"] = m_Arena == null ? "practice" : "arena",
                    ["file"] = m_Name + ".json",
                    ["started"] = m_Started.ToString("o"),
                    ["human"] = m_Human.ToString(),
                    ["ai"] = m_Game.PlayerManager.GetOpponentPlayerId(m_Human).ToString(),
                    ["seed"] = m_Live.Seed,
                    ["starting"] = settings.StartingPlayer.ToString(),
                    ["p1_deck"] = Deck(EPlayerId.P1),
                    ["p2_deck"] = Deck(EPlayerId.P2),
                    ["moves"] = m_Live.Moves.Select(entry => new Dictionary<string, object> { ["round"] = entry.Round, ["move"] = entry.Move }).ToList(),
                    ["winner"] = winner,
                    ["end_reason"] = reason,
                    ["round_scores"] = m_Game.RoundManager.Rounds.Where(round => round != null && round.Finished).Select(round => round.PlayerScores).ToList(),
                    ["round_winners"] = m_Game.RoundManager.Rounds.Where(round => round != null && round.Finished).Select(round => round.WinnerId.ToString()).ToList(),
                    // No seat yet: the search's entry, unless Configure replaced the seat.
                    ["agent"] = ai != null ? ai.Answerer.RecordAgent(search) : s_Options.SeatFactory == null ? search : null,
                    ["ai_decisions"] = ai?.Answerer.Stats,
                    ["answers"] = m_Answers,
                };
                // With emotes on: the persona and the taunts ("taunts").
                m_Emotes?.Record(game);
                // An arena match: the run before it and the AI's draft (both
                // dealt decks are p1_deck and p2_deck).
                if (m_Arena != null)
                {
                    game["arena"] = new Dictionary<string, object>
                    {
                        ["wins_before"] = m_Arena.WinsBefore,
                        ["lives"] = m_Arena.Lives,
                        ["ai_draft"] = m_Arena.AiDeck,
                    };
                }
                return game;
            }

            // The search's entry in the record ("agent").
            private Dictionary<string, object> SearchAgent(Assembly build)
            {
                return new Dictionary<string, object>
                {
                    ["build"] = build.ManifestModule.ModuleVersionId.ToString(),
                    ["spec"] = s_Options.Label,
                    ["iterations"] = m_Settings.Iterations,
                    ["time_budget_ms"] = m_Settings.TimeBudgetMs,
                    ["extend"] = m_Settings.Extend,
                    ["exploration"] = m_Settings.Exploration,
                    ["group_plays"] = m_Settings.GroupPlays,
                    ["no_early_pass"] = m_Settings.NoEarlyPass,
                    ["early_pass_hand"] = m_Settings.EarlyPassHand,
                    ["early_pass_margin"] = m_Settings.EarlyPassMargin,
                    ["no_resign"] = m_Settings.NoResign,
                    ["pass_to_win"] = m_Settings.PassToWin,
                    ["economy_tie_break"] = m_Settings.EconomyTieBreak,
                    ["two_round_horizon"] = m_Settings.TwoRoundHorizon,
                    ["search_random"] = m_Settings.SearchRandom,
                    ["tie_margin"] = m_Settings.TieMargin,
                    ["rollout_aim"] = m_Settings.RolloutAim,
                    ["opponent_exploration"] = m_Settings.OpponentExploration,
                    ["keep_premium"] = m_Settings.KeepPremium,
                    ["prior_temperature"] = m_Settings.PriorTemperature,
                    ["opponent_temperature"] = m_Settings.OpponentTemperature,
                    ["opponent_policy"] = m_Settings.OpponentPolicy == null ? null : m_Settings.OpponentPolicy.Name,
                    ["class_b_policy"] = m_Settings.ClassBPolicy == null ? null : m_Settings.ClassBPolicy.Name,
                    ["c_puct"] = m_Settings.CPuct,
                    ["dry_pass"] = m_Settings.DryPass,
                    ["margin"] = m_Settings.Margin,
                    ["threat_aware"] = m_Settings.ThreatAware,
                    ["policy_prior"] = m_Settings.Prior == null ? null : m_Settings.Prior.Name,
                    ["opponent_prior"] = m_Settings.OpponentPrior == null ? null : m_Settings.OpponentPrior.Name,
                    ["deck_state"] = s_Belief.KeepDeckState,
                    ["posterior_decks"] = s_Belief.PosteriorDecks,
                    ["opponent_style"] = (s_Belief.OpponentStyle ? 1 : 0) | (m_Settings.OpponentStyle ? 2 : 0) | (m_Settings.OpponentAnswers ? 4 : 0) | (m_Settings.SelfPlayStyle ? 8 : 0),
                    ["workers"] = s_Workers == null ? 0 : s_Workers.Count,
                };
            }

            // One line per game for quick statistics.
            private static Dictionary<string, object> Index(Dictionary<string, object> game)
            {
                string human = (string)game["human"];
                Dictionary<string, object> mine = (Dictionary<string, object>)game[human == "P1" ? "p1_deck" : "p2_deck"];
                Dictionary<string, object> theirs = (Dictionary<string, object>)game[human == "P1" ? "p2_deck" : "p1_deck"];
                string winner = (string)game["winner"];
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["file"] = game["file"],
                    ["started"] = game["started"],
                    ["result"] = winner == null ? "abandoned" : winner == "draw" ? "draw" : winner == human ? "human" : "ai",
                    ["human_faction"] = mine["faction"],
                    ["human_leader"] = mine["leader"],
                    ["ai_faction"] = theirs["faction"],
                    ["ai_leader"] = theirs["leader"],
                    ["round_scores"] = game["round_scores"],
                    ["agent"] = game["agent"],
                };
                // Arena rows say so; Practice rows keep their shape.
                if ((string)game["kind"] == "arena")
                {
                    row["kind"] = "arena";
                }
                return row;
            }

            private Dictionary<string, object> Deck(EPlayerId id)
            {
                return DeckOf(m_Game, id);
            }
        }
    }
}
