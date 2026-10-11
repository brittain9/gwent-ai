using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GwentCore;
using GwentGameplay;
using GwentGameplay.Settings;
using RedTools;

namespace GwentHost
{
    // Checks of the engine patch pack (EnginePack.cs, EventPatches.cs):
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --self-test engine-pack
    //
    // Each check runs the same seeded scenarios on the engine without the
    // pack (the check sets GWENT_ENGINE_PATCHES=0 for itself), then
    // installs the whole pack (and level 1) and runs them again; the answers
    // must be equal. The first run is the client's own code, so the check
    // compares the patches with the original, not with a transcription.
    //
    //   events     random sequences of listener operations on RedTools.Event
    //              types, with listeners that call back into the event,
    //              change listeners during a dispatch and throw
    //   filter     FilterDefinitions with random argument tuples, each run
    //              twice after install (a miss, then a hit)
    //   authority  CheckAuthority of every plain action type, on controllers
    //              of every game mode
    //   rng        MersenneTwisterRandom: seeding, draws across refills, copies
    //   presize    AbilityVariables clones
    internal static class EnginePackSelfTest
    {
        public static int Run(string client)
        {
            // The first run needs the engine without the pack, which is the
            // default host's engine.
            Environment.SetEnvironmentVariable(EnginePatches.LevelVariable, "0");
            GameSharedData shared = Host.Engine(client, out HarmonyLib.Harmony harmony, out RedLogger.IRedLogger logger);
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            List<KeyValuePair<string, List<string>>> before = Scenarios(shared, logger);
            Console.WriteLine($"engine-pack: scenarios on the engine as it is: {clock.ElapsedMilliseconds} ms");
            clock.Reset();
            clock.Start();
            EnginePatches.Install(harmony, 2, Pack.All);
            Console.WriteLine($"engine-pack: pack installed: {clock.ElapsedMilliseconds} ms");
            clock.Reset();
            clock.Start();
            List<KeyValuePair<string, List<string>>> after = Scenarios(shared, logger);
            Console.WriteLine($"engine-pack: scenarios with the pack: {clock.ElapsedMilliseconds} ms");
            int failures = 0;
            for (int g = 0; g < before.Count; g++)
            {
                List<string> expected = before[g].Value, actual = after[g].Value;
                int first = -1;
                int differing = 0;
                for (int i = 0; i < expected.Count; i++)
                {
                    if (expected[i] != actual[i])
                    {
                        differing++;
                        first = first < 0 ? i : first;
                    }
                }
                Console.WriteLine($"{before[g].Key}: {expected.Count} scenarios, {(differing == 0 ? "identical" : differing + " differ")}");
                if (differing > 0)
                {
                    failures++;
                    Console.WriteLine($"  first at {first}:\n    before {Clip(expected[first])}\n    after  {Clip(actual[first])}");
                }
            }
            Console.WriteLine("engine-pack: " + PatchedTypes());
            if (!Live())
            {
                failures++;
            }
            Console.WriteLine($"engine-pack: filter entries {FilterMemo.EntryCount}, {(failures == 0 ? "identical" : failures + " groups differ")}");
            if (FilterMemo.EntryCount == 0)
            {
                Console.WriteLine("engine-pack: the filter memo recorded nothing (patch not live)");
                return 1;
            }
            return failures == 0 ? 0 : 1;
        }

        private static string Clip(string text)
        {
            return text.Length > 1500 ? text.Substring(0, 1500) + "..." : text;
        }

        private static List<KeyValuePair<string, List<string>>> Scenarios(GameSharedData shared, RedLogger.IRedLogger logger)
        {
            List<KeyValuePair<string, List<string>>> groups = new List<KeyValuePair<string, List<string>>>();
            for (int kind = 0; kind < s_EventKinds.Length; kind++)
            {
                groups.Add(new KeyValuePair<string, List<string>>("events " + s_EventKinds[kind], EventScenarios(kind, kind < 3 ? 1500 : 700)));
            }
            groups.Add(new KeyValuePair<string, List<string>>("filter", FilterScenarios(shared)));
            groups.Add(new KeyValuePair<string, List<string>>("authority", AuthorityScenarios(shared, logger)));
            groups.Add(new KeyValuePair<string, List<string>>("rng", RngScenarios()));
            groups.Add(new KeyValuePair<string, List<string>>("presize", PresizeScenarios()));
            return groups;
        }

