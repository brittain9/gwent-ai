using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Learn;
using GwentAI.Sim;
using GwentCore;
using GwentGameplay;

namespace GwentAI.Search
{
    public class SearchSettings
    {
        // Simulated games per decision; with a time budget, the most.
        public int Iterations = 1000;
        // Practice: stop once this many ms have passed (0: none). A play or
        // mulligan gets the whole budget; a target or choice, which follows
        // a searched play, a third. Without a time budget, a decision
        // depends on the iterations alone, not on machine load.
        public int TimeBudgetMs;
        // A budget in work units instead of the clock (only
        // with search workers, WorkerSearch): work-ms per worker and
        // decision, 0: none. The workers stop to be merged at fixed amounts of
        // work (WorkCheckpoints) and the adaptive rule below reads the sum at
        // those points, so the decision depends on the seeds and the budget
        // alone, which a clock would break. A play or mulligan gets the whole
        // budget, a target or choice a third, as with TimeBudgetMs. Not with
        // TimeBudgetMs.
        public int WorkBudgetMs;
        // Adaptive allocation, as chess engines manage their clock, with an
        // indifference zone (ranking and selection): from half the budget
        // on, a decision stops once its best step is clearly ahead of the
        // runner-up (by Confidence standard errors) or once the runner-up
        // cannot be better by more than Material (the choice no longer
        // matters, as when every step wins or every step loses). Unresolved
        // decisions run past the budget, up to twice it; beyond that, up to
        // Extend times it (iterations and time alike), only while the
        // runner-up could still be better by BigMaterial. 1: fixed budget.
        public double Extend = 1;
        public const double Confidence = 2;
        public const double Material = 0.01;
        public const double BigMaterial = 0.04;
        // A mulligan's budget (time and iterations) in plays' budgets. Its
        // steps differ by a few hundredths in winning chances, so at a
        // play's budget the same position draws different mulligans from
        // search to search.
        public double MulliganScale = 1;
        // UCB exploration constant for rewards in [0, 1].
        public double Exploration = 0.7;
        // The constant at the opponent's nodes; negative: the same. Lower
        // models an opponent who explores less and so replies well sooner.
        public double OpponentExploration = -1;
        // 0: one tree over a fresh determinization per iteration
        // (SO-ISMCTS). N > 0: N fixed worlds, one tree each, root visits
        // summed (PIMC / ensemble determinization).
        public int Worlds;
        // Root parallelization (Chaslot, Winands and van den Herik 2008):
        // Trees independent searches, each with its own seed and the full
        // budget, their root statistics summed. They run one after another:
        // the engine cannot simulate on two threads in one process (ability
        // graphs are shared, process-wide objects that hold their working
        // state while they run), so parallel trees need one process each.
        public int Trees = 1;
        // Sees the real hidden cards and random generator. A cheating
        // reference only: it bounds what hidden information costs and
        // catches leaks (an honest search that matches it is suspect).
        public bool Oracle;
        // Sees the opponent's real hand and face-down cards and
        // nothing else hidden: the deck orders and contents are still sampled
        // from the belief, and the random generator is re-seeded for every
        // determinization, as in the fair search. A cheating reference that
        // tells hidden-hand costs from clairvoyance about draws (Oracle).
        public bool HandOracle;
        // Scores a simulated game at the start of the round after the
        // searched one (see TwoRoundHorizon), instead of playing it to the
        // end: the linear Evaluator or the learned GwentAI.Learn.ValueNet.
        // Null: play to the end.
        public IRoundEvaluator Evaluator;
        // Early playout termination: a simulation that has left the tree
        // stops at the start of its Cutoff-th turn after that and is scored
        // by Mid (0: play to the round horizon).
        public int Cutoff;
        public MidEvaluator Mid;
        // Mid cuts round-3 simulations only (with a MidEvaluator); rounds 1 and 2 keep their horizon.
        public bool MidRound3Only;
        // In place of Mid, round 3 only: a turn-start full-state value net
        // (ValueNet.TurnStart) scores the simulation at its
        // Cutoff-th turn past the tree.
        public GwentAI.Learn.ValueNet MidNet;
        // Rollout policy: chance per turn to pass while ahead.
        public double PassWhenAhead = Rollout.DefaultPassWhenAhead;
        // Rollout policy: the round-1 winner's chance to pass round 2 dry,
        // and the largest deficit it chases once the opponent passed.
        public double DryPass = Rollout.DefaultDryPass;
        public int ChaseLimit = Rollout.DefaultChaseLimit;
        // Rollout policy (see Rollout.KeepPremium); negative: off.
        public double KeepPremium = -1;
        // Rollout policy (see Rollout.ThreatAware): the chance that a seat
        // answers a threat with a removal or weather card; 0: off.
        public double ThreatAware;
        // Rollout policy (see Rollout.OpponentStyle): the opponent seat plays
        // the style fitted on recorded games; the seat's own play is
        // unchanged.
        public bool OpponentStyle;
        // With OpponentStyle, its answer cards (removal, weather) weigh by
        // the observer's threats (Rollout.OpponentAnswers).
        public bool OpponentAnswers;
        // With OpponentStyle, the self-play fitted set (Rollout.SelfPlayStyle).
        public bool SelfPlayStyle;
        // Plays are two tree steps, card then position (see GameAction).
        public bool GroupPlays = true;
        // The tree holds at most this many steps below the root (0: no
        // limit); the rollout policy plays every step after them. 1 is flat
        // Monte Carlo: UCB over the root's steps, each scored by rollouts,
        // with no tree for the replies.
        public int TreeDepth;
        // The searching seat never passes first in round 1 before it has
        // played a card (Actions.PassForbidden): without this the search
        // often concedes round 1. Its own steps in the tree
        // follow the same rule; rollouts do not.
        public bool NoEarlyPass;
        // Round 1 again, while the opponent has not passed: no pass while the
        // seat holds more than this many cards (0: off). 7 from a 10-card
        // hand contests round 1 with three cards before passing is allowed.
        public int EarlyPassHand;
        // Round 1 again, while the opponent has not passed: no pass while the
        // seat leads by 0 to this many points (negative: off). One card from
        // the opponent then takes the round at no card cost; recorded human games
        // show passes at a tie or a 1-point lead as mistakes and passes with
        // a large lead as good.
        public int EarlyPassMargin = -1;
        // No resigning pass (Actions.Resigns): while the opponent holds a
        // round and the seat trails (or is level and behind in rounds), it
        // never passes. The opponent would pass back and win, so the pass
        // loses for sure and every play is at least as good. In lost positions the
        // search values every step near 0 and could pick the pass by noise.
        public bool NoResign;
        // The pass rules NoEarlyPass, EarlyPassHand and EarlyPassMargin
        // bind the root decision only; inside the tree the pass is a legal
        // step like any other. NoResign stays tree-wide: a pass that loses for
        // sure is wrong at any node.
        public bool PassRulesAtRoot;
        // Pass to win at the root: a play request where the
        // opponent has passed, the seat strictly leads and winning the
        // round wins the match (MatchPoint.PassWins) takes the pass at once,
        // with no simulation. Passing is exact there, as in the rollouts
        // (Rollout.Play, always on).
        public bool PassToWin;
        // Economy tie-break (EconomyTieBreak): at a play decision in
        // round 1, or in round 2 after winning round 1, a root step within
        // this gap of the most visited one that spends less is played
        // instead (EconomyOrder). Negative: off.
        public double EconomyTieBreak = -1;
        // Two-round horizon: a round-1 play, target or choice is
        // searched through round 2 and scored at the start of round 3, where
        // the evaluator prices who holds the last say; without it the
        // horizon is the start of round 2. Mulligans keep their horizon.
        public bool TwoRoundHorizon;
        // Search random: every draw the search makes comes from a
        // SplitMixRandom, not the engine's autocorrelated
        // MersenneTwisterRandom (see SplitMix64). The engine's own generator
        // inside game copies stays as it is.
        public bool SearchRandom;
        // Rollout picks by Move-Average Sampling with this chance of a
        // uniform pick (see Mast); negative: uniform picks only.
        public double MastEpsilon = -1;
        // Weight of the last round's score margin in the reward of a game
        // played to its end (0: off). With every move winning or every move
        // losing, the plain reward ties them and play turns arbitrary; the
        // margin ranks them as a player would, by points (the score utility
        // of KataGo, Wu 2019). Small, so it never outweighs a real
        // difference in the chance to win.
        public double Margin;
        public const double MarginScale = 10;
        // Ties at a target or a choice go to points. The horizon
        // evaluator counts a round's points only through who wins
        // it, so where the round is decided (conceded, or the game won or
        // lost in every simulation) a boost on an enemy unit and one on
        // an own unit score alike and the pick falls to visit noise. With
        // this on, a tied root step (BreakTie) that ends the searched round
        // with a clearly better score margin, on average over its
        // simulations, is taken over the most visited one.
        public bool TieMargin;
        // Rollout policy (see Rollout.Aim).
        public bool RolloutAim;
        public int MaxUpdates = 5000;
        // Policy prior (GwentAI.Learn.PolicyNet): selection becomes PUCT,
        // Q + CPuct * P * sqrt(availability) / (1 + visits), with P the
        // net's probability of the step from the mover's view in the current
        // determinization, averaged over the first PriorSamples
        // determinizations that select at the node (an opponent node is
        // shared by determinizations in which its hand differs). Null: UCB.
        public GwentAI.Learn.PolicyNet Prior;
        public double CPuct = 1.5;
        // First-play urgency reduction an unvisited step of a PUCT
        // node is valued at the node's visited mean minus this, so low-prior
        // steps wait. 0: the node's mean itself.
        public double FirstPlayReduction;
        // Economy tie-break: a draw-two spy ranks between a pass
        // and a bronze.
        public bool SpyEconomy;
        // Final step by lower confidence bound: among root steps with
        // at least LowerBoundRule.VisitShare of the most visited step's
        // visits, the best mean minus one standard error.
        public bool LowerBoundChoice;
        public int PriorSamples = 8;
        // Softmax temperature of the prior; below 1 sharpens it toward the
        // net's favourite steps.
        public double PriorTemperature = 1;
        // Rollout policy: steps below the tree are sampled from this net's
        // softmax at RolloutTemperature instead of uniformly. Null: uniform
        // picks plus the pass rules (Rollout). RolloutPolicyFromRound: the
        // round index (0-2) from which it acts (1: rounds 2 and 3; 2: round 3
        // only).
        public GwentAI.Learn.PolicyNet RolloutPolicy;
        public int RolloutPolicyFromRound;
        public double RolloutTemperature = 1;
        // The rollout policy's forward pass through its folded first layer
        // (PolicyNet.FoldedScores): the same function, a few times
        // cheaper; a score may differ from the plain pass in the last bits.
        public bool RolloutFolded;
        // Second look (SecondLook): at a play decision
        // whose two most visited steps have mean values within this gap, a
        // second search restricted to those two steps, every simulation
        // played to the end of the match, chooses between them by its mean
        // values. Negative: off. Its budget is SecondLookIterations
        // simulations, summed over the trees or workers, within the clock
        // the main search left (TimeBudgetMs times Extend).
        public const double DefaultSecondLookGap = 0.03;
        public const int DefaultSecondLookIterations = 2000;
        public double SecondLookGap = -1;
        public int SecondLookIterations = DefaultSecondLookIterations;

