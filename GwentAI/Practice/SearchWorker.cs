using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GwentAI.Learn;
using GwentAI.Search;
using GwentGameplay;
using RedLogger;

namespace GwentAI.Practice
{
    // A search worker: a headless host process that rebuilds the live game
    // from its record (Replica) and searches a decision with its own seed,
    // reporting root statistics to the coordinator (WorkerSearch) that sums
    // them over every worker. Protocol: SearchProtocol.cs. One connection at
    // a time; the engine simulates on one thread per process.
    //
    //   mono -O=-float32 GwentHost.exe --client DIR --loader DIR --search-worker [--port P] [--memory-limit MB]
    public class SearchWorker
    {
        // Root statistics go to the coordinator this often while searching.
        public const int ProgressEveryMs = 100;

        // Exit code once the working set passes the memory limit; the
        // supervisor starts a new worker.
        public const int MemoryLimitExitCode = 3;

        private class Context
        {
            public WorkerMessage Setup;
            public Belief Belief;
        }

        private readonly GameSharedData m_Shared;
        private readonly IRedLogger m_Logger;
        private readonly Action<string> m_Log;
        private readonly List<KeyValuePair<string, Context>> m_Contexts = new List<KeyValuePair<string, Context>>();
        private Replica m_Replica;
        private NetworkStream m_Stream;
        private StreamReader m_In;
        private StreamWriter m_Out;

        private SearchWorker(GameSharedData shared, IRedLogger logger, Action<string> log)
        {
            m_Shared = shared;
            m_Logger = logger;
            m_Log = log;
        }

