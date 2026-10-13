using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GwentAI.Sim;
using GwentCore;
using GwentGameplay;

namespace GwentAI.Search
{
    // The fair search's answers to a seat's requests. It works on any copy
    // of the game and never changes it, so a seat can answer from the live
    // game between updates (FairPlayer) or from a snapshot on
    // another thread (Practice).
    //
    // Each step of a response (a play, one target, one mulligan) is its own
    // search. Steps already chosen for the response are replayed in every
    // simulated game first, and the whole response is returned at once,
    // since the engine acts on a request only after it is complete.
    public class FairAgent
    {
        private readonly ISearch m_Search;
        private readonly Belief m_Belief;
        private readonly SearchSettings m_Settings;
        private readonly bool m_CheckFairness;
        private int m_Decisions;
        private SearchResult m_LastRecheck;
        // Opponent cards this seat has seen during the game (Belief.Remember).
        private readonly Dictionary<ushort, int> m_Seen = new Dictionary<ushort, int>();

        public readonly List<DecisionStat> Stats = new List<DecisionStat>();
        public int FairnessChecks { get; private set; }
        public int FairnessViolations { get; private set; }
        // Called after each searched decision, with the live game still at
        // the decision; null: none.
        public Action<SearchedDecision> OnDecision;

        // Search: in place of this process's own Ismcts, a search elsewhere
        // (GwentAI.Practice.WorkerSearch).
        public FairAgent(SearchSettings settings, Belief belief, bool checkFairness, ISearch search = null)
        {
            if (!SearchPatches.Installed)
            {
                throw new InvalidOperationException("The fair search needs SearchPatches installed");
            }
            m_Belief = belief;
            m_Settings = settings;
            m_Search = search ?? new Ismcts(settings, belief);
            IWholeMatchSearch look = m_Search as IWholeMatchSearch;
            if (settings.LowerBoundChoice)
            {
                m_Search = new LowerBoundSearch(m_Search, settings);
            }
            if (settings.EconomyTieBreak >= 0)
            {
                m_Search = new EconomyTieBreak(m_Search, settings);
            }
            if (settings.SecondLookGap >= 0)
            {
                if (look == null)
                {
                    throw new ArgumentException("The second look needs a search that can run again from the same position (ismcts, not a policy)");
                }
                m_Search = new SecondLook(m_Search, look, settings);
            }
            m_Search.Remembered = m_Seen;
            m_CheckFairness = checkFairness && !settings.Oracle && !settings.HandOracle;
        }

        // The moves answering the seat's request in the game; seed gives the
        // search seed for the agent's n-th searched decision.
        public List<Move> Respond(GameController game, Player seat, ARequestAction request, Func<int, int> seed)
        {
            List<GameAction> steps = new List<GameAction>();
            while (true)
            {
                List<GameAction> legal = Actions.Legal(request, seat, steps.Select(step => step.Key).ToList(), m_Settings.GroupPlays);
                GameAction forced = steps.Count == 0 ? Forced(legal) : null;
                if (forced == null)
                {
                    Actions.RemoveEarlyPass(legal, seat, m_Settings);
                }
                if (forced != null)
                {
                    steps.Add(forced);
                }
                else if (legal.Count == 0)
                {
                    break;
                }
                else if (legal.Count == 1 || SkipSearch)
                {
                    steps.Add(legal[0]);
                }
                else
                {
                    SearchResult result = Decide(game, seat.Id, request, steps, legal, seed);
                    steps.Add(result.Action);
                    if (result.Next != null)
                    {
                        steps.Add(result.Next);
                    }
                }
                Move last = steps[steps.Count - 1].Move;
                if (last != null && (last.EndsResponse || !SimPlayer.IsMultiStep(request)))
                {
                    break;
                }
            }
            return steps.Where(step => step.Move != null).Select(step => step.Move).ToList();
        }

        // The first step of a response, taken instead of a search and before
        // the early-pass rule; null: none.
        protected virtual GameAction Forced(List<GameAction> legal)
        {
            return null;
        }

        // Whether a step with several legal options takes the first one
        // instead of a search.
        protected virtual bool SkipSearch => false;