        // Whether a search's result depends on its seeds alone: no clock, and
        // either a fixed budget or the adaptive rule read at work
        // checkpoints.
        public bool IsDeterministic()
        {
            return TimeBudgetMs == 0 && (Extend <= 1 || WorkBudgetMs > 0);
        }

        // The search's generator for a seed.
        public Random NewRandom(int seed)
        {
            return SearchRandom ? new SplitMixRandom(seed) : new MersenneTwisterRandom(seed);
        }

        // Every field copied; the models (evaluator, nets) are shared.
        public SearchSettings Clone()
        {
            return (SearchSettings)MemberwiseClone();
        }

        // The settings of a second look's search: every simulation played to
        // the end of the match, with no evaluator and no cutoff, the clock
        // fixed (0: none) and no extension. The rollout policy, the pass
        // rules and the rest are the agent's.
        public SearchSettings WholeMatch(int timeBudgetMs)
        {
            SearchSettings settings = Clone();
            settings.Evaluator = null;
            settings.Cutoff = 0;
            settings.Mid = null;
            settings.MidNet = null;
            settings.MidRound3Only = false;
            settings.TwoRoundHorizon = false;
            settings.TimeBudgetMs = timeBudgetMs;
            settings.WorkBudgetMs = 0;
            settings.Extend = 1;
            return settings;
        }
    }

