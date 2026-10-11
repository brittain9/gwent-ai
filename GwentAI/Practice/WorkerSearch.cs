using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GwentAI.Search;
using GwentGameplay;

namespace GwentAI.Practice
{
    // Root parallelization across processes (Chaslot, Winands and van den
    // Herik 2008): the same decision searched by K worker processes
    // (SearchWorker), each with its own seed, their root statistics summed
    // as SearchSettings.Trees sums trees in one process. The workers replay
    // the live game from its record (LiveGame) and must reach the same
    // position: each reports a fingerprint, and a worker whose position
    // differs does not count. The coordinator applies the stopping rule
    // (Ismcts.Continue) to the summed roots, with the budget's share used
    // measured over all workers, and tells them to stop.
    //
    // The game never waits on the workers: when none is ready within a grace
    // period, or a decision loses a worker, the search runs in this
    // process (Ismcts) as without workers, and the result says so (Source).
    //
    // Deterministic without a time budget and without Extend: each worker
    // runs its N iterations and is never stopped early, so the result
    // depends on the seed alone and equals that of K trees in one process
    // (SearchSettings.Trees).
    //
    // Deterministic with a work budget (SearchSettings.WorkBudgetMs)
    // too, though the stopping rule runs: the workers stop at fixed amounts
    // of work (WorkCheckpoints), the coordinator waits for all of them,
    // applies the rule to their summed roots and tells them to go on or stop.
    // No clock enters, no worker may be missing, and a search that cannot run
    // so is an error, not a search in this process.
    public class WorkerSearch : ISearch, IWholeMatchSearch
    {
        // Comma-separated worker endpoints: "host:port" or "port" (localhost).
        public const string EndpointsVariable = "GWENT_SEARCH_WORKERS";

        // A connection to a worker process. Connections are shared by every
        // WorkerSearch in the process (the two seats): a worker serves
        // one connection at a time, and searches never overlap in a process.
        private class Worker
        {
            public string Host;
            public int Port;
            public TcpClient Client;
            public StreamWriter Out;
            // The setups this connection has sent, for the current game.
            public readonly HashSet<string> Contexts = new HashSet<string>();
            public string Game;
            public bool Failed;
            // The search in flight and its answers.
            public int Id;
            public WorkerMessage Ready, Progress, Checkpoint, Done, Error;

            public string Name => Host + ":" + Port;
        }

        private static readonly Dictionary<string, Worker> s_Pool = new Dictionary<string, Worker>();
        private static readonly object s_Sync = new object();
        private readonly List<Worker> m_Workers = new List<Worker>();
        private readonly SearchSettings m_Settings;
        private readonly Belief m_Belief;
        private readonly List<DeckSpec> m_Prior;
        private readonly Action<string> m_Log;
        private readonly Ismcts m_Local;
        private IDictionary<ushort, int> m_Remembered;
        private int m_NextId;
        // Fingerprint mismatches already reported, by worker and game.
        private readonly HashSet<string> m_Reported = new HashSet<string>();

        // Time for every worker to replay the game and report ready, and
        // after a stop, to report done. Deterministic searches wait long:
        // a result must not depend on load.
        public int ReadyGraceMs;
        public int DoneGraceMs;
        public int ConnectTimeoutMs = 500;
        public const int DeterministicWaitMs = 600000;

        public IDictionary<ushort, int> Remembered
        {
            get => m_Remembered;
            set
            {
                m_Remembered = value;
                m_Local.Remembered = value;
            }
        }

        public int Count => m_Workers.Count;
        // The number of the live game's moves the position holds, when the
        // caller searches a copy taken earlier (Practice copies the game on
        // the main thread and searches on another); null: all moves so far.
        public int? MoveCount;
        public bool Deterministic => m_Settings.IsDeterministic();

        public static List<string> Endpoints()
        {
            string value = Environment.GetEnvironmentVariable(EndpointsVariable);
            return string.IsNullOrEmpty(value) ? new List<string>()
                : value.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
        }