        // A searched decision's budget from its seed: simulations in place of
        // the settings' (0 keeps them), and whether the decision ran at the
        // full budget (DecisionStat.Full; null: not recorded, full).
        protected virtual int DecisionBudget(int seed, out bool? full)
        {
            full = null;
            return 0;
        }

        private SearchResult Decide(GameController game, EPlayerId seat, ARequestAction request, List<GameAction> prefix, List<GameAction> legal,
                                    Func<int, int> seeds)
        {
            int index = m_Decisions++;
            int seed = seeds(index);
            int iterations = DecisionBudget(seed, out bool? budget);
            bool full = budget != false;
            Belief.Remember(game, seat, m_Seen);
            Stopwatch clock = Stopwatch.StartNew();
            SearchResult result = m_Search.Search(game, seat, request, prefix, seed, iterations);
            Player me = game.PlayerManager.GetPlayer(seat);
            Player them = me.OpponentPlayer;
            DecisionStat stat = new DecisionStat
            {
                Position = $"score {me.CurrentScore}-{them.CurrentScore} hand {me.GetCardsInLocation(ELocation.Hand).Count}-" +
                           $"{them.GetCardsInLocation(ELocation.Hand).Count} crowns {me.Crowns}-{them.Crowns}" + (them.HasPassed ? " opponent passed" : ""),
                Index = index,
                Request = request.GetRequestType().ToString(),
                Round = game.RoundManager.CurrentRoundIndex + 1,
                Legal = legal.Count,
                Key = Describe(result),
                Full = budget,
                RootVisits = full ? result.Root.Select(root => new object[] { root.Key, root.Visits, root.Mean }).ToList() : null,
                SecondLook = result.SecondLook,
                PriorCalls = m_Settings.Prior != null ? result.PriorCalls : (long?)null,
                HiddenFallbacks = m_Settings.Prior != null ? result.HiddenFallbacks : (long?)null,
                Iterations = result.Iterations,
                Unfinished = result.Unfinished,
                Evaluated = result.Evaluated,
                Stall = result.Stall,
                ShownMismatches = result.ShownMismatches,
                DeckStatesLost = result.DeckStatesLost,
                Ms = clock.Elapsed.TotalMilliseconds,
                Profile = $"clone {result.CloneMs:F0} det {result.DeterminizeMs:F0} run {result.RunMs:F0} (decide {result.DecideMs:F0}) updates {result.Updates}",
                Top = result.Root.Take(3).Select(Describe).ToList(),
                Visits = result.Root.Sum(root => root.Visits),
                Value = RootValue(result.Root),
                BestVisits = result.Root[0].Visits,
                BestMean = result.Root[0].Mean,
                Source = result.Source,
                TieBreak = result.TieBreak,
                Work = result.Work,
            };
            if (m_Settings.ClassBPolicy != null && result.Source != Ismcts.PassWinSource && Ismcts.ClassBRoot(game, seat, request, prefix))
            {
                // The decision searched on the class-B full-horizon settings
                // (SearchSettings.ClassBPolicy), not a pass taken without a search;
                // every other record is unchanged.
                stat.Extra = stat.Extra ?? new Dictionary<string, object>();
                stat.Extra["ClassB"] = true;
            }
            if (m_CheckFairness)
            {
                stat.Fair = CheckFairness(game, seat, request, prefix, seed, iterations, Describe(result), out List<string> unfairTop);
                stat.UnfairTop = unfairTop;
                if (unfairTop != null && result.Mast != null && m_LastRecheck?.Mast != null)
                {
                    // The kinds whose play counts differ most between the two
                    // searches: where hidden information changed the rollouts.
                    stat.UnfairMast = result.Mast.Keys.Union(m_LastRecheck.Mast.Keys)
                        .Select(kind => new
                        {
                            Kind = kind,
                            A = result.Mast.TryGetValue(kind, out double[] a) ? a[1] : 0,
                            B = m_LastRecheck.Mast.TryGetValue(kind, out double[] b) ? b[1] : 0,
                        })
                        .Where(row => row.A != row.B).OrderByDescending(row => Math.Abs(row.A - row.B)).Take(12)
                        .Select(row => $"{row.Kind} {row.A}/{row.B}").ToList();
                    // The first simulated game whose moves differ, and the
                    // moves around the first difference in both searches.
                    for (int g = 0; g < Math.Min(result.MastTrace.Count, m_LastRecheck.MastTrace.Count); g++)
                    {
                        List<string> a = result.MastTrace[g], b = m_LastRecheck.MastTrace[g];
                        int m = 0;
                        while (m < a.Count && m < b.Count && a[m] == b[m])
                        {
                            m++;
                        }
                        if (m < a.Count || m < b.Count)
                        {
                            stat.UnfairMast.Add($"game {g} move {m}: " + string.Join(" | ", a.Skip(Math.Max(0, m - 3)).Take(5).ToArray()) + "  VS  " +
                                                string.Join(" | ", b.Skip(Math.Max(0, m - 3)).Take(5).ToArray()));
                            break;
                        }
                    }
                }
            }
            Stats.Add(stat);
            if (OnDecision != null)
            {
                OnDecision(new SearchedDecision
                {
                    Game = game, Seat = seat, Request = request, Prefix = prefix, Legal = legal, Result = result, Stat = stat, Seen = m_Seen,
                    Iterations = iterations > 0 ? iterations : m_Settings.Iterations, Seed = seed, Full = full,
                });
            }
            return result;
        }