    public class SearchResult
    {
        public GameAction Action;
        // When Action only chooses a card: the position most visited under
        // it, if the tree went that deep.
        public GameAction Next;
        public int Iterations;
        public int Unfinished;
        // Simulated games cut at the round horizon and scored by the evaluator.
        public int Evaluated;
        // Where the time went, in ms: copying, determinizing, simulating.
        public double CloneMs, DeterminizeMs, RunMs, DecideMs;
        // The state of the first simulated game that did not finish.
        public string Stall;
        // Simulated picks at the searched choice whose original card held a
        // different template than the copy shown (see Belief.Shown); each is
        // a simulation that played another card than the one chosen.
        public int ShownMismatches;
        // Own deck card states the determinizations reset (see
        // Determinization.DeckStatesLost), summed over simulations.
        public int DeckStatesLost;
        // The search's MAST table (kind -> summed result, plays); null
        // without MAST.
        public IDictionary<string, double[]> Mast;
        // With MAST: each simulated game's moves by kind (see Mast.Trace).
        public List<List<string>> MastTrace;
        public long Updates;
        // Root children by visits, best first; the chosen step first (the
        // economy or margin tie-break may move a tied step ahead).
        public List<RootStat> Root = new List<RootStat>();
        // What a tie-break changed, if it changed the choice: the economy
        // tie-break names the round and the two steps; the margin
        // tie-break (SearchSettings.TieMargin) the step taken and the
        // most visited one, with their mean margins.
        public string TieBreak;
        // When Action only chooses a card: the positions under it, by visits.
        public List<RootStat> NextRoot;
        // Policy prior calls (PUCT) and the options scored by the fallback
        // for kinds the net never sees (PolicyNet.HiddenFallbacks).
        public long PriorCalls;
        public long HiddenFallbacks;
        // What the second look compared and found (SecondLook); null when it
        // did not look.
        public SecondLookRecord SecondLook;
        // Where the search ran: null for this process; a coordinator of
        // worker processes names them (GwentAI.Practice.WorkerSearch).
        public string Source;
        // What the search cost, summed over its trees or workers.
        public WorkTally Work;
    }

    // A search that picks the next step of a seat's response to its open
    // request in a live game (Ismcts, or WorkerSearch over worker
    // processes). Remembered: opponent cards the seat has seen in the game
    // (Belief.Remember), kept by the agent.
    public interface ISearch
    {
        IDictionary<ushort, int> Remembered { get; set; }
        // Iterations: a budget in place of the settings'
        // (FairAgent.DecisionBudget); 0 keeps the settings'.
        SearchResult Search(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations = 0);
    }

    // Information Set Monte Carlo Tree Search, single observer (Cowling,
    // Powley and Whitehouse 2012), over the real engine.
    //
    // Each iteration copies the live game, determinizes the copy from the
    // searching seat's belief, and plays it to the end. Every decision in the
    // copy, by either seat, is a step (GameAction) named by its observable
    // key. While the path stays inside the tree, steps are picked by UCB over
    // the children that are legal in this determinization, counting each
    // child's availability instead of the parent's visits; the first new
    // step joins the tree, and the rollout policy plays the rest. The
    // result (win 1, draw 0.5, loss 0 for the searching seat) is backed up
    // along the path, each node scoring for the seat that chose it.
    //
    // The searching seat's own nodes are shared by every determinization,
    // so its plan cannot depend on cards it cannot see.
    public class Ismcts : IDecider, ISearch, IWholeMatchSearch
    {
        private readonly SearchSettings m_Settings;
        private readonly Belief m_Belief;
        private Random m_Random;
        private Rollout m_Rollout;
        private Mast m_Mast;
        // Opponent cards the seat saw, by instance id (Belief.Remember); the
        // agent keeps it for the game.
        public IDictionary<ushort, int> Remembered { get; set; }
        // Called every MonitorEvery iterations with the iteration count and
        // the ranked root statistics so far; false stops the search. A worker
        // process reports its roots through it and stops when the
        // coordinator, which applies the stopping rule to the summed roots
        // of every worker, says so (GwentAI.Practice.WorkerSearch).
        public Func<int, List<RootStat>, bool> Monitor;
        public const int MonitorEvery = 20;
        // A work-budget worker (SearchWorker): every CheckpointUnits of work
        // the search calls OnCheckpoint with the simulations so far, the work
        // so far and the ranked root statistics; false stops the search. The
        // coordinator sums the workers' roots at each checkpoint and applies
        // the stopping rule to the sum (WorkCheckpoints). 0: none.
        public long CheckpointUnits;
        public Func<int, WorkTally, List<RootStat>, bool> OnCheckpoint;
        // Restricts the root to these steps (the second look's search): the
        // tree holds no other first step. Null: every legal step.
        public HashSet<string> RootOnly;
        private EPlayerId m_Observer;
        private int m_RootRound;
        // The last round a simulated game plays before the evaluator scores
        // it (SearchSettings.TwoRoundHorizon).
        private int m_HorizonRound;
        private int m_IterationBudget;
        // What the search has cost since Explore began: the steps the
        // simulated games took, and the nets' counters at the start.
        private long m_Actions;
        private readonly List<ValueNet> m_ValueNets = new List<ValueNet>();
        private long[] m_ValueCalls, m_ValueTicks;
        private long m_RolloutCalls, m_RolloutTicks, m_PriorTicks;

        // Per-iteration state.
        private Node m_Node;
        private bool m_InTree;
        private bool m_RootReached;
        private ERequestType m_RootType;
        private int m_RootRequestId;
        private List<GameAction> m_Prefix;
        private int m_PrefixApplied;
        private Playout m_Playout;
        // What the observer's view of the live game fixes for every
        // determinization of this search (null with an Oracle).
        private Belief.Preparation m_Prepared;
        private SearchResult m_Result;
        private bool m_Cut;
        private bool m_MidCut;
        private int m_RolloutTurns;
        private readonly List<Node> m_Path = new List<Node>();