        // Serves coordinators forever. Port 0 picks a free port; the port is
        // logged as "listening PORT".
        public static int Run(GameSharedData shared, IRedLogger logger, int port, long memoryLimitMB, Action<string> log)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, port);
            // A restarted worker takes its predecessor's port at once.
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            log($"listening {((IPEndPoint)listener.LocalEndpoint).Port}");
            Timer watch = memoryLimitMB > 0 ? new Timer(_ => CheckMemory(memoryLimitMB, log), null, 0, 100) : null;
            SearchWorker worker = new SearchWorker(shared, logger, log);
            while (true)
            {
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    try
                    {
                        worker.Serve(client);
                    }
                    catch (Exception ex)
                    {
                        log("connection ended: " + ex.Message);
                    }
                }
            }
        }

        private static void CheckMemory(long limitMB, Action<string> log)
        {
            using (Process process = Process.GetCurrentProcess())
            {
                long usedMB = process.WorkingSet64 / (1024 * 1024);
                if (usedMB > limitMB)
                {
                    log($"{usedMB} MB in use exceeds the {limitMB} MB limit; exiting");
                    Environment.Exit(MemoryLimitExitCode);
                }
            }
        }

        private void Serve(TcpClient client)
        {
            client.NoDelay = true;
            m_Stream = client.GetStream();
            m_In = new StreamReader(m_Stream, Encoding.UTF8);
            m_Out = new StreamWriter(m_Stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            m_Log("coordinator connected");
            string line;
            while ((line = m_In.ReadLine()) != null)
            {
                WorkerMessage message = WorkerMessage.Parse(line);
                switch (message.Type)
                {
                    case WorkerMessage.Setup:
                        Store(message);
                        break;
                    case WorkerMessage.Search:
                        Search(message);
                        break;
                    case WorkerMessage.Stop:
                        // Late: the search it meant has already reported.
                        break;
                    default:
                        m_Log("unknown message type " + message.Type);
                        break;
                }
            }
            m_Log("coordinator disconnected");
        }

        private void Store(WorkerMessage setup)
        {
            Belief belief = new Belief(setup.Prior);
            WireFields.Copy(setup.Belief, belief);
            Context context = new Context { Setup = setup, Belief = belief };
            // Setups of the current game only (one per seat): the key is
            // "<game>/<seat>", as the coordinator forms it.
            string game = setup.Context.Substring(0, setup.Context.LastIndexOf('/') + 1);
            m_Contexts.RemoveAll(entry => entry.Key == setup.Context || !entry.Key.StartsWith(game, StringComparison.Ordinal));
            m_Contexts.Add(new KeyValuePair<string, Context>(setup.Context, context));
            SearchSettings settings = setup.Parameters;
            m_Log($"setup {setup.Context}: seed {setup.Seed}, {setup.Observer}, {(setup.Prior == null ? 0 : setup.Prior.Count)} prior decks{(belief.PosteriorDecks ? " (posterior decks)" : "")}{(belief.OpponentStyle ? " (opponent style)" : "")}, " +
                  $"{Scoring(settings.Evaluator)}{(settings.Prior != null ? ", policy prior " + settings.Prior.Name : "")}{(settings.RolloutPolicy != null ? ", rollout policy " + settings.RolloutPolicy.Name : "")}" +
                  $"{(settings.Cutoff > 0 ? ", cutoff " + settings.Cutoff : "")}{(settings.Oracle ? ", oracle (sees the hidden cards)" : "")}{(settings.HandOracle ? ", hand oracle (sees the opponent's hand)" : "")}");
        }

        private static string Scoring(IRoundEvaluator evaluator)
        {
            switch (evaluator)
            {
                case null:
                    return "games played out";
                case ValueNet net:
                    return "value net " + net.Name;
                case BlendEvaluator blend:
                    return $"value net {(blend.Net as ValueNet)?.Name} blended {blend.NetWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)} with the round evaluator";
                case RoundGateEvaluator gate:
                    return $"{Scoring(gate.Net)} from round {gate.FromRound}, the round evaluator before";
                default:
                    return "round evaluator";
            }
        }

        // What the coordinator sent for one search: its settings, but for
        // what the worker's own search owns. The caps (Iterations,
        // TimeBudgetMs) are the search message's, Extend is 1 because the
        // coordinator applies the stopping rule to the summed roots, and
        // Trees is 1 because the workers are the coordinator's trees
        // (WorkerSearch.TreeSeeds): a worker that ran the coordinator's
        // Trees itself would search more trees than the settings name.
        public static SearchSettings WorkerSettings(SearchSettings sent, int iterations, int timeBudgetMs, bool wholeMatch = false)
        {
            SearchSettings settings = wholeMatch ? sent.WholeMatch(timeBudgetMs) : sent.Clone();
            settings.Iterations = iterations;
            settings.TimeBudgetMs = timeBudgetMs;
            settings.Extend = 1;
            settings.Trees = 1;
            return settings;
        }

        private void Send(WorkerMessage message)
        {
            m_Out.WriteLine(message.Serialize());
        }

        // The coordinator's answer to a checkpoint: true to go on (resume),
        // false to stop (stop, or a connection that ended). Messages for
        // another search are late and ignored.
        private bool AwaitVerdict(int searchId)
        {
            while (true)
            {
                string line = m_In.ReadLine();
                if (line == null)
                {
                    return false;
                }
                WorkerMessage message = WorkerMessage.Parse(line);
                if (message.Id == searchId && message.Type == WorkerMessage.Resume)
                {
                    return true;
                }
                if (message.Id == searchId && message.Type == WorkerMessage.Stop)
                {
                    return false;
                }
            }
        }

        private void Fail(WorkerMessage search, string message)
        {
            m_Log($"search {search.Id}: {message}");
            Send(new WorkerMessage { Type = WorkerMessage.Error, Id = search.Id, Message = message });
        }

        private void Search(WorkerMessage search)
        {
            Context context = m_Contexts.Find(entry => entry.Key == search.Context).Value;
            if (context == null)
            {
                Fail(search, "unknown context " + search.Context);
                return;
            }
            WorkerMessage setup = context.Setup;
            Stopwatch clock = Stopwatch.StartNew();
            m_Replica?.Dispose();
            m_Replica = null;
            EPlayerId seat = (EPlayerId)Enum.Parse(typeof(EPlayerId), setup.Observer);
            ERequestType type = (ERequestType)Enum.Parse(typeof(ERequestType), search.Request);
            ARequestAction request;
            try
            {
                m_Replica = new Replica(m_Shared, m_Logger, setup.Seed, setup.Starting, setup.P1, setup.P2, search.Moves ?? new List<string>());
                request = m_Replica.Reach(seat, type, out string error);
                if (request == null)
                {
                    Fail(search, "replay failed: " + error);
                    return;
                }
            }
            catch (Exception ex)
            {
                Fail(search, "replay threw: " + ex);
                return;
            }
            double replayMs = clock.Elapsed.TotalMilliseconds;
            GameController game = m_Replica.Game;
            Player me = game.PlayerManager.GetPlayer(seat);
            // This search runs to its caps unless the coordinator says stop.
            SearchSettings settings = WorkerSettings(setup.Parameters, search.Iterations, search.TimeBudgetMs, search.RootKeys != null);
            // The steps already chosen in this response, by key.
            List<GameAction> prefix = new List<GameAction>();
            List<string> keys = new List<string>();
            foreach (string key in search.Prefix ?? new List<string>())
            {
                GameAction step = Actions.Legal(request, me, keys, settings.GroupPlays).Find(action => action.Key == key);
                if (step == null)
                {
                    Fail(search, $"prefix step {key} is not legal in the replayed game");
                    return;
                }
                prefix.Add(step);
                keys.Add(key);
            }
            string fingerprint = Fingerprint.Of(game, seat, request, keys, settings.GroupPlays);
            string hidden = Fingerprint.Hidden(game, settings);
            Send(new WorkerMessage { Type = WorkerMessage.Ready, Id = search.Id, Fingerprint = fingerprint, Hidden = hidden, ReplayMs = replayMs, Updates = m_Replica.Updates });
            if (search.Fingerprint != null && search.Fingerprint != fingerprint)
            {
                // The coordinator will not count this search; save the CPU.
                m_Log($"search {search.Id}: position differs from the live game after {m_Replica.Applied} moves\n  live:   {search.Fingerprint}\n  replay: {fingerprint}");
                return;
            }
            if (search.Hidden != hidden)
            {
                m_Log($"search {search.Id}: hidden cards differ from the live game after {m_Replica.Applied} moves\n  live:   {search.Hidden}\n  replay: {hidden}");
                return;
            }
            bool stopped = false;
            long lastProgress = 0;
            int checkpoints = 0;
            bool workBudget = search.CheckpointUnits > 0;
            Ismcts ismcts = new Ismcts(settings, context.Belief)
            {
                RootOnly = search.RootKeys == null ? null : new HashSet<string>(search.RootKeys),
                Remembered = WorkerMessage.RememberedMap(search.Remembered),
                // A work-budget search reports at its checkpoints and the
                // coordinator's verdict stops it; a clocked one reports its
                // progress and stops when told.
                CheckpointUnits = search.CheckpointUnits,
                OnCheckpoint = !workBudget ? (Func<int, WorkTally, List<RootStat>, bool>)null : (iterations, work, ranked) =>
                {
                    Send(new WorkerMessage
                    {
                        Type = WorkerMessage.Checkpoint, Id = search.Id, Index = ++checkpoints, Iterations = iterations, Work = work,
                        Roots = RootReport.Of(ranked, false),
                    });
                    stopped = !AwaitVerdict(search.Id);
                    return !stopped;
                },
                Monitor = workBudget ? (Func<int, List<RootStat>, bool>)null : (iteration, ranked) =>
                {
                    if (m_Stream.DataAvailable)
                    {
                        string line = m_In.ReadLine();
                        WorkerMessage message = line == null ? null : WorkerMessage.Parse(line);
                        if (message == null || (message.Type == WorkerMessage.Stop && message.Id == search.Id))
                        {
                            stopped = true;
                            return false;
                        }
                    }
                    if (clock.ElapsedMilliseconds - lastProgress >= ProgressEveryMs)
                    {
                        lastProgress = clock.ElapsedMilliseconds;
                        Send(new WorkerMessage { Type = WorkerMessage.Progress, Id = search.Id, Iterations = iteration, Roots = RootReport.Of(ranked, false) });
                    }
                    return true;
                },
            };
            SearchResult result;
            try
            {
                result = ismcts.Search(game, seat, request, prefix, search.SearchSeed);
            }
            catch (Exception ex)
            {
                Fail(search, "search failed: " + ex.Message);
                return;
            }
            Send(new WorkerMessage
            {
                Type = WorkerMessage.Done, Id = search.Id, Iterations = result.Iterations, Unfinished = result.Unfinished, Evaluated = result.Evaluated,
                CloneMs = result.CloneMs, DeterminizeMs = result.DeterminizeMs, RunMs = result.RunMs, DecideMs = result.DecideMs, Updates = result.Updates,
                Stall = result.Stall, ShownMismatches = result.ShownMismatches, Roots = RootReport.Of(result.Root, true), Work = result.Work,
            });
            m_Log($"search {search.Id}: {type} after {m_Replica.Applied} moves, replay {replayMs:F0} ms ({m_Replica.Updates} updates), " +
                  $"{result.Iterations} iterations in {clock.ElapsedMilliseconds - (long)replayMs} ms{(stopped ? " (stopped)" : "")}, best {result.Root[0].Key}");
        }
    }
}