        // The fairness test: the same search, with the same seed, from a
        // copy whose hidden cards (the opponent's hand, deck and face-down
        // cards, our deck order, the random generator) were redrawn, must
        // choose the same step. A search that reads anything the seat cannot
        // see would, in general, choose differently.
        // UnfairTop: on a violation, the re-decision's top steps, to set
        // beside the decision's own.
        private bool CheckFairness(GameController game, EPlayerId seat, ARequestAction request, List<GameAction> prefix, int seed, int iterations,
                                   string key, out List<string> unfairTop)
        {
            unfairTop = null;
            Playout copy = Playout.Clone(game, (players, id) => new Player(players, id));
            Random random = m_Settings.NewRandom(seed ^ 0x2545F491);
            m_Belief.Determinize(copy.Game, seat, random);
            ARequestAction mirror = copy.Game.RequestManager.Requests.First(open => open.Id == request.Id && open.TargetPlayer == seat);
            // Copies share the game settings, which hold the opponent's real
            // deck list. For the re-decision it holds a scrambled list with
            // the same public facts (leader, faction, size), so a search that
            // read the real list would decide differently. Restored after.
            GwentGameplay.Settings.PlayerSettings opponent = game.GameInstance.Settings.GetPlayerSettings(game.PlayerManager.GetOpponentPlayerId(seat));
            BattleDeck real = opponent.Deck;
            opponent.Deck = Belief.Scramble(real, random);
            SearchResult again;
            try
            {
                again = m_Search.Search(copy.Game, seat, mirror, prefix, seed, iterations);
            }
            finally
            {
                opponent.Deck = real;
            }
            FairnessChecks++;
            m_LastRecheck = again;
            bool fair = Describe(again) == key;
            if (!fair)
            {
                FairnessViolations++;
                unfairTop = again.Root.Take(5).Select(Describe).ToList();
            }
            return fair;
        }

        private static string Describe(SearchResult result)
        {
            return result.Action.Key + (result.Next == null ? "" : " > " + result.Next.Key);
        }

        // The search's own estimate of the seat's chance to win from the
        // searched position: the visit-weighted mean reward over every root
        // step. It is the search's value of the position.
        private static double RootValue(List<RootStat> root)
        {
            double reward = 0;
            int visits = 0;
            foreach (RootStat stat in root)
            {
                reward += stat.Reward;
                visits += stat.Visits;
            }
            return visits == 0 ? 0.5 : reward / visits;
        }

        // Opponent cards the seat saw earlier in the game (Belief.Remember),
        // for an agent that takes a seat over mid-game (a replayed position):
        // it then knows what the seat knew.
        public void Remember(IDictionary<ushort, int> seen)
        {
            foreach (KeyValuePair<ushort, int> card in seen)
            {
                m_Seen[card.Key] = card.Value;
            }
        }