        private class Node
        {
            public readonly Dictionary<string, Node> Children = new Dictionary<string, Node>();
            public EPlayerId Mover;
            public int Visits;
            public int Available;
            // Sum of rewards for the seat that chose this node, and of
            // their squares.
            public double Reward;
            public double RewardSquares;
            // PUCT: the prior's sum over the determinizations that scored
            // this step at its parent, and their count; the parent counts
            // how many determinizations it has scored (PriorSamples).
            public double PriorSum;
            public int PriorCount;
            public int PriorSamples;
            // Root steps only, with SearchSettings.TieMargin (RootStat).
            public double Margin;
            public double MarginSquares;
            public int MarginCount;
        }

        public Ismcts(SearchSettings settings, Belief belief)
        {
            m_Settings = settings;
            m_Belief = belief;
        }

        // Picks the next step of the seat's response to its open request in
        // a live game. Prefix holds the steps already chosen for this
        // response and not yet sent; each copy replays them first. The live
        // game is only copied, never changed.
        // Iterations: this decision's budget in place of the settings'
        // (playout-cap randomization, FairAgent); 0 keeps the settings'.
        public SearchResult Search(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations = 0)
        {
            SearchResult passWin = RootPassWin(m_Settings, live, observer, request, prefix);
            if (passWin != null)
            {
                return passWin;
            }
            SearchResult result = SearchTrees(live, observer, request, prefix, seed, iterations);
            PhaseClock.Mark(result.Iterations);
            return result;
        }

        private SearchResult SearchTrees(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations)
        {
            int trees = Math.Max(1, m_Settings.Trees);
            if (trees == 1)
            {
                SearchResult single = Explore(live, observer, request, prefix, seed, iterations, out Node[] roots);
                return Finish(single, roots, live, observer, request, prefix);
            }
            Random seeds = m_Settings.NewRandom(seed);
            int[] treeSeeds = Enumerable.Range(0, trees).Select(_ => seeds.Next()).ToArray();
            if (m_Helpers == null || m_Helpers.Length != trees - 1)
            {
                m_Helpers = Enumerable.Range(1, trees - 1).Select(_ => new Ismcts(m_Settings, m_Belief)).ToArray();
            }
            SearchResult[] parts = new SearchResult[trees];
            Node[][] rootsByTree = new Node[trees][];
            for (int index = 0; index < trees; index++)
            {
                Ismcts search = index == 0 ? this : m_Helpers[index - 1];
                search.Remembered = Remembered;
                search.RootOnly = RootOnly;
                parts[index] = search.Explore(live, observer, request, prefix, treeSeeds[index], iterations, out rootsByTree[index]);
            }
            SearchResult merged = new SearchResult
            {
                Iterations = parts.Sum(part => part.Iterations),
                Unfinished = parts.Sum(part => part.Unfinished),
                Evaluated = parts.Sum(part => part.Evaluated),
                CloneMs = parts.Sum(part => part.CloneMs),
                DeterminizeMs = parts.Sum(part => part.DeterminizeMs),
                RunMs = parts.Sum(part => part.RunMs),
                DecideMs = parts.Sum(part => part.DecideMs),
                Updates = parts.Sum(part => part.Updates),
                ShownMismatches = parts.Sum(part => part.ShownMismatches),
                DeckStatesLost = parts.Sum(part => part.DeckStatesLost),
                Stall = parts.Select(part => part.Stall).FirstOrDefault(stall => stall != null),
                Mast = parts[0].Mast,
                MastTrace = parts[0].MastTrace,
                PriorCalls = parts.Sum(part => part.PriorCalls),
                HiddenFallbacks = parts.Sum(part => part.HiddenFallbacks),
                Work = WorkTally.Sum(parts.Select(part => part.Work)),
            };
            return Finish(merged, rootsByTree.SelectMany(roots => roots).ToArray(), live, observer, request, prefix);
        }

        public SearchResult SearchWholeMatch(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed,
                                             int iterations, int timeBudgetMs, ICollection<string> rootKeys, out string unavailable)
        {
            unavailable = null;
            int trees = Math.Max(1, m_Settings.Trees);
            SearchSettings settings = m_Settings.WholeMatch(timeBudgetMs);
            settings.Iterations = (iterations + trees - 1) / trees;
            Ismcts look = new Ismcts(settings, m_Belief) { Remembered = Remembered, RootOnly = new HashSet<string>(rootKeys) };
            return look.Search(live, observer, request, prefix, seed);
        }

        public const string PassWinSource = "pass-to-win at root";

        // With SearchSettings.PassToWin: the pass as a finished search (no
        // simulations) when the opponent has passed, the seat strictly leads
        // and winning the round wins the match; null otherwise.
        public static SearchResult RootPassWin(SearchSettings settings, GameController live, EPlayerId observer, ARequestAction request,
                                               List<GameAction> prefix)
        {
            if (!settings.PassToWin || !(request is RequestPlayCardAction) || prefix.Count > 0)
            {
                return null;
            }
            Player seat = live.PlayerManager.GetPlayer(observer);
            if (!MatchPoint.PassWins(seat.CurrentScore - seat.OpponentScore, seat.Crowns, live.RoundManager.CurrentRoundIndex, seat.OpponentPlayer.HasPassed))
            {
                return null;
            }
            GameAction pass = Actions.Legal(request, seat, new List<string>(), settings.GroupPlays).Find(action => action.Key == Actions.PassKey);
            return pass == null ? null : new SearchResult
            {
                Action = pass,
                Root = new List<RootStat> { new RootStat { Key = Actions.PassKey } },
                Source = PassWinSource,
            };
        }

        private Ismcts[] m_Helpers;
        private long m_PriorCallsAtStart, m_HiddenFallbacksAtStart;