        // Prior: the decks the belief was built from (sent to the workers).
        // The workers search with the settings and the belief as they are
        // here, models included (SearchProtocol); a settings object the wire
        // cannot carry is refused now, not at the first decision.
        public WorkerSearch(IEnumerable<string> endpoints, SearchSettings settings, Belief belief, List<DeckSpec> prior, Action<string> log)
        {
            new WorkerMessage { Parameters = settings }.Serialize();
            m_Settings = settings;
            m_Belief = belief;
            m_Prior = prior ?? new List<DeckSpec>();
            m_Log = log;
            m_Local = new Ismcts(settings, belief);
            foreach (string endpoint in endpoints)
            {
                int colon = endpoint.LastIndexOf(':');
                string host = colon < 0 ? "127.0.0.1" : endpoint.Substring(0, colon);
                int port = int.Parse(colon < 0 ? endpoint : endpoint.Substring(colon + 1));
                lock (s_Sync)
                {
                    if (!s_Pool.TryGetValue(host + ":" + port, out Worker worker))
                    {
                        worker = s_Pool[host + ":" + port] = new Worker { Host = host, Port = port };
                    }
                    m_Workers.Add(worker);
                }
            }
            if (m_Workers.Count == 0)
            {
                throw new ArgumentException("No search worker endpoints");
            }
            ReadyGraceMs = Deterministic ? 60000 : 3000;
            DoneGraceMs = Deterministic ? DeterministicWaitMs : 1500;
        }

        public SearchResult Search(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations = 0)
        {
            // A match-winning pass needs no workers.
            SearchResult passWin = Ismcts.RootPassWin(m_Settings, live, observer, request, prefix);
            if (passWin != null)
            {
                return passWin;
            }
            string reason;
            LiveGame game = LiveGame.Current;
            SearchResult result = null;
            if (game == null)
            {
                reason = "no live game record";
            }
            else
            {
                try
                {
                    result = Remote(game, live, observer, request, prefix, seed, iterations, out reason);
                }
                catch (Exception ex)
                {
                    reason = ex.GetType().Name + ": " + ex.Message;
                }
            }
            if (result != null)
            {
                return result;
            }
            if (m_Settings.WorkBudgetMs > 0)
            {
                throw new InvalidOperationException($"A work-budget search needs all its workers and cannot run in this process: {reason}");
            }
            m_Log($"Search workers unavailable ({reason}); searching in this process");
            SearchResult local = m_Local.Search(live, observer, request, prefix, seed, iterations);
            local.Source = "local: " + reason;
            return local;
        }

        private static readonly object s_Searching = new object();

        private SearchResult Remote(LiveGame game, GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations,
                                    out string reason, SearchSettings look = null, ICollection<string> rootKeys = null)
        {
            lock (s_Searching)
            {
                return RemoteLocked(game, live, observer, request, prefix, seed, iterations, out reason, look, rootKeys);
            }
        }