        // "key visits mean", the mean to three decimals as Mono prints it
        // with F3: rounded to 15 significant digits, then half away from
        // zero. .NET's F3 rounds the exact binary value instead (0.3125 ->
        // "0.312", Mono "0.313"), so the GwentHost.Core records would differ
        // in text only. The decimal conversion keeps 15 digits on every
        // runtime.
        private static string Describe(RootStat root)
        {
            return $"{root.Key} {root.Visits} {Math.Round((decimal)root.Mean, 3, MidpointRounding.AwayFromZero):F3}";
        }

        // Decorrelated seeds per match, seat and decision (SplitMix32-style).
        public static int Seed(int matchSeed, EPlayerId seat, int decision)
        {
            unchecked
            {
                uint x = (uint)matchSeed * 0x9E3779B1u ^ (uint)seat * 0x85EBCA77u ^ (uint)decision * 0xC2B2AE3Du;
                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;
                return (int)(x & 0x7FFFFFFF);
            }
        }
    }

    // A searched decision as the live game stands, for recording.
    public class SearchedDecision
    {
        public GameController Game;
        public EPlayerId Seat;
        public ARequestAction Request;
        // Steps of this response chosen before this one, and the steps the
        // search chose among.
        public List<GameAction> Prefix;
        public List<GameAction> Legal;
        public SearchResult Result;
        public DecisionStat Stat;
        // What the seat remembers of the opponent's cards (Belief.Remember).
        public IDictionary<ushort, int> Seen;
        public int Iterations;
        // The decision's search seed, and whether it ran at the full budget.
        public int Seed;
        public bool Full;
    }

    public class DecisionStat
    {
        public int Index;
        public string Request;
        public int Round;
        public int Legal;
        public string Key;
        public int Iterations;
        public int Unfinished;
        public int Evaluated;
        // The first unfinished simulated game's state, if any.
        public string Stall;
        public double Ms;
        // The most visited root steps: "key visits mean".
        public List<string> Top;
        // Visits over every root step, the visit-weighted mean reward over
        // them (the search's value of the position for the seat), and the
        // chosen step's visits and mean reward.
        public int Visits;
        public double Value;
        public int BestVisits;
        public double BestMean;
        // What the seat saw: scores, hand sizes and crowns (seat first).
        public string Position;
        public string Profile;
        // Null when not checked.
        public bool? Fair;
        // On a fairness violation: the re-decision's most visited steps.
        public List<string> UnfairTop;
        // On a fairness violation with MAST: kinds whose play counts differ
        // between the decision and its re-decision ("kind a/b").
        public List<string> UnfairMast;
        // See SearchResult.ShownMismatches.
        public int ShownMismatches;
        // See SearchResult.DeckStatesLost.
        public int DeckStatesLost;
        // Whether this decision ran at the full budget; null when not
        // recorded (FairAgent.DecisionBudget).
        public bool? Full;
        // Every root step with its visits and mean reward, [key, visits,
        // mean], at full-budget decisions.
        public List<object[]> RootVisits;
        // With SearchSettings.SecondLookGap, at a decision whose two most
        // visited steps were within the gap: the steps compared, their
        // values in both searches, the second search's simulations, and
        // whether it changed the choice (SecondLookRecord); null otherwise.
        public SecondLookRecord SecondLook;
        // With a policy prior: prior evaluations in this search, and options
        // scored by the hidden-kind fallback (PolicyNet.HiddenFallbacks).
        public long? PriorCalls;
        public long? HiddenFallbacks;
        // See SearchResult.Source; null for a search in this process.
        public string Source;
        // See SearchResult.TieBreak; null when the tie-break kept the choice.
        public string TieBreak;
        // What the search cost in operations and work units (WorkTally),
        // summed over its trees or workers; null for a search that did none.
        public WorkTally Work;
        // Further fields an OnDecision handler adds, written beside the
        // others in the record; null: none.
        [Newtonsoft.Json.JsonExtensionData]
        public IDictionary<string, object> Extra;
    }
}