        // One tree's search (or one per world in PIMC); the roots hold its
        // statistics.
        private SearchResult Explore(GameController live, EPlayerId observer, ARequestAction request, List<GameAction> prefix, int seed, int iterations,
                                     out Node[] roots)
        {
            m_Observer = observer;
            m_RootType = request.GetRequestType();
            m_RootRequestId = request.Id;
            m_Prefix = prefix;
            m_Random = m_Settings.NewRandom(seed);
            m_Mast = m_Settings.MastEpsilon >= 0 ? new Mast(m_Settings.MastEpsilon, m_Random) { Trace = new List<List<string>>() } : null;
            m_Rollout = new Rollout(m_Random)
            {
                PassWhenAhead = m_Settings.PassWhenAhead, DryPass = m_Settings.DryPass, ChaseLimit = m_Settings.ChaseLimit, KeepPremium = m_Settings.KeepPremium,
                ThreatAware = m_Settings.ThreatAware, Aim = m_Settings.RolloutAim, OpponentStyle = m_Settings.OpponentStyle, OpponentAnswers = m_Settings.OpponentAnswers, SelfPlayStyle = m_Settings.SelfPlayStyle, Observer = observer, Mast = m_Mast,
                Policy = m_Settings.RolloutPolicy, PolicyFromRound = m_Settings.RolloutPolicyFromRound, Temperature = m_Settings.RolloutTemperature,
                Folded = m_Settings.RolloutFolded,
                Remembered = Remembered,
            };
            if (m_Settings.RolloutFolded)
            {
                m_Settings.RolloutPolicy.ResetFold();
            }
            // The horizon is the end of the searched round (with
            // TwoRoundHorizon, of round 2 for a round-1 search). A mulligan
            // opens the next round: the round index advances only after it
            // (it is -1 at the opening mulligan).
            m_RootRound = live.RoundManager.CurrentRoundIndex + (m_RootType == ERequestType.Mulligan ? 1 : 0);
            m_HorizonRound = m_RootRound + (m_Settings.TwoRoundHorizon && m_RootRound == 0 && m_RootType != ERequestType.Mulligan ? 1 : 0);
            int worlds = m_Settings.Worlds > 0 ? m_Settings.Worlds : 1;
            roots = new Node[worlds];
            int[] worldSeeds = new int[worlds];
            for (int w = 0; w < worlds; w++)
            {
                roots[w] = new Node { Mover = observer };
                worldSeeds[w] = m_Random.Next();
            }
            SearchResult result = m_Result = new SearchResult();
            m_Prepared = m_Settings.Oracle ? null : m_Belief.Prepare(live, observer, Remembered, m_Settings.HandOracle);
            if (m_Settings.Prior != null)
            {
                m_PriorCallsAtStart = m_Settings.Prior.Calls;
                m_HiddenFallbacksAtStart = m_Settings.Prior.HiddenFallbacks;
                m_PriorTicks = m_Settings.Prior.Ticks;
            }
            BeginTally();
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Budget(m_Settings, m_RootType, out int budget, out m_IterationBudget);
            if (iterations > 0)
            {
                m_IterationBudget = m_RootType == ERequestType.Mulligan ? (int)(iterations * m_Settings.MulliganScale) : iterations;
            }
            WorkCheckpoints.Clock checkpoints = new WorkCheckpoints.Clock(CheckpointUnits);
            for (int i = 0; Continue(i, clock.ElapsedMilliseconds, budget, roots); i++)
            {
                int world = i % worlds;
                Random determinization = m_Settings.Worlds > 0 ? m_Settings.NewRandom(worldSeeds[world]) : m_Random;
                switch (Iterate(live, roots[world], determinization))
                {
                    case Outcome.Unfinished:
                        result.Unfinished++;
                        break;
                    case Outcome.Evaluated:
                        result.Evaluated++;
                        break;
                }
                result.Iterations++;
                if (CheckpointUnits > 0 && checkpoints.Reached(WorkUnits(result)))
                {
                    if (!OnCheckpoint(result.Iterations, Tally(result), Ranked(roots.Select(root => root.Children))))
                    {
                        break;
                    }
                }
            }
            result.Work = Tally(result);
            result.Mast = m_Mast?.Stats;
            result.MastTrace = m_Mast?.Trace;
            if (m_Settings.Prior != null)
            {
                result.PriorCalls = m_Settings.Prior.Calls - m_PriorCallsAtStart;
                result.HiddenFallbacks = m_Settings.Prior.HiddenFallbacks - m_HiddenFallbacksAtStart;
            }
            return result;
        }

        // The decision from the summed roots of every tree.
        private SearchResult Finish(SearchResult result, Node[] roots, GameController live, EPlayerId observer, ARequestAction request,
                                    List<GameAction> prefix)
        {
            result.Root = Ranked(roots.Select(root => root.Children));
            foreach (RootStat stat in result.Root)
            {
                stat.Children = Ranked(roots.Select(root => root.Children.TryGetValue(stat.Key, out Node chosen) ? chosen.Children : null));
            }
            result.TieBreak = BreakTie(result.Root, request.GetRequestType(), m_Settings);
            // The seat's own steps are legal in every determinization, so
            // each root child is a live step.
            Player seat = live.PlayerManager.GetPlayer(observer);
            List<string> keys = prefix.Select(step => step.Key).ToList();
            List<GameAction> legal = Actions.Legal(request, seat, keys, m_Settings.GroupPlays);
            if (result.Root.Count == 0)
            {
                throw new InvalidOperationException($"No simulated game reached the {request.GetRequestType()} request " +
                                                    $"(round {m_RootRound + 1}, {legal.Count} legal steps): {Playout.Describe(live)}");
            }
            result.Action = legal.Find(action => action.Key == result.Root[0].Key);
            if (result.Action == null)
            {
                throw new InvalidOperationException($"Searched step {result.Root[0].Key} is not legal in the live game");
            }
            if (result.Action.Move == null)
            {
                result.NextRoot = result.Root[0].Children;
                RootStat next = result.NextRoot.FirstOrDefault();
                keys.Add(result.Action.Key);
                result.Next = next == null ? null : Actions.Legal(request, seat, keys, m_Settings.GroupPlays).Find(action => action.Key == next.Key);
            }
            return result;
        }

        // The time (ms) and iteration budgets of a request: a play or
        // mulligan gets the whole budget (a mulligan's scaled by
        // MulliganScale); a target or choice, which follows a searched play,
        // a third of the time.
        public static void Budget(SearchSettings settings, ERequestType type, out int budgetMs, out int iterations)
        {
            budgetMs = settings.TimeBudgetMs;
            iterations = settings.Iterations;
            if (type == ERequestType.Mulligan)
            {
                budgetMs = (int)(budgetMs * settings.MulliganScale);
                iterations = (int)(iterations * settings.MulliganScale);
            }
            else if (type != ERequestType.PlayCard)
            {
                budgetMs /= 3;
            }
        }