        // The second look (SecondLook): the same decision searched again by
        // the workers, each over the two root steps rootKeys with every
        // simulation played to the end of the match, their roots summed as in
        // the main search. iterations is the whole search's, shared by the
        // workers. A search that cannot run now (no live game record, no
        // worker reaching the position) returns null: the second look is
        // never run in this process, where it would hold the game for the
        // time of one worker's share times the workers.
        public SearchResult SearchWholeMatch(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed,
                                             int iterations, int timeBudgetMs, ICollection<string> rootKeys, out string unavailable)
        {
            LiveGame game = LiveGame.Current;
            if (game == null)
            {
                unavailable = "no live game record";
                return null;
            }
            SearchSettings look = m_Settings.WholeMatch(timeBudgetMs);
            look.Iterations = (iterations + m_Workers.Count - 1) / m_Workers.Count;
            try
            {
                SearchResult result = Remote(game, live, observer, request, prefix, seed, 0, out string reason, look, rootKeys);
                unavailable = result == null ? "second look unavailable: " + reason : null;
                return result;
            }
            catch (Exception ex)
            {
                unavailable = "second look unavailable: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // Iterations: a budget in place of the settings' (playout caps); 0
        // keeps the settings'.
        // look: the settings of a second look's search (SearchSettings.WholeMatch),
        // with rootKeys the steps it is restricted to; null for the main search.
        private SearchResult RemoteLocked(LiveGame game, GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations,
                                          out string reason, SearchSettings look, ICollection<string> rootKeys)
        {
            SearchSettings plan = look ?? m_Settings;
            bool deterministic = plan.IsDeterministic();
            bool checkpointed = plan.WorkBudgetMs > 0;
            Stopwatch clock = Stopwatch.StartNew();
            ERequestType type = request.GetRequestType();
            Player seat = live.PlayerManager.GetPlayer(observer);
            List<string> keys = prefix.Select(step => step.Key).ToList();
            string fingerprint = Fingerprint.Of(live, observer, request, keys, m_Settings.GroupPlays);
            string hidden = Fingerprint.Hidden(live, m_Settings);
            string context = game.Id + "/" + observer;
            int id = ++m_NextId;
            int[] seeds = TreeSeeds(m_Settings, seed, m_Workers.Count);
            double extend = Math.Max(1, plan.Extend);
            List<string> moves = game.MoveTexts();
            if (MoveCount != null && MoveCount < moves.Count)
            {
                moves.RemoveRange(MoveCount.Value, moves.Count - MoveCount.Value);
            }
            List<Worker> sent = new List<Worker>();
            for (int index = 0; index < m_Workers.Count; index++)
            {
                Worker worker = m_Workers[index];
                if (!Connect(worker))
                {
                    continue;
                }
                lock (s_Sync)
                {
                    worker.Id = id;
                    worker.Ready = worker.Progress = worker.Checkpoint = worker.Done = worker.Error = null;
                }
                // The worker keeps the current game's setups (one per seat).
                if (worker.Game != game.Id)
                {
                    worker.Game = game.Id;
                    worker.Contexts.Clear();
                }
                if (worker.Contexts.Add(context))
                {
                    Send(worker, new WorkerMessage
                    {
                        Type = WorkerMessage.Setup, Context = context, Seed = game.Seed, Starting = game.Starting, P1 = game.P1, P2 = game.P2,
                        Observer = observer.ToString(), Prior = m_Prior, Belief = m_Belief, Parameters = m_Settings,
                    });
                }
                Send(worker, new WorkerMessage
                {
                    Type = WorkerMessage.Search, Id = id, Context = context, Moves = moves, Request = type.ToString(), Prefix = keys,
                    Remembered = WorkerMessage.RememberedList(m_Remembered), SearchSeed = seeds[index],
                    Iterations = (int)Math.Min(int.MaxValue, (iterations > 0 ? iterations : plan.Iterations) * Math.Ceiling(extend)),
                    TimeBudgetMs = (int)(plan.TimeBudgetMs * extend), Fingerprint = fingerprint, Hidden = hidden,
                    CheckpointUnits = checkpointed ? WorkCheckpoints.Spacing(Ismcts.WorkBudget(plan, type)) : 0,
                    RootKeys = rootKeys?.ToList(),
                });
                if (!worker.Failed)
                {
                    sent.Add(worker);
                }
            }
            if (sent.Count == 0)
            {
                reason = "no worker connected";
                return null;
            }
            if (checkpointed && sent.Count != m_Workers.Count)
            {
                reason = $"{sent.Count} of {m_Workers.Count} workers connected";
                return null;
            }
            // Every worker replays the game and reports the position.
            long deadline = clock.ElapsedMilliseconds + ReadyGraceMs;
            lock (s_Sync)
            {
                while (sent.Any(worker => worker.Ready == null && worker.Error == null && !worker.Failed) && clock.ElapsedMilliseconds < deadline)
                {
                    Monitor.Wait(s_Sync, 50);
                }
            }
            List<Worker> active = new List<Worker>();
            foreach (Worker worker in sent)
            {
                if (worker.Ready != null && worker.Ready.Fingerprint == fingerprint && worker.Ready.Hidden == hidden)
                {
                    active.Add(worker);
                }
                else if (worker.Ready != null)
                {
                    if (m_Reported.Add(worker.Name + "/" + game.Id))
                    {
                        m_Log(worker.Ready.Fingerprint != fingerprint
                            ? $"Search worker {worker.Name} replayed a different position after {moves.Count} moves; not used for this decision (reported once per game)" +
                              $"\n  live:   {fingerprint}\n  worker: {worker.Ready.Fingerprint}"
                            : $"Search worker {worker.Name} replayed other hidden cards after {moves.Count} moves; not used for this oracle decision (reported once per game)" +
                              $"\n  live:   {hidden}\n  worker: {worker.Ready.Hidden}");
                    }
                }
                else if (worker.Error != null)
                {
                    m_Log($"Search worker {worker.Name}: {worker.Error.Message}");
                }
                else if (!worker.Failed)
                {
                    m_Log($"Search worker {worker.Name} did not reach the position within {ReadyGraceMs} ms");
                    Send(worker, new WorkerMessage { Type = WorkerMessage.Stop, Id = id });
                }
            }
            if (active.Count == 0)
            {
                reason = "no worker reached the position";
                return null;
            }
            if (checkpointed && active.Count != m_Workers.Count)
            {
                reason = $"{active.Count} of {m_Workers.Count} workers reached the position";
                return null;
            }
            // The stopping rule on the summed roots; its budget share counts
            // every active worker's iterations against K budgets.
            Ismcts.Budget(plan, type, out int budgetMs, out int iterationBudget);
            if (iterations > 0)
            {
                iterationBudget = type == ERequestType.Mulligan ? (int)(iterations * m_Settings.MulliganScale) : iterations;
            }
            if (checkpointed)
            {
                reason = AwaitCheckpoints(plan, type, active, id, clock, iterationBudget);
                if (reason != null)
                {
                    return null;
                }
            }
            else
            {
                AwaitStop(plan, type, active, id, clock, budgetMs, iterationBudget, extend, deterministic);
            }
            List<WorkerMessage> finals;
            lock (s_Sync)
            {
                if (deterministic && active.Any(worker => worker.Done == null))
                {
                    // A lost worker would make the decision depend on timing.
                    reason = "a worker did not finish: " + string.Join(", ", active.Where(worker => worker.Done == null)
                        .Select(worker => worker.Name + (worker.Error != null ? " (" + worker.Error.Message + ")" : worker.Failed ? " (lost)" : " (timeout)")).ToArray());
                    return null;
                }
                // A worker that reported progress but no final roots in time
                // still counts with what it reported.
                finals = active.Select(worker => worker.Done ?? worker.Progress).Where(report => report != null).ToList();
            }
            if (finals.Count == 0)
            {
                reason = "no worker reported statistics";
                return null;
            }
            foreach (Worker worker in active.Where(worker => worker.Error != null))
            {
                m_Log($"Search worker {worker.Name}: {worker.Error.Message}");
            }
            return Merge(finals, active.Count, seat, request, keys, clock, out reason);
        }

        // The clocked and the fixed-iteration search: the coordinator applies
        // the stopping rule to the roots the workers report as they go, and
        // tells them to stop. A deterministic search (no clock, no extension)
        // is never stopped early. Returns when every worker is done or the
        // wait is over; what is missing is read from the workers after.
        private void AwaitStop(SearchSettings plan, ERequestType type, List<Worker> active, int id, Stopwatch clock, int budgetMs, int iterationBudget,
                                 double extend, bool deterministic)
        {
            bool stopSent = deterministic;
            long stopAt = 0;
            long waitUntil = clock.ElapsedMilliseconds + (deterministic ? DeterministicWaitMs : (long)(Math.Max(budgetMs, 1) * extend * 2) + ReadyGraceMs + DoneGraceMs);
            while (true)
            {
                List<WorkerMessage> latest;
                lock (s_Sync)
                {
                    if (active.All(worker => worker.Done != null || worker.Error != null || worker.Failed))
                    {
                        break;
                    }
                    Monitor.Wait(s_Sync, 50);
                    latest = active.Select(worker => worker.Done ?? worker.Progress).Where(report => report != null).ToList();
                }
                long now = clock.ElapsedMilliseconds;
                if (!stopSent)
                {
                    long done = latest.Sum(report => (long)report.Iterations);
                    double used = Math.Max(done / ((double)Math.Max(1, iterationBudget) * active.Count), budgetMs > 0 ? now / (double)budgetMs : 0);
                    if (!Ismcts.Continue(plan, type, used, () => Ismcts.Rank(latest.Select(report => RootReport.ToStats(report.Roots)))))
                    {
                        foreach (Worker worker in active.Where(worker => worker.Done == null && !worker.Failed))
                        {
                            Send(worker, new WorkerMessage { Type = WorkerMessage.Stop, Id = id });
                        }
                        stopSent = true;
                        stopAt = now;
                    }
                }
                else if (!deterministic && now - stopAt > DoneGraceMs)
                {
                    break;
                }
                if (now > waitUntil)
                {
                    break;
                }
            }
        }

        // The work-budget search: the workers report at their checkpoints and
        // wait. At checkpoint n the coordinator has every worker's roots (a
        // worker that finished earlier, at its iteration cap, counts with its
        // final ones), sums them in worker order, reads the share of the
        // budget used and applies the stopping rule: the workers go on or stop.
        // Nothing in it depends on timing, so the sum at each checkpoint, and
        // the checkpoint at which the search stops, depend on the seeds and the
        // budget alone. Null when every worker finished; otherwise the reason
        // a worker is missing, after the others were told to stop.
        private string AwaitCheckpoints(SearchSettings plan, ERequestType type, List<Worker> active, int id, Stopwatch clock, int iterationBudget)
        {
            long budgetUnits = Ismcts.WorkBudget(plan, type);
            long waitUntil = clock.ElapsedMilliseconds + DeterministicWaitMs;
            for (int number = 1; ; number++)
            {
                List<WorkerMessage> latest;
                List<Worker> waiting;
                string lost = null;
                lock (s_Sync)
                {
                    while (!active.All(worker => worker.Done != null || worker.Error != null || worker.Failed || worker.Checkpoint?.Index == number) &&
                           clock.ElapsedMilliseconds < waitUntil)
                    {
                        Monitor.Wait(s_Sync, 50);
                    }
                    Worker missing = active.Find(worker => worker.Done == null && worker.Checkpoint?.Index != number);
                    if (missing != null)
                    {
                        lost = $"a worker did not finish: {missing.Name}" + (missing.Error != null ? " (" + missing.Error.Message + ")" : missing.Failed ? " (lost)" : " (timeout)");
                    }
                    waiting = active.Where(worker => worker.Done == null).ToList();
                    latest = active.Select(worker => worker.Done ?? worker.Checkpoint).ToList();
                }
                if (lost != null)
                {
                    // Every worker still searching, not only those at the checkpoint:
                    // one that reaches its next checkpoint would wait there for a
                    // verdict and miss the next search's message.
                    foreach (Worker worker in waiting.Where(worker => !worker.Failed))
                    {
                        Send(worker, new WorkerMessage { Type = WorkerMessage.Stop, Id = id });
                    }
                    return lost;
                }
                if (waiting.Count == 0)
                {
                    return null;
                }
                double used = WorkCheckpoints.Used(latest.Sum(report => report.Work?.Units ?? 0), budgetUnits, latest.Sum(report => (long)report.Iterations), iterationBudget, active.Count);
                string verdict = Ismcts.Continue(plan, type, used, () => Ismcts.Rank(latest.Select(report => RootReport.ToStats(report.Roots)))) ? WorkerMessage.Resume : WorkerMessage.Stop;
                foreach (Worker worker in waiting)
                {
                    Send(worker, new WorkerMessage { Type = verdict, Id = id });
                }
            }
        }

        // The summed roots' choice as a step of the live game; null, with the
        // reason, when no worker's simulated games reached the request or the
        // most visited step is not legal here: the caller then searches in
        // this process.
        private SearchResult Merge(List<WorkerMessage> finals, int active, Player seat, ARequestAction request, List<string> keys, Stopwatch clock,
                                   out string reason)
        {
            List<RootStat> ranked = Ismcts.Rank(finals.Select(report => RootReport.ToStats(report.Roots)));
            string tieBreak = Ismcts.BreakTie(ranked, request.GetRequestType(), m_Settings);
            List<GameAction> legal = Actions.Legal(request, seat, keys, m_Settings.GroupPlays);
            if (ranked.Count == 0)
            {
                reason = $"no simulated game reached the {request.GetRequestType()} request on any worker ({legal.Count} legal steps)";
                return null;
            }
            GameAction action = legal.Find(candidate => candidate.Key == ranked[0].Key);
            if (action == null)
            {
                reason = $"the workers' step {ranked[0].Key} is not legal in the live game";
                return null;
            }
            foreach (RootStat stat in ranked)
            {
                stat.Children = Ismcts.Rank(finals.Select(report =>
                {
                    RootReport root = report.Roots.Find(candidate => candidate.Key == stat.Key);
                    return RootReport.ToStats(root?.Children);
                }));
            }
            SearchResult result = new SearchResult
            {
                Iterations = finals.Sum(report => report.Iterations),
                Unfinished = finals.Sum(report => report.Unfinished),
                Evaluated = finals.Sum(report => report.Evaluated),
                CloneMs = finals.Sum(report => report.CloneMs),
                DeterminizeMs = finals.Sum(report => report.DeterminizeMs),
                RunMs = finals.Sum(report => report.RunMs),
                DecideMs = finals.Sum(report => report.DecideMs),
                Updates = finals.Sum(report => report.Updates),
                Work = WorkTally.Sum(finals.Select(report => report.Work)),
                ShownMismatches = finals.Sum(report => report.ShownMismatches),
                Stall = finals.Select(report => report.Stall).FirstOrDefault(stall => stall != null),
                Root = ranked,
                Source = $"workers {finals.Count(report => report.Type == WorkerMessage.Done)}/{active}/{m_Workers.Count} {clock.ElapsedMilliseconds} ms" +
                         (m_Settings.Oracle ? " oracle" : "") + (m_Settings.HandOracle ? " hand oracle" : ""),
                Action = action,
                TieBreak = tieBreak,
            };
            if (action.Move == null)
            {
                result.NextRoot = ranked[0].Children;
                RootStat next = result.NextRoot.FirstOrDefault();
                List<string> taken = new List<string>(keys) { result.Action.Key };
                result.Next = next == null ? null : Actions.Legal(request, seat, taken, m_Settings.GroupPlays).Find(candidate => candidate.Key == next.Key);
            }
            reason = null;
            return result;
        }

        // Closes every worker connection, so no reader thread is left blocked
        // in a socket read when the process quits.
        public static void CloseAll()
        {
            lock (s_Sync)
            {
                foreach (Worker worker in s_Pool.Values)
                {
                    worker.Failed = true;
                    worker.Client?.Close();
                    worker.Client = null;
                }
                Monitor.PulseAll(s_Sync);
            }
        }

        // Worker seeds as SearchSettings.Trees seeds its trees, so K workers
        // search the trees of SearchSettings.Trees = K.
        public static int[] TreeSeeds(SearchSettings settings, int seed, int count)
        {
            if (count == 1)
            {
                return new[] { seed };
            }
            Random seeds = settings.NewRandom(seed);
            return Enumerable.Range(0, count).Select(_ => seeds.Next()).ToArray();
        }

        private bool Connect(Worker worker)
        {
            if (worker.Client != null && !worker.Failed)
            {
                return true;
            }
            worker.Client?.Close();
            worker.Client = null;
            worker.Game = null;
            worker.Contexts.Clear();
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult pending = client.BeginConnect(worker.Host, worker.Port, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMs, false))
                {
                    client.Close();
                    return false;
                }
                client.EndConnect(pending);
                client.NoDelay = true;
            }
            catch (Exception)
            {
                client.Close();
                return false;
            }
            NetworkStream stream = client.GetStream();
            worker.Client = client;
            worker.Out = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            worker.Failed = false;
            StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            Thread thread = new Thread(() => Read(worker, client, reader)) { IsBackground = true, Name = "search worker " + worker.Name };
            thread.Start();
            return true;
        }