        // Xorshift: the same numbers on every runtime.
        private sealed class Rng
        {
            private ulong m_State;

            public Rng(ulong seed)
            {
                m_State = seed * 2685821657736338717UL + 1442695040888963407UL;
            }

            public int Next(int bound)
            {
                m_State ^= m_State << 13;
                m_State ^= m_State >> 7;
                m_State ^= m_State << 17;
                return (int)((m_State >> 33) % (ulong)bound);
            }
        }

        // ---- events -------------------------------------------------------

        private static readonly int[] s_Priorities = { int.MinValue, -3, 0, 1000, 1000, 5, 5, int.MaxValue };

        private static readonly string[] s_EventKinds =
        {
            "Event<int>", "Event", "Event<int,int>", "Event<Card,CardPosition>", "Event<Card>", "Event<Location>",
            "Event<Card,int,int>", "Event<EPlayerId,bool,bool>", "Event<StringBuilder>",
        };

        // Dummy engine objects for the typed events' arguments: no
        // constructor runs, so no controller is needed.
        private static readonly Card[] s_Cards =
        {
            (Card)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Card)),
            (Card)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Card)),
            (Card)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Card)),
        };

        private static readonly StringBuilder[] s_Builders = { new StringBuilder(), new StringBuilder() };

        private static readonly Location[] s_Locations =
        {
            (Location)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Location)),
            (Location)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Location)),
        };

        // Listeners are objects whose delegates compare equal when made
        // from the same target and method. Each has a fixed behaviour when
        // called, and reports the arguments it received as one number.
        private sealed class Listener
        {
            public int Id;
            public int Behaviour;
            public EventScenario Scenario;

            public void Call(int a) { Scenario.OnCall(this, a); }
            public void CallPair(int a, int b) { Scenario.OnCall(this, a * 31 + b); }
            public void CallPlain() { Scenario.OnCall(this, int.MinValue); }
            public void CallCardPosition(Card c, CardPosition p) { Scenario.OnCall(this, Array.IndexOf(s_Cards, c) * 1000 + p.Index); }
            public void CallCard(Card c) { Scenario.OnCall(this, Array.IndexOf(s_Cards, c)); }
            public void CallLocation(Location l) { Scenario.OnCall(this, Array.IndexOf(s_Locations, l)); }
            public void CallCardInts(Card c, int a, int b) { Scenario.OnCall(this, Array.IndexOf(s_Cards, c) * 1000 + a * 31 + b); }
            public void CallBuilder(StringBuilder b) { Scenario.OnCall(this, Array.IndexOf(s_Builders, b)); }
            public void CallTriple(EPlayerId p, bool a, bool b) { Scenario.OnCall(this, (int)p * 100 + (a ? 10 : 0) + (b ? 1 : 0)); }
        }

        private sealed class EventScenario
        {
            private readonly Rng m_Rng;
            private readonly StringBuilder m_Log = new StringBuilder();
            private readonly Listener[] m_Listeners;
            private readonly int m_Kind;
            private readonly BaseEvent m_Event;
            private readonly Delegate[] m_Last;
            private int m_Depth;
            private int m_Calls;

            public EventScenario(int seed, int kind)
            {
                m_Kind = kind;
                m_Rng = new Rng((ulong)seed);
                m_Event = EnginePackSelfTest.NewEvent(kind);
                m_Listeners = new Listener[6];
                m_Last = new Delegate[m_Listeners.Length];
                for (int i = 0; i < m_Listeners.Length; i++)
                {
                    m_Listeners[i] = new Listener { Id = i, Behaviour = m_Rng.Next(9), Scenario = this };
                }
            }

            public string Play(int operations)
            {
                for (int step = 0; step < operations; step++)
                {
                    int op = m_Rng.Next(10);
                    Guarded(() => Operate(op, m_Rng.Next(m_Listeners.Length), m_Rng.Next(3), m_Rng.Next(s_Priorities.Length), m_Rng.Next(5)));
                    m_Log.Append(m_Event.HasListeners() ? 'H' : 'h');
                    for (int i = 0; i < m_Listeners.Length; i++)
                    {
                        m_Log.Append(m_Event.HasListener(Make(i, false)) ? '1' : '0');
                    }
                    m_Log.Append(';');
                }
                return m_Log.ToString();
            }

            private void Guarded(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    m_Log.Append('!').Append(e.GetType().Name).Append(';');
                }
            }

            // A delegate for listener i: its last one (the same reference,
            // for reference-equality removal) or a new equal one.
            private Delegate Make(int i, bool reuse)
            {
                if (reuse && m_Last[i] != null)
                {
                    return m_Last[i];
                }
                Listener l = m_Listeners[i];
                Delegate made;
                switch (m_Kind)
                {
                    case 0: made = new Action<int>(l.Call); break;
                    case 1: made = new Action(l.CallPlain); break;
                    case 2: made = new Action<int, int>(l.CallPair); break;
                    case 3: made = new Action<Card, CardPosition>(l.CallCardPosition); break;
                    case 4: made = new Action<Card>(l.CallCard); break;
                    case 5: made = new Action<Location>(l.CallLocation); break;
                    case 6: made = new Action<Card, int, int>(l.CallCardInts); break;
                    case 7: made = new Action<EPlayerId, bool, bool>(l.CallTriple); break;
                    default: made = new Action<StringBuilder>(l.CallBuilder); break;
                }
                m_Last[i] = made;
                return made;
            }

            private void Add(int i, bool single, int priority, bool reuse)
            {
                Delegate d = Make(i, reuse);
                switch (m_Kind)
                {
                    case 0: ((Event<int>)m_Event).AddListener((Action<int>)d, single, priority); break;
                    case 1: ((Event)m_Event).AddListener((Action)d, single, priority); break;
                    case 2: ((Event<int, int>)m_Event).AddListener((Action<int, int>)d, single, priority); break;
                    case 3: ((Event<Card, CardPosition>)m_Event).AddListener((Action<Card, CardPosition>)d, single, priority); break;
                    case 4: ((Event<Card>)m_Event).AddListener((Action<Card>)d, single, priority); break;
                    case 5: ((Event<Location>)m_Event).AddListener((Action<Location>)d, single, priority); break;
                    case 6: ((Event<Card, int, int>)m_Event).AddListener((Action<Card, int, int>)d, single, priority); break;
                    case 7: ((Event<EPlayerId, bool, bool>)m_Event).AddListener((Action<EPlayerId, bool, bool>)d, single, priority); break;
                    default: ((Event<StringBuilder>)m_Event).AddListener((Action<StringBuilder>)d, single, priority); break;
                }
            }

            private void Remove(int i, bool reuse)
            {
                Delegate d = Make(i, reuse);
                switch (m_Kind)
                {
                    case 0: ((Event<int>)m_Event).RemoveListener((Action<int>)d); break;
                    case 1: ((Event)m_Event).RemoveListener((Action)d); break;
                    case 2: ((Event<int, int>)m_Event).RemoveListener((Action<int, int>)d); break;
                    case 3: ((Event<Card, CardPosition>)m_Event).RemoveListener((Action<Card, CardPosition>)d); break;
                    case 4: ((Event<Card>)m_Event).RemoveListener((Action<Card>)d); break;
                    case 5: ((Event<Location>)m_Event).RemoveListener((Action<Location>)d); break;
                    case 6: ((Event<Card, int, int>)m_Event).RemoveListener((Action<Card, int, int>)d); break;
                    case 7: ((Event<EPlayerId, bool, bool>)m_Event).RemoveListener((Action<EPlayerId, bool, bool>)d); break;
                    default: ((Event<StringBuilder>)m_Event).RemoveListener((Action<StringBuilder>)d); break;
                }
            }

            private void Fire(int arg)
            {
                Card card = s_Cards[Math.Abs(arg) % s_Cards.Length];
                switch (m_Kind)
                {
                    case 0: ((Event<int>)m_Event).Invoke(arg); break;
                    case 1: ((Event)m_Event).Invoke(); break;
                    case 2: ((Event<int, int>)m_Event).Invoke(arg, arg + 1); break;
                    case 3: ((Event<Card, CardPosition>)m_Event).Invoke(card, new CardPosition(EPlayerId.P1, ELocation.Hand, arg)); break;
                    case 4: ((Event<Card>)m_Event).Invoke(card); break;
                    case 5: ((Event<Location>)m_Event).Invoke(s_Locations[Math.Abs(arg) % s_Locations.Length]); break;
                    case 6: ((Event<Card, int, int>)m_Event).Invoke(card, arg, arg + 7); break;
                    case 7: ((Event<EPlayerId, bool, bool>)m_Event).Invoke(arg % 2 == 0 ? EPlayerId.P1 : EPlayerId.P2, arg % 3 == 0, arg % 5 == 0); break;
                    default: ((Event<StringBuilder>)m_Event).Invoke(s_Builders[Math.Abs(arg) % s_Builders.Length]); break;
                }
            }

            private void Operate(int op, int i, int flavour, int priority, int arg)
            {
                switch (op)
                {
                    case 0:
                    case 1:
                    case 2:
                        Add(i, false, s_Priorities[priority], flavour == 0);
                        break;
                    case 3:
                        Add(i, true, s_Priorities[priority], flavour == 0);
                        break;
                    case 4:
                    case 5:
                        Remove(i, flavour != 0);
                        break;
                    case 6:
                        m_Event.RemoveAllListeners();
                        m_Log.Append("R;");
                        break;
                    default:
                        m_Log.Append("I").Append(arg).Append(':');
                        Fire(arg);
                        m_Log.Append(';');
                        break;
                }
            }

            // What a listener does when called: log, then act by behaviour.
            public void OnCall(Listener l, int arg)
            {
                m_Log.Append('c').Append(l.Id).Append('(').Append(arg).Append(')');
                // Listeners that add listeners and fire the event can snowball:
                // after 300 calls in a scenario they only log.
                if (++m_Calls > 300)
                {
                    return;
                }
                int next = (l.Id * 7 + Math.Abs(arg % 1000) * 3 + 11) & 0xff;
                switch (l.Behaviour)
                {
                    case 1:
                        if (m_Depth < 2)
                        {
                            m_Depth++;
                            try { Fire(next); } finally { m_Depth--; }
                        }
                        break;
                    case 2:
                        Remove(l.Id, next % 2 == 0);
                        break;
                    case 3:
                        Remove((l.Id + 1) % m_Listeners.Length, next % 2 == 0);
                        break;
                    case 4:
                        Add((l.Id + 2) % m_Listeners.Length, next % 3 == 0, s_Priorities[next % s_Priorities.Length], next % 2 == 0);
                        break;
                    case 5:
                        Add(l.Id, true, s_Priorities[next % s_Priorities.Length], false);
                        break;
                    case 6:
                        if (next % 5 == 0)
                        {
                            m_Event.RemoveAllListeners();
                        }
                        break;
                    case 7:
                        if (next % 3 == 0)
                        {
                            throw new InvalidOperationException("listener " + l.Id);
                        }
                        break;
                    case 8:
                        if (m_Depth < 2)
                        {
                            m_Depth++;
                            try { Fire(next + 2); } finally { m_Depth--; }
                        }
                        Remove((l.Id + 3) % m_Listeners.Length, true);
                        break;
                }
            }
        }

        private static List<string> EventScenarios(int kind, int count)
        {
            List<string> lines = new List<string>();
            for (int s = 0; s < count; s++)
            {
                lines.Add(new EventScenario((kind + 1) * 100000 + s, kind).Play(40));
            }
            return lines;
        }

        private static BaseEvent NewEvent(int kind)
        {
            switch (kind)
            {
                case 0: return new Event<int>();
                case 1: return new Event();
                case 2: return new Event<int, int>();
                case 3: return new Event<Card, CardPosition>();
                case 4: return new Event<Card>();
                case 5: return new Event<Location>();
                case 6: return new Event<Card, int, int>();
                case 7: return new Event<EPlayerId, bool, bool>();
                default: return new Event<StringBuilder>();
            }
        }

        // The patches took effect: a new typed event of the engine's has no
        // argument array until it gets a listener, and every patched method
        // carries the pack's prefix.
        private static bool Live()
        {
            bool live = true;
#if !NETCOREAPP
            for (int kind = 3; kind < s_EventKinds.Length; kind++)
            {
                BaseEvent fresh = NewEvent(kind);
                bool armed = ArgsOf(fresh) != null;
                Console.WriteLine($"engine-pack: new {s_EventKinds[kind]} has {(armed ? "an" : "no")} argument array" +
                                  (kind == 8 ? " (a type the engine does not use, sharing code with listed ones)" : ""));
                live &= !armed;
            }
#endif
            foreach (string name in new[] { "HasListener", "AddListener", "RemoveListener", "Invoke", "RemoveAllListeners", "HasListeners" })
            {
                MethodBase method = typeof(BaseEvent).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                HarmonyLib.Patches patches = HarmonyLib.Harmony.GetPatchInfo(method);
                bool patched = patches != null && patches.Prefixes.Count > 0;
                Console.WriteLine($"engine-pack: BaseEvent.{name} {(patched ? "patched" : "NOT patched")}");
                live &= patched;
            }
            return live;
        }

        private static object ArgsOf(BaseEvent instance)
        {
            for (Type t = instance.GetType(); t != null; t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType == typeof(object[]))
                    {
                        return field.GetValue(instance);
                    }
                }
            }
            return null;
        }

        // Event types the invoke part rewrites: the checks above only test
        // the rewrite when these are among them.
        private static string PatchedTypes()
        {
            List<Type> engine = EventPatches.EngineEventTypes();
            StringBuilder text = new StringBuilder();
            foreach (Type type in new[]
            {
                typeof(Event<Card, CardPosition>), typeof(Event<Card>), typeof(Event<Location>),
                typeof(Event<Card, int, int>), typeof(Event<EPlayerId, bool, bool>),
            })
            {
                text.Append(type.Name).Append(engine.Contains(type) ? " in the engine set; " : " MISSING from the engine set; ");
            }
            return $"{engine.Count} engine event types; {text}";
        }

        // ---- filter -------------------------------------------------------

        private static Enum Pick(Rng rng, Type type)
        {
            Array values = Enum.GetValues(type);
            long value = 0;
            int parts = rng.Next(3);
            for (int i = 0; i < parts; i++)
            {
                value |= Convert.ToInt64(values.GetValue(rng.Next(values.Length)));
            }
            return (Enum)Enum.ToObject(type, value);
        }

        private static List<string> FilterScenarios(GameSharedData shared)
        {
            List<string> lines = new List<string>();
            Dictionary<int, RuntimeCardTemplate>.ValueCollection all = shared.SharedRuntimeTemplates.Values;
            List<RuntimeCardTemplate> listed = new List<RuntimeCardTemplate>(all);
            Rng rng = new Rng(77);
            for (int round = 0; round < 2; round++)
            {
                // Round 1 runs the same tuples again: after install, a hit
                // where round 0 was a miss.
                rng = new Rng(77);
                for (int n = 0; n < 500; n++)
                {
                    EnumBitArray<EAvailability> availability = null;
                    switch (rng.Next(4))
                    {
                        case 1: availability = new EnumBitArray<EAvailability>(); break;
                        case 2: availability = new EnumBitArray<EAvailability>(EAvailability.BaseSet); break;
                        case 3:
                            availability = new EnumBitArray<EAvailability>();
                            availability.Add((EAvailability)Pick(rng, typeof(EAvailability)));
                            break;
                    }
                    CardCategory category = null;
                    switch (rng.Next(4))
                    {
                        case 1: category = new CardCategory(); break;
                        case 2:
                        case 3:
                            category = new CardCategory();
                            category.Copy(listed[rng.Next(listed.Count)].Template.Categories, null);
                            break;
                    }
                    List<CardDefinition> valid = null;
                    int validKind = rng.Next(4);
                    if (validKind > 0)
                    {
                        valid = new List<CardDefinition>();
                        int size = validKind == 1 ? 0 : 1 + rng.Next(validKind == 2 ? 3 : 40);
                        for (int v = 0; v < size; v++)
                        {
                            valid.Add(new CardDefinition(rng.Next(4) == 0 ? rng.Next(100000) : listed[rng.Next(listed.Count)].Template.Id, rng.Next(2) == 0));
                        }
                    }
                    ECardKind kind = rng.Next(3) == 0 ? ECardKind.Normal : (ECardKind)Pick(rng, typeof(ECardKind));
                    List<CardDefinition> result = new List<CardDefinition>();
                    int prefix = rng.Next(3);
                    for (int p = 0; p < prefix; p++)
                    {
                        result.Add(new CardDefinition(900000 + p, p == 1));
                    }
                    IEnumerable<RuntimeCardTemplate> source = rng.Next(8) == 0 ? (IEnumerable<RuntimeCardTemplate>)listed : all;
                    CardDefinitionHelper.FilterDefinitions(source, result, availability, kind,
                        (EFactionId)Pick(rng, typeof(EFactionId)), (ECardTier)Pick(rng, typeof(ECardTier)),
                        (ECardType)Pick(rng, typeof(ECardType)), (ECardToken)Pick(rng, typeof(ECardToken)),
                        (ELocation)Pick(rng, typeof(ELocation)), (ELocation)Pick(rng, typeof(ELocation)),
                        category, (ECategoryCompareMethod)rng.Next(3), rng.Next(2) == 0, valid);
                    StringBuilder line = new StringBuilder();
                    for (int r = 0; r < result.Count; r++)
                    {
                        line.Append(result[r].TemplateId).Append(result[r].IsPremium ? 'p' : 'n').Append(',');
                    }
                    lines.Add(line.ToString());
                }
            }
            return lines;
        }

        // ---- authority ----------------------------------------------------

        private static List<string> AuthorityScenarios(GameSharedData shared, RedLogger.IRedLogger logger)
        {
            List<string> lines = new List<string>();
            List<Type> types = new List<Type>();
            foreach (Type type in LoadableTypes(typeof(AAction).Assembly))
            {
                if (typeof(AAction).IsAssignableFrom(type) && !type.IsAbstract && !type.IsGenericTypeDefinition &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                {
                    types.Add(type);
                }
            }
            types.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (EGameMode mode in new[] { EGameMode.SinglePlayer, EGameMode.Client, EGameMode.Server, EGameMode.Replay, EGameMode.Spectator })
            {
                GameInstance instance = new GameInstance(logger, mode, shared, new BattleSettings());
                foreach (Type type in types)
                {
                    AAction action = (AAction)Activator.CreateInstance(type);
                    action.SetGameController(instance.GameController);
                    lines.Add(mode + " " + type.Name + " " + Outcome(action));
                }
            }
            // No controller: authority actions fail with a null reference, others pass.
            foreach (Type type in types)
            {
                lines.Add("none " + type.Name + " " + Outcome((AAction)Activator.CreateInstance(type)));
            }
            return lines;
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                List<Type> loaded = new List<Type>();
                foreach (Type type in e.Types)
                {
                    if (type != null)
                    {
                        loaded.Add(type);
                    }
                }
                return loaded;
            }
        }

        private static string Outcome(AAction action)
        {
            try
            {
                action.CheckAuthority();
                return "ok";
            }
            catch (Exception e)
            {
                return e.GetType().Name + ":" + e.Message;
            }
        }

        // ---- rng ----------------------------------------------------------

        // Every private field of a generator (the state array included) as
        // one hash: the patches replace methods that write them.
        private static string Fields(MersenneTwisterRandom generator)
        {
            ulong hash = 14695981039346656037UL;
            foreach (FieldInfo field in typeof(MersenneTwisterRandom).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                object value = field.GetValue(generator);
                uint[] words = value as uint[];
                if (words != null)
                {
                    foreach (uint word in words)
                    {
                        hash = (hash ^ word) * 1099511628211UL;
                    }
                }
                else
                {
                    hash = (hash ^ unchecked((uint)Convert.ToInt64(value))) * 1099511628211UL;
                }
            }
            return hash.ToString("x16");
        }

        private static List<string> RngScenarios()
        {
            List<string> lines = new List<string>();
            Rng rng = new Rng(5);
            int[] seeds = { 0, 1, 2, -1, 7, 12345, int.MaxValue, int.MinValue, 910000, 910007 };
            for (int s = 0; s < 40; s++)
            {
                int seed = s < seeds.Length ? seeds[s] : rng.Next(int.MaxValue) - rng.Next(1000000);
                MersenneTwisterRandom generator = new MersenneTwisterRandom(seed);
                StringBuilder line = new StringBuilder();
                line.Append(Fields(generator)).Append(':');
                int draws = 1 + rng.Next(3000);
                for (int i = 0; i < draws; i++)
                {
                    switch (i % 5)
                    {
                        case 0: line.Append(generator.NextUnsigned()); break;
                        case 1: line.Append(generator.Next()); break;
                        case 2: line.Append(generator.Next(1 + rng.Next(100))); break;
                        case 3: line.Append(generator.Next(-5, 50)); break;
                        default: line.Append(generator.NextDouble()); break;
                    }
                    line.Append(',');
                }
                line.Append(Fields(generator)).Append("|copy:");
                // Copy replays the draws from the seed: the other generator
                // starts from a time seed and must end where this one is.
                MersenneTwisterRandom copy = new MersenneTwisterRandom();
                copy.Copy(generator);
                line.Append(Fields(copy)).Append(',').Append(copy.InitialSeed).Append(',').Append(copy.NumTimesCalledSinceCreation).Append(':');
                for (int i = 0; i < 700; i++)
                {
                    line.Append(copy.NextUnsigned() == generator.NextUnsigned() ? '=' : '!');
                }
                MersenneTwisterRandom moved = new MersenneTwisterRandom(seed + 1);
                moved.SetToNewSeededPosition(generator);
                line.Append("|moved:").Append(Fields(moved)).Append(':').Append(moved.NextUnsigned());
                lines.Add(line.ToString());
            }
            return lines;
        }

        // ---- presize ------------------------------------------------------

        private static List<string> PresizeScenarios()
        {
            List<string> lines = new List<string>();
            for (int count = 0; count < 20; count++)
            {
                AbilityVariables source = new AbilityVariables();
                for (int i = 0; i < count; i++)
                {
                    source.Variables.Add(new IntVar(i * 3 + 1));
                }
                AbilityVariables copy = source.Clone(null);
                StringBuilder line = new StringBuilder();
                line.Append(copy.Variables.Count).Append(':');
                for (int i = 0; i < copy.Variables.Count; i++)
                {
                    line.Append(((IntVar)copy.Variables[i]).Value).Append(',');
                }
                lines.Add(line.ToString());
            }
            return lines;
        }
    }
}