        // The work budget (units, SearchSettings.WorkBudgetMs) of a request,
        // scaled as Budget scales the time.
        public static long WorkBudget(SearchSettings settings, ERequestType type)
        {
            long units = settings.WorkBudgetMs * WorkWeights.UnitsPerMs;
            if (type == ERequestType.Mulligan)
            {
                return (long)(units * settings.MulliganScale);
            }
            return type == ERequestType.PlayCard ? units : units / 3;
        }

        // Starts counting a search's work: the nets whose passes it counts
        // (the leaf evaluator's and the mid-round net, the play-out policy,
        // the prior) and their counters now.
        private void BeginTally()
        {
            m_Actions = 0;
            m_ValueNets.Clear();
            AddValueNets(m_Settings.Evaluator);
            if (m_Settings.MidNet != null && !m_ValueNets.Contains(m_Settings.MidNet))
            {
                m_ValueNets.Add(m_Settings.MidNet);
            }
            m_ValueCalls = m_ValueNets.Select(net => net.Calls).ToArray();
            m_ValueTicks = m_ValueNets.Select(net => net.Ticks).ToArray();
            m_RolloutCalls = m_Settings.RolloutPolicy?.Calls ?? 0;
            m_RolloutTicks = m_Settings.RolloutPolicy?.Ticks ?? 0;
        }

        private void AddValueNets(IRoundEvaluator evaluator)
        {
            switch (evaluator)
            {
                case ValueNet net:
                    if (!m_ValueNets.Contains(net))
                    {
                        m_ValueNets.Add(net);
                    }
                    break;
                case BlendEvaluator blend:
                    AddValueNets(blend.Net);
                    break;
                case RoundGateEvaluator gate:
                    AddValueNets(gate.Net);
                    break;
            }
        }

        // The search's work so far in units (WorkWeights).
        private long WorkUnits(SearchResult result)
        {
            long valuePasses = 0;
            for (int i = 0; i < m_ValueNets.Count; i++)
            {
                valuePasses += m_ValueNets[i].Calls - m_ValueCalls[i];
            }
            return WorkWeights.Units(result.Iterations, m_Actions, valuePasses, (m_Settings.RolloutPolicy?.Calls ?? 0) - m_RolloutCalls,
                                     m_Settings.Prior == null ? 0 : m_Settings.Prior.Calls - m_PriorCallsAtStart);
        }

        private WorkTally Tally(SearchResult result)
        {
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            WorkTally tally = new WorkTally
            {
                Simulations = result.Iterations,
                Actions = m_Actions,
                RolloutPasses = (m_Settings.RolloutPolicy?.Calls ?? 0) - m_RolloutCalls,
                PriorPasses = m_Settings.Prior == null ? 0 : m_Settings.Prior.Calls - m_PriorCallsAtStart,
                RolloutMs = ((m_Settings.RolloutPolicy?.Ticks ?? 0) - m_RolloutTicks) * tick,
                PriorMs = m_Settings.Prior == null ? 0 : (m_Settings.Prior.Ticks - m_PriorTicks) * tick,
            };
            for (int i = 0; i < m_ValueNets.Count; i++)
            {
                tally.ValuePasses += m_ValueNets[i].Calls - m_ValueCalls[i];
                tally.ValueMs += (m_ValueNets[i].Ticks - m_ValueTicks[i]) * tick;
            }
            tally.Units = WorkWeights.Units(tally.Simulations, tally.Actions, tally.ValuePasses, tally.RolloutPasses, tally.PriorPasses);
            return tally;
        }

        // Whether to run iteration i (see SearchSettings.Extend).
        private bool Continue(int i, long elapsedMs, int budgetMs, Node[] roots)
        {
            double used = Math.Max((double)i / m_IterationBudget, budgetMs > 0 ? (double)elapsedMs / budgetMs : 0);
            if (Monitor != null && i % MonitorEvery == 0 && !Monitor(i, Ranked(roots.Select(root => root.Children))))
            {
                return false;
            }
            if (m_Settings.Extend <= 1 || used < 0.5)
            {
                return used < 1;
            }
            return Continue(m_Settings, m_RootType, used, () => Ranked(roots.Select(root => root.Children)));
        }

        // The adaptive stopping rule (SearchSettings.Extend) on ranked root
        // statistics, for a search that has used the given share of its
        // budget (at least half). Ranked is called only when the rule needs
        // the roots.
        public static bool Continue(SearchSettings settings, ERequestType type, double used, Func<List<RootStat>> ranked)
        {
            if (settings.Extend <= 1 || used < 0.5)
            {
                return used < 1;
            }
            // A mulligan's scaled budget runs on to twice it at most: a
            // response can hold three mulligans.
            if (used >= (type == ERequestType.Mulligan ? Math.Min(2, settings.Extend) : settings.Extend))
            {
                return false;
            }
            List<RootStat> stats = ranked();
            if (stats.Count < 2)
            {
                return used < 1;
            }
            RootStat best = stats[0];
            RootStat runnerUp = stats[1];
            double error = SearchSettings.Confidence * Math.Sqrt(best.MeanVariance + runnerUp.MeanVariance);
            if (best.Mean - runnerUp.Mean > error)
            {
                return false;
            }
            // How much better the runner-up could plausibly be.
            double upside = runnerUp.Mean - best.Mean + error;
            return upside > (used < 2 ? SearchSettings.Material : SearchSettings.BigMaterial);
        }

        // SearchSettings.TieMargin at a target or choice (RootStat.BreakTie):
        // moves the step taken to the front of ranked and describes the
        // change; null when the most visited step stands.
        public static string BreakTie(List<RootStat> ranked, ERequestType type, SearchSettings settings)
        {
            if (!settings.TieMargin || !(type == ERequestType.TargetCards || type == ERequestType.TargetLocations || type == ERequestType.Choice))
            {
                return null;
            }
            return RootStat.BreakTie(ranked, SearchSettings.Material, SearchSettings.Confidence);
        }

        // Children summed over trees (one per world in PIMC), most visited
        // first.
        private static List<RootStat> Ranked(IEnumerable<Dictionary<string, Node>> children)
        {
            return Rank(children.Where(siblings => siblings != null).Select(siblings => siblings.Select(child => new RootStat
            {
                Key = child.Key, Visits = child.Value.Visits, Reward = child.Value.Reward, RewardSquares = child.Value.RewardSquares,
                Margin = child.Value.Margin, MarginSquares = child.Value.MarginSquares, MarginCount = child.Value.MarginCount,
            })));
        }