        private void Read(Worker worker, TcpClient client, StreamReader reader)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    WorkerMessage message = WorkerMessage.Parse(line);
                    lock (s_Sync)
                    {
                        if (message.Id == worker.Id && worker.Client == client)
                        {
                            switch (message.Type)
                            {
                                case WorkerMessage.Ready: worker.Ready = message; break;
                                case WorkerMessage.Progress: worker.Progress = message; break;
                                case WorkerMessage.Checkpoint: worker.Checkpoint = message; break;
                                case WorkerMessage.Done: worker.Done = message; break;
                                case WorkerMessage.Error: worker.Error = message; break;
                            }
                        }
                        Monitor.PulseAll(s_Sync);
                    }
                }
            }
            catch (Exception)
            {
                // The connection is gone; the next search reconnects.
            }
            lock (s_Sync)
            {
                if (worker.Client == client)
                {
                    worker.Failed = true;
                }
                Monitor.PulseAll(s_Sync);
            }
        }

        private void Send(Worker worker, WorkerMessage message)
        {
            try
            {
                worker.Out.WriteLine(message.Serialize());
            }
            catch (Exception ex)
            {
                m_Log($"Search worker {worker.Name} lost: {ex.Message}");
                lock (s_Sync)
                {
                    worker.Failed = true;
                    Monitor.PulseAll(s_Sync);
                }
            }
        }
    }
}