        // Root statistics summed over searches (trees, worlds or worker
        // processes), most visited first. Ties break on the mean reward,
        // then on the key, so the choice depends on the seeds alone.
        public static List<RootStat> Rank(IEnumerable<IEnumerable<RootStat>> searches)
        {
            Dictionary<string, RootStat> totals = new Dictionary<string, RootStat>();
            foreach (IEnumerable<RootStat> roots in searches)
            {
                foreach (RootStat root in roots)
                {
                    if (!totals.TryGetValue(root.Key, out RootStat total))
                    {
                        total = totals[root.Key] = new RootStat { Key = root.Key };
                    }
                    total.Visits += root.Visits;
                    total.Reward += root.Reward;
                    total.RewardSquares += root.RewardSquares;
                    total.Margin += root.Margin;
                    total.MarginSquares += root.MarginSquares;
                    total.MarginCount += root.MarginCount;
                }
            }
            return totals.Values.OrderByDescending(stat => stat.Visits).ThenByDescending(stat => stat.Mean)
                .ThenBy(stat => stat.Key, StringComparer.Ordinal).ToList();
        }

        private enum Outcome
        {
            Ended,
            Evaluated,
            Unfinished,
        }

        private Outcome Iterate(GameController live, Node root, Random determinization)
        {
            m_Node = root;
            m_InTree = true;
            m_RootReached = false;
            m_PrefixApplied = 0;
            m_Cut = false;
            m_MidCut = false;
            m_RolloutTurns = 0;
            m_Path.Clear();
            m_Mast?.Begin();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Playout playout = m_Playout = Playout.Clone(live, (players, id) => new SimPlayer(players, id, this));
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!m_Settings.Oracle)
            {
                long determinizeStart = PhaseClock.Start();
                m_Result.DeckStatesLost += m_Belief.Determinize(playout.Game, m_Observer, determinization, m_Prepared).DeckStatesLost;
                PhaseClock.Stop(Phase.Determinize, determinizeStart);
            }
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ended = playout.Run(m_Settings.MaxUpdates);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            m_Result.CloneMs += (t1 - t0) * tick;
            m_Result.DeterminizeMs += (t2 - t1) * tick;
            m_Result.RunMs += (t3 - t2) * tick;
            m_Result.Updates += playout.Updates;
            SearchPatches.OverrideInitialDecks(null, null);
            double reward;
            Outcome outcome;
            if (ended)
            {
                reward = playout.Winner == m_Observer ? 1 : playout.Winner == EPlayerId.AllPlayers ? 0.5 : 0;
                if (m_Settings.Margin > 0)
                {
                    double margin = playout.LastRoundMargin(m_Observer) / SearchSettings.MarginScale;
                    reward = (1 - m_Settings.Margin) * reward + m_Settings.Margin / (1 + Math.Exp(-margin));
                }
                outcome = Outcome.Ended;
            }
            else if (m_MidCut)
            {
                reward = m_Settings.MidNet != null ? m_Settings.MidNet.TurnWinProbability(playout.Game, m_Observer)
                    : m_Settings.Mid.WinProbability(playout.Game, m_Observer);
                outcome = Outcome.Evaluated;
            }
            else if (m_Cut)
            {
                long evaluateStart = PhaseClock.Start();
                reward = m_Settings.Evaluator.WinProbability(playout.Game, m_Observer, Remembered);
                PhaseClock.Stop(Phase.Evaluate, evaluateStart);
                outcome = Outcome.Evaluated;
            }
            else
            {
                reward = 0.5;
                outcome = Outcome.Unfinished;
                if (m_Result.Stall == null)
                {
                    m_Result.Stall = Playout.Describe(playout.Game);
                }
            }
            m_Mast?.End(m_Observer, reward);
            if (m_Settings.TieMargin && m_Path.Count > 0 && (ended || m_Cut) && playout.RoundMargin(m_Observer, m_RootRound) is int roundMargin)
            {
                Node step = m_Path[0];
                step.Margin += roundMargin;
                step.MarginSquares += (double)roundMargin * roundMargin;
                step.MarginCount++;
            }
            root.Visits++;
            foreach (Node node in m_Path)
            {
                node.Visits++;
                double own = node.Mover == m_Observer ? reward : 1 - reward;
                node.Reward += own;
                node.RewardSquares += own * own;
            }
            return outcome;
        }

        public GameAction Decide(Player player, ARequestAction request, ICollection<string> taken)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            GameAction step = DecideStep(player, request, taken);
            PhaseClock.Stop(Phase.Decide, start);
            if (step != null)
            {
                m_Actions++;
            }
            if (m_Mast != null && step?.Move != null)
            {
                m_Mast.Record(player.Id, request, step.Move);
            }
            if (step?.Move != null && step.Move.Kind == EMoveKind.Choose && player.Id == m_Observer && request.Id == m_RootRequestId &&
                request is RequestCardChoicesAction choices)
            {
                Card original = choices.GetOriginalFromChoiceId(step.Move.CardId);
                Card shown = choices.GetChoiceFromId(step.Move.CardId);
                if (original != null && shown != null && original.Definition.TemplateId != shown.Definition.TemplateId)
                {
                    m_Result.ShownMismatches++;
                }
            }
            m_Result.DecideMs += (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return step;
        }

        private GameAction TimedRollout(Phase phase, Player player, ARequestAction request, ICollection<string> taken, List<GameAction> legal)
        {
            long start = PhaseClock.Start();
            GameAction step = m_Rollout.Decide(player, request, taken, legal);
            PhaseClock.Stop(phase, start);
            return step;
        }

        private GameAction DecideStep(Player player, ARequestAction request, ICollection<string> taken)
        {
            bool atRoot = false;
            // The round horizon: the first request of a round after the
            // horizon round. The rest of this update still runs, so the
            // evaluator sees the same settled state as the records it
            // is fitted to.
            if (m_Cut || (m_Settings.Evaluator != null && player.CurrentGameController.RoundManager.CurrentRoundIndex > m_HorizonRound))
            {
                m_Cut = true;
                m_Playout.Stop();
                return null;
            }
            if (!m_RootReached)
            {
                // The copy re-delivers every open request; the searched one
                // is the seat's. Another (the opponent's simultaneous
                // mulligan) is answered by the rollout policy.
                if (player.Id != m_Observer || request.GetRequestType() != m_RootType)
                {
                    return TimedRollout(Phase.RolloutOther, player, request, taken, null);
                }
                if (m_PrefixApplied < m_Prefix.Count)
                {
                    return m_Prefix[m_PrefixApplied++];
                }
                m_RootReached = true;
                atRoot = true;
            }
            if (m_InTree && m_Settings.TreeDepth > 0 && m_Path.Count >= m_Settings.TreeDepth)
            {
                m_InTree = false;
            }
            if (!m_InTree && m_Settings.Cutoff > 0 && request is RequestPlayCardAction && taken.Count == 0 &&
                (m_Settings.Mid != null && !m_Settings.MidRound3Only ||
                 (m_Settings.Mid != null || m_Settings.MidNet != null) && player.CurrentGameController.RoundManager.CurrentRoundIndex == 2) &&
                ++m_RolloutTurns > m_Settings.Cutoff)
            {
                m_MidCut = true;
                m_Playout.Stop();
                return null;
            }
            // A rollout's play needs the steps only to know that it chooses;
            // Rollout.Play picks without them.
            if (!m_InTree && taken.Count == 0 && m_Settings.GroupPlays && request is RequestPlayCardAction play)
            {
                long choiceStart = PhaseClock.Start();
                bool choice = Actions.HasChoice(play, player, m_Observer);
                PhaseClock.Stop(Phase.Legal, choiceStart);
                if (choice)
                {
                    return TimedRollout(Phase.RolloutPlay, player, request, taken, null);
                }
            }
            long legalStart = PhaseClock.Start();
            List<GameAction> legal = Actions.Legal(request, player, taken, m_Settings.GroupPlays, m_Observer);
            if (player.Id == m_Observer)
            {
                Actions.RemoveEarlyPass(legal, player, m_Settings, atRoot);
            }
            if (atRoot && RootOnly != null)
            {
                legal.RemoveAll(action => !RootOnly.Contains(action.Key));
            }
            PhaseClock.Stop(Phase.Legal, legalStart);
            if (legal.Count <= 1)
            {
                return legal.FirstOrDefault();
            }
            if (!m_InTree)
            {
                return TimedRollout(Phase.RolloutOther, player, request, taken, legal);
            }
            Node node = m_Node;
            if (m_Settings.Prior != null)
            {
                return SelectPuct(node, player, request, taken, legal);
            }
            List<GameAction> untried = new List<GameAction>();
            GameAction best = null;
            Node bestChild = null;
            double bestScore = double.NegativeInfinity;
            double exploration = player.Id != m_Observer && m_Settings.OpponentExploration >= 0 ? m_Settings.OpponentExploration : m_Settings.Exploration;
            foreach (GameAction action in legal)
            {
                if (!node.Children.TryGetValue(action.Key, out Node child))
                {
                    untried.Add(action);
                    continue;
                }
                child.Available++;
                double score = child.Reward / child.Visits + exploration * Math.Sqrt(Math.Log(child.Available) / child.Visits);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                    bestChild = child;
                }
            }
            if (untried.Count > 0)
            {
                best = untried[m_Random.Next(untried.Count)];
                bestChild = new Node { Mover = player.Id, Available = 1 };
                node.Children.Add(best.Key, bestChild);
                m_InTree = false;
            }
            m_Path.Add(bestChild);
            m_Node = bestChild;
            return best;
        }

        // PUCT selection (Silver et al. 2017) over the steps legal in this
        // determinization, with ISMCTS availability counts in place of the
        // parent's visits. The prior comes from the policy net, scored from
        // the mover's own view of the determinized game: the searching seat
        // sees its real hand, the opponent its sampled one. A node's children
        // keep the running mean of their prior over the first PriorSamples
        // determinizations that select here; a child first seen later takes
        // a uniform share. Every legal step has a child, so an unvisited
        // step competes with its prior and the parent's value (first play
        // urgency: the visited children's mean), and the step chosen with no
        // visits is the expansion.
        private GameAction SelectPuct(Node node, Player player, ARequestAction request, ICollection<string> taken, List<GameAction> legal)
        {
            PolicyNet prior = m_Settings.Prior;
            bool sample = node.PriorSamples < m_Settings.PriorSamples;
            double[] probabilities = null;
            if (sample)
            {
                prior.Begin();
                foreach (GameAction action in legal)
                {
                    prior.Add(action, player.Id);
                }
                double[] scores = prior.Evaluate(player.CurrentGameController, player.Id, player.Id == m_Observer ? Remembered : null, request, taken.Count);
                probabilities = prior.Probabilities(scores, m_Settings.PriorTemperature);
                node.PriorSamples++;
            }
            double rewardSum = 0;
            int visitSum = 0;
            for (int i = 0; i < legal.Count; i++)
            {
                GameAction action = legal[i];
                if (!node.Children.TryGetValue(action.Key, out Node child))
                {
                    child = new Node { Mover = player.Id };
                    node.Children.Add(action.Key, child);
                }
                child.Available++;
                if (sample)
                {
                    child.PriorSum += probabilities[i];
                    child.PriorCount++;
                }
                rewardSum += child.Reward;
                visitSum += child.Visits;
            }
            double parentValue = visitSum > 0 ? rewardSum / visitSum : 0.5;
            double uniform = 1.0 / legal.Count;
            GameAction best = null;
            Node bestChild = null;
            double bestScore = double.NegativeInfinity;
            int ties = 0;
            foreach (GameAction action in legal)
            {
                Node child = node.Children[action.Key];
                double q = child.Visits > 0 ? child.Reward / child.Visits : parentValue - m_Settings.FirstPlayReduction;
                double p = child.PriorCount > 0 ? child.PriorSum / child.PriorCount : uniform;
                double score = q + m_Settings.CPuct * p * Math.Sqrt(child.Available) / (1 + child.Visits);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = action;
                    bestChild = child;
                    ties = 1;
                }
                else if (score == bestScore && m_Random.Next(++ties) == 0)
                {
                    best = action;
                    bestChild = child;
                }
            }
            if (bestChild.Visits == 0)
            {
                m_InTree = false;
            }
            m_Path.Add(bestChild);
            m_Node = bestChild;
            return best;
        }
    }
}
