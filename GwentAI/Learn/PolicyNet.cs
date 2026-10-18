using System;
using System.Collections.Generic;
using System.IO;
using GwentAI.Search;
using GwentGameplay;

namespace GwentAI.Learn
{
    // The steps a seat may take, as raw integers the policy net reads
    // (PolicyNet) and that code driving the AI can record. One option is
    // OptionFields integers; one request's context is ContextFields integers.
    public static class OptionEncoder
    {
        // Bump on any change to what is encoded or how; weights carry it and
        // are refused on a mismatch.
        public const int Version = 1;

        // Per option: kind (OptionKind), template id of the card named (0:
        // none or hidden), its power and armour, side (0 none, 1 the mover's,
        // 2 the opponent's), location (see Location), position index or
        // hidden-choice slot, hidden (1: the step names a card the observer
        // cannot see).
        public const int OptionFields = 8;
        public const int KindCount = 9;
        public const int SideCount = 3;
        public const int LocationCount = 8;

        // Per request: kind (0 PlayCard, 1 Mulligan, 2 TargetCards, 3
        // TargetLocations, 4 Choice), the template of the card whose ability
        // asks (targets, rows, choices; 0 otherwise), steps already taken in
        // this response, legal steps.
        public const int ContextFields = 4;
        public const int RequestKinds = 5;

        public static void Encode(GameAction action, EPlayerId mover, int[] into, int at)
        {
            Encode(action.Kind, action.Card, action.Side, action.Location, action.Index, action.Hidden, mover, into, at);
        }

        public static void Encode(OptionKind kind, Card card, EPlayerId side, ELocation location, int index, bool hidden, EPlayerId mover, int[] into, int at)
        {
            into[at] = (int)kind;
            if (card != null)
            {
                into[at + 1] = card.Template.Id;
                into[at + 2] = card.Power;
                into[at + 3] = card.Armor;
            }
            else
            {
                into[at + 1] = 0;
                into[at + 2] = 0;
                into[at + 3] = 0;
            }
            into[at + 4] = side == 0 ? 0 : side == mover ? 1 : 2;
            into[at + 5] = Location(location);
            into[at + 6] = index;
            into[at + 7] = hidden ? 1 : 0;
        }

        public static int Location(ELocation location)
        {
            switch (location)
            {
                case ELocation.Melee: return 1;
                case ELocation.Ranged: return 2;
                case ELocation.Siege: return 3;
                case ELocation.Hand: return 4;
                case ELocation.Deck: return 5;
                case ELocation.Graveyard: return 6;
                default: return location == 0 ? 0 : 7;
            }
        }

        public static int RequestKind(ARequestAction request)
        {
            switch (request.GetRequestType())
            {
                case ERequestType.PlayCard: return 0;
                case ERequestType.Mulligan: return 1;
                case ERequestType.TargetCards: return 2;
                case ERequestType.TargetLocations: return 3;
                case ERequestType.Choice: return 4;
                default: return 0;
            }
        }

        // The card whose ability asks, for targets, rows and choices.
        public static int SourceTemplate(ARequestAction request)
        {
            ushort requester;
            switch (request)
            {
                case ARequestTargetsAction targets: requester = targets.RequesterId; break;
                case RequestCardChoicesAction choices: requester = choices.RequesterId; break;
                default: return 0;
            }
            Card source = request.GetCard(requester);
            return source == null ? 0 : source.Template.Id;
        }

        public static void Context(ARequestAction request, int taken, int legal, int[] into)
        {
            into[0] = RequestKind(request);
            into[1] = SourceTemplate(request);
            into[2] = taken;
            into[3] = legal;
        }
    }

    // The policy net (fitted offline): a score per legal
    // step, from the mover's view of the game (StateEncoder), the request's
    // context and each step's option features (OptionEncoder). Softmax over
    // the legal steps gives the prior the search uses at a node (PUCT) and
    // the distribution a rollout samples from.
    //
    // Model: the value net's trunk (zone sums and scaled globals, plus the
    // request context) ends in a vector t; each option's features pass
    // through one ReLU layer to a vector u; the score is a bilinear form
    // (Wout t + wb) . u. The state is encoded once per request and the
    // option layer costs a few hundred multiplies per step. Weights are
    // float32, sums are double; one instance is not thread-safe (it reuses
    // its buffers: no allocation per call).
    public sealed class PolicyNet
    {
        public const int Magic = 0x314E5047; // "GPN1"
        public const int FileVersion = 1;
        // Dense option features: power, armour (in tens), side one-hot,
        // location one-hot, index (in tens), hidden.
        public const int OptionDense = 2 + OptionEncoder.SideCount + OptionEncoder.LocationCount + 2;
        // Dense context: request kind one-hot, the source template's inputs
        // (F, appended by the net), taken, legal (in tens).
        public const int ContextDenseFixed = OptionEncoder.RequestKinds + 2;

        private readonly int m_Vocab, m_TemplateInputs, m_Hidden, m_H1, m_H2, m_Trunk, m_OptionHidden, m_Input;
        private readonly Dictionary<int, int> m_Index = new Dictionary<int, int>();
        private readonly double[] m_TemplateInputsTable; // [V][F]
        private readonly double[] m_Templates;           // [Z][V][H]
        private readonly double[] m_Dynamic;             // [Z][D][H]
        private readonly double[] m_GlobalMean, m_GlobalScale;
        private readonly double[] m_W1, m_B1, m_W2, m_B2, m_Wt, m_Bt;
        private readonly double[] m_Kind;                // [K][Ho]
        private readonly double[] m_OptionTemplates;     // [V][Ho] = Wtm . x[v]
        private readonly double[] m_OptionDynamic;       // [Ho][OD]
        private readonly double[] m_OptionBias;          // [Ho]
        private readonly double[] m_Out;                 // [T][Ho]
        private readonly double[] m_OutBias;             // [Ho]

        private readonly EncodedState m_State = new EncodedState();
        private readonly int[] m_Context = new int[OptionEncoder.ContextFields];
        private int[] m_Options = new int[32 * OptionEncoder.OptionFields];
        private int m_OptionCount;
        private readonly double[] m_Zones, m_DynamicSums, m_X, m_A1, m_A2, m_T, m_V, m_U;
        private double[] m_Scores = new double[32];
        private double[] m_Probs = new double[32];

        public string Name;
        // Calls and time spent (encoding and forward pass), for timing reports.
        public long Calls;
        public long Ticks;
        // Options whose kind the net is never fitted on (another player's
        // face-down card or mulligan), scored as the mean of the rest.
        public long HiddenFallbacks;

        private PolicyNet(BinaryReader reader)
        {
            if (reader.ReadInt32() != Magic)
            {
                throw new InvalidDataException("Not a policy net file");
            }
            int fileVersion = reader.ReadInt32();
            int encoding = reader.ReadInt32();
            int options = reader.ReadInt32();
            if (fileVersion != FileVersion || encoding != StateEncoder.Version || options != OptionEncoder.Version)
            {
                throw new InvalidDataException($"Policy net file version {fileVersion}, encoding {encoding}, options {options}; " +
                                               $"this build reads {FileVersion}, encoding {StateEncoder.Version}, options {OptionEncoder.Version}");
            }
            m_Vocab = reader.ReadInt32();
            int zones = reader.ReadInt32();
            m_TemplateInputs = reader.ReadInt32();
            m_Hidden = reader.ReadInt32();
            int dynamic = reader.ReadInt32();
            int globals = reader.ReadInt32();
            m_H1 = reader.ReadInt32();
            m_H2 = reader.ReadInt32();
            m_Trunk = reader.ReadInt32();
            m_OptionHidden = reader.ReadInt32();
            if (zones != StateEncoder.ZoneCount || dynamic != ValueNet.DynamicCount || globals != StateEncoder.GlobalCount)
            {
                throw new InvalidDataException($"Policy net shapes: {zones} zones, {dynamic} dynamic, {globals} globals");
            }
            for (int v = 0; v < m_Vocab; v++)
            {
                int template = reader.ReadInt32();
                if (v >= 2)
                {
                    m_Index[template] = v;
                }
            }
            float[] templateInputs = Read(reader, m_Vocab * m_TemplateInputs);
            m_TemplateInputsTable = Widen(templateInputs);
            float[] projections = Read(reader, zones * m_TemplateInputs * m_Hidden);
            float[] zoneBias = Read(reader, zones * m_Hidden);
            m_Dynamic = Widen(Read(reader, zones * ValueNet.DynamicCount * m_Hidden));
            m_GlobalMean = Widen(Read(reader, globals));
            m_GlobalScale = Widen(Read(reader, globals));
            m_Input = zones * m_Hidden + globals + ContextDenseFixed + m_TemplateInputs;
            m_W1 = Widen(Read(reader, m_H1 * m_Input));
            m_B1 = Widen(Read(reader, m_H1));
            m_W2 = Widen(Read(reader, m_H2 * m_H1));
            m_B2 = Widen(Read(reader, m_H2));
            m_Wt = Widen(Read(reader, m_Trunk * m_H2));
            m_Bt = Widen(Read(reader, m_Trunk));
            m_Kind = Widen(Read(reader, OptionEncoder.KindCount * m_OptionHidden));
            float[] optionTemplates = Read(reader, m_OptionHidden * m_TemplateInputs);
            m_OptionDynamic = Widen(Read(reader, m_OptionHidden * OptionDense));
            m_OptionBias = Widen(Read(reader, m_OptionHidden));
            m_Out = Widen(Read(reader, m_Trunk * m_OptionHidden));
            m_OutBias = Widen(Read(reader, m_OptionHidden));
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException("Policy net file has trailing data");
            }

            m_Templates = new double[zones * m_Vocab * m_Hidden];
            for (int z = 0; z < zones; z++)
            {
                for (int v = 0; v < m_Vocab; v++)
                {
                    int to = (z * m_Vocab + v) * m_Hidden;
                    for (int h = 0; h < m_Hidden; h++)
                    {
                        double sum = zoneBias[z * m_Hidden + h];
                        for (int f = 0; f < m_TemplateInputs; f++)
                        {
                            sum += (double)templateInputs[v * m_TemplateInputs + f] * projections[(z * m_TemplateInputs + f) * m_Hidden + h];
                        }
                        m_Templates[to + h] = sum;
                    }
                }
            }
            m_OptionTemplates = new double[m_Vocab * m_OptionHidden];
            for (int v = 0; v < m_Vocab; v++)
            {
                for (int h = 0; h < m_OptionHidden; h++)
                {
                    double sum = 0;
                    for (int f = 0; f < m_TemplateInputs; f++)
                    {
                        sum += (double)optionTemplates[h * m_TemplateInputs + f] * templateInputs[v * m_TemplateInputs + f];
                    }
                    m_OptionTemplates[v * m_OptionHidden + h] = sum;
                }
            }
            m_Zones = new double[zones * m_Hidden];
            m_DynamicSums = new double[zones * ValueNet.DynamicCount];
            m_X = new double[m_Input];
            m_A1 = new double[m_H1];
            m_A2 = new double[m_H2];
            m_T = new double[m_Trunk];
            m_V = new double[m_OptionHidden];
            m_U = new double[m_OptionHidden];
        }

        // A net on the weights of another, which stay shared (nothing writes
        // them after the constructor above): its own buffers and counters.
        private PolicyNet(PolicyNet weights)
        {
            m_Vocab = weights.m_Vocab;
            m_TemplateInputs = weights.m_TemplateInputs;
            m_Hidden = weights.m_Hidden;
            m_H1 = weights.m_H1;
            m_H2 = weights.m_H2;
            m_Trunk = weights.m_Trunk;
            m_OptionHidden = weights.m_OptionHidden;
            m_Input = weights.m_Input;
            m_Index = weights.m_Index;
            m_TemplateInputsTable = weights.m_TemplateInputsTable;
            m_Templates = weights.m_Templates;
            m_Dynamic = weights.m_Dynamic;
            m_GlobalMean = weights.m_GlobalMean;
            m_GlobalScale = weights.m_GlobalScale;
            m_W1 = weights.m_W1;
            m_B1 = weights.m_B1;
            m_W2 = weights.m_W2;
            m_B2 = weights.m_B2;
            m_Wt = weights.m_Wt;
            m_Bt = weights.m_Bt;
            m_Kind = weights.m_Kind;
            m_OptionTemplates = weights.m_OptionTemplates;
            m_OptionDynamic = weights.m_OptionDynamic;
            m_OptionBias = weights.m_OptionBias;
            m_Out = weights.m_Out;
            m_OutBias = weights.m_OutBias;
            m_Zones = new double[weights.m_Zones.Length];
            m_DynamicSums = new double[weights.m_DynamicSums.Length];
            m_X = new double[m_Input];
            m_A1 = new double[m_H1];
            m_A2 = new double[m_H2];
            m_T = new double[m_Trunk];
            m_V = new double[m_OptionHidden];
            m_U = new double[m_OptionHidden];
            Name = weights.Name;
            Bytes = weights.Bytes;
        }

        // A new net of the same weights with its own buffers, counters and
        // fold state (a net keeps scratch state, so seats that must count
        // their passes apart each take one).
        public PolicyNet Fresh(string name)
        {
            return new PolicyNet(this) { Name = name };
        }

        private static float[] Read(BinaryReader reader, int count)
        {
            float[] values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = reader.ReadSingle();
            }
            return values;
        }

        private static double[] Widen(float[] values)
        {
            double[] wide = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                wide[i] = values[i];
            }
            return wide;
        }

        // The weights are parsed once a process (NetCache): every call after
        // the first for a file builds only the buffers.
        public static PolicyNet Load(string path)
        {
            return s_Cache.FromFile(path).Fresh(Path.GetFileNameWithoutExtension(path));
        }

        // A net from the bytes of its .pnet file, which it keeps (Bytes):
        // search workers receive the coordinator's net this way.
        public static PolicyNet FromBytes(byte[] bytes, string name)
        {
            return s_Cache.FromBytes(bytes).Fresh(name);
        }

        private static readonly NetCache<PolicyNet> s_Cache = new NetCache<PolicyNet>(bytes =>
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
            {
                return new PolicyNet(reader) { Bytes = bytes };
            }
        });

        // The .pnet file this net was read from.
        public byte[] Bytes { get; private set; }

        // The options of the next request, one by one.
        public void Begin()
        {
            m_OptionCount = 0;
        }

        public int Count => m_OptionCount;

        public void Add(GameAction action, EPlayerId mover)
        {
            Add(action.Kind, action.Card, action.Side, action.Location, action.Index, action.Hidden, mover);
        }

        public void Add(OptionKind kind, Card card, EPlayerId side, ELocation location, int index, bool hidden, EPlayerId mover)
        {
            int at = m_OptionCount * OptionEncoder.OptionFields;
            if (at + OptionEncoder.OptionFields > m_Options.Length)
            {
                int[] grown = new int[m_Options.Length * 2];
                Array.Copy(m_Options, grown, m_Options.Length);
                m_Options = grown;
                m_Scores = new double[m_Scores.Length * 2];
                m_Probs = new double[m_Probs.Length * 2];
            }
            OptionEncoder.Encode(kind, card, side, location, index, hidden, mover, m_Options, at);
            m_OptionCount++;
        }

        // The options' scores (logits) from the mover's view of the game,
        // for its open request with `taken` steps already chosen. The array
        // is reused by the next call; entries past Count are stale. Folded:
        // the same function through FoldedScores (rollouts with folded scoring).
        public double[] Evaluate(GameController game, EPlayerId mover, IDictionary<ushort, int> remembered, ARequestAction request, int taken,
                                 bool folded = false)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            StateEncoder.Encode(game, mover, remembered, false, m_State);
            OptionEncoder.Context(request, taken, m_OptionCount, m_Context);
            PhaseClock.Stop(Phase.PolicyEncode, start);
            long scoring = PhaseClock.Start();
            double[] scores = folded ? FoldedScores(m_State, m_Context, m_Options, m_OptionCount, mover.ToIndex())
                                     : Scores(m_State, m_Context, m_Options, m_OptionCount);
            PhaseClock.Stop(Phase.PolicyScore, scoring);
            Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            Calls++;
            QueryObserver?.Invoke(this, scores, remembered != null);
            return scores;
        }

        // Called after every evaluation with the net, its scores and whether
        // the view held the seat's memory of the opponent's cards (the
        // searching seat's own view); null: none. The query itself is in the
        // net's State, Context, Options and Count until its next call.
        public static Action<PolicyNet, double[], bool> QueryObserver;

        // The options' encoded features, OptionFields each, as last added.
        public int[] Options => m_Options;
        public int[] Context => m_Context;
        // The encoded view of the last evaluation.
        public EncodedState State => m_State;

        // Softmax of the first Count scores at a temperature, into an array
        // reused by the next call.
        public double[] Probabilities(double[] scores, double temperature)
        {
            int count = m_OptionCount;
            double best = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                if (scores[i] > best)
                {
                    best = scores[i];
                }
            }
            double total = 0;
            for (int i = 0; i < count; i++)
            {
                double e = Math.Exp((scores[i] - best) / temperature);
                m_Probs[i] = e;
                total += e;
            }
            for (int i = 0; i < count; i++)
            {
                m_Probs[i] /= total;
            }
            return m_Probs;
        }

        // The forward pass over an encoded state, request context and
        // options (count of them, OptionFields integers each).
        public double[] Scores(EncodedState state, int[] context, int[] options, int count)
        {
            int zones = StateEncoder.ZoneCount;
            Array.Clear(m_Zones, 0, m_Zones.Length);
            Array.Clear(m_DynamicSums, 0, m_DynamicSums.Length);
            int[] cards = state.Cards;
            for (int c = 0; c < state.CardCount; c++)
            {
                int at = c * StateEncoder.CardFields;
                int zone = cards[at];
                int v = VocabIndex(cards[at + 1], cards[at + 1] == 0);
                int from = (zone * m_Vocab + v) * m_Hidden;
                int to = zone * m_Hidden;
                for (int h = 0; h < m_Hidden; h++)
                {
                    m_Zones[to + h] += m_Templates[from + h];
                }
                AddDynamic(cards, at, zone);
            }
            for (int z = 0; z < zones; z++)
            {
                for (int k = 0; k < ValueNet.DynamicCount; k++)
                {
                    double sum = m_DynamicSums[z * ValueNet.DynamicCount + k];
                    if (sum == 0)
                    {
                        continue;
                    }
                    int w = (z * ValueNet.DynamicCount + k) * m_Hidden;
                    int to = z * m_Hidden;
                    for (int h = 0; h < m_Hidden; h++)
                    {
                        m_Zones[to + h] += sum * m_Dynamic[w + h];
                    }
                }
            }
            Array.Copy(m_Zones, m_X, m_Zones.Length);
            int[] globals = state.Globals;
            int x = m_Zones.Length;
            for (int i = 0; i < globals.Length; i++)
            {
                m_X[x + i] = (globals[i] - m_GlobalMean[i]) * m_GlobalScale[i];
            }
            x += globals.Length;
            for (int k = 0; k < OptionEncoder.RequestKinds; k++)
            {
                m_X[x + k] = context[0] == k ? 1 : 0;
            }
            x += OptionEncoder.RequestKinds;
            int source = VocabIndex(context[1], false) * m_TemplateInputs;
            for (int f = 0; f < m_TemplateInputs; f++)
            {
                m_X[x + f] = m_TemplateInputsTable[source + f];
            }
            x += m_TemplateInputs;
            m_X[x] = context[2];
            m_X[x + 1] = context[3] / 10.0;
            ValueNet.Dense(m_W1, m_B1, m_X, m_A1, true);
            ValueNet.Dense(m_W2, m_B2, m_A1, m_A2, true);
            ValueNet.Dense(m_Wt, m_Bt, m_A2, m_T, false);
            // v = Wout^T t + wb: the state's weighting of the option layer.
            int ho = m_OptionHidden;
            for (int h = 0; h < ho; h++)
            {
                m_V[h] = m_OutBias[h];
            }
            for (int k = 0; k < m_Trunk; k++)
            {
                double t = m_T[k];
                int row = k * ho;
                for (int h = 0; h < ho; h++)
                {
                    m_V[h] += m_Out[row + h] * t;
                }
            }
            return OptionScores(options, count);
        }

        // A card's dynamic inputs added to its zone's sums.
        private void AddDynamic(int[] cards, int at, int zone)
        {
            int d = zone * ValueNet.DynamicCount;
            m_DynamicSums[d] += cards[at + 2] / 10.0;
            m_DynamicSums[d + 1] += cards[at + 3] / 10.0;
            m_DynamicSums[d + 2] += cards[at + 4] / 10.0;
            int tokens = cards[at + 5];
            for (int bit = 0; bit < ValueNet.TokenBits; bit++)
            {
                m_DynamicSums[d + 3 + bit] += (tokens >> bit) & 1;
            }
            m_DynamicSums[d + 3 + ValueNet.TokenBits] += cards[at + 6] / 10.0;
        }

        // Each option's score from the state's weighting of the option
        // layer (m_V).
        private double[] OptionScores(int[] options, int count)
        {
            int ho = m_OptionHidden;
            double visibleSum = 0;
            int visible = 0;
            for (int o = 0; o < count; o++)
            {
                int at = o * OptionEncoder.OptionFields;
                int kind = options[at];
                bool hidden = options[at + 7] != 0;
                int v = VocabIndex(options[at + 1], hidden);
                int kindRow = kind * ho;
                int templateRow = v * ho;
                double power = options[at + 2] / 10.0, armor = options[at + 3] / 10.0, index = options[at + 6] / 10.0;
                int side = options[at + 4], location = options[at + 5];
                for (int h = 0; h < ho; h++)
                {
                    int w = h * OptionDense;
                    double sum = m_OptionBias[h] + m_Kind[kindRow + h] + m_OptionTemplates[templateRow + h]
                                 + m_OptionDynamic[w] * power + m_OptionDynamic[w + 1] * armor
                                 + m_OptionDynamic[w + 2 + side]
                                 + m_OptionDynamic[w + 2 + OptionEncoder.SideCount + location]
                                 + m_OptionDynamic[w + 2 + OptionEncoder.SideCount + OptionEncoder.LocationCount] * index
                                 + (hidden ? m_OptionDynamic[w + OptionDense - 1] : 0);
                    m_U[h] = sum < 0 ? 0 : sum;
                }
                double score = 0;
                for (int h = 0; h < ho; h++)
                {
                    score += m_V[h] * m_U[h];
                }
                m_Scores[o] = score;
                if (!(hidden && (kind == (int)OptionKind.Card || kind == (int)OptionKind.Play || kind == (int)OptionKind.Mulligan)))
                {
                    visibleSum += score;
                    visible++;
                }
            }
            // Kinds never seen in fitting (another player's face-down play
            // or mulligan) take the mean score of the rest.
            if (visible < count && visible > 0)
            {
                double mean = visibleSum / visible;
                for (int o = 0; o < count; o++)
                {
                    int at = o * OptionEncoder.OptionFields;
                    int kind = options[at];
                    if (options[at + 7] != 0 && (kind == (int)OptionKind.Card || kind == (int)OptionKind.Play || kind == (int)OptionKind.Mulligan))
                    {
                        m_Scores[o] = mean;
                        HiddenFallbacks++;
                    }
                }
            }
            return m_Scores;
        }

        // The forward pass of Scores with its linear parts folded into tables,
        // for the rollout.
        //
        // The trunk's first layer is linear in its inputs, and each input is
        // a sum over cards, a global or a context field, so W1 times the
        // input is a sum of W1's products with each part: one row of H1 per
        // card (W1 times the card's zone vector), per dynamic sum and per
        // global (the standardization folded into the row and the bias). A
        // seat's view changes little between its play-out steps (a card
        // played, one drawn, some powers), so each slot keeps the state part
        // of that sum for the view it last scored and adds the difference:
        // the rows of the cards that left or joined a zone, and the changed
        // dynamic sums and globals. The trunk's last layer and the option
        // layer's state weighting are linear too and fold into one matrix.
        // The tables fill on first use (a zone and template pair, a source
        // template), so a net pays only for the cards it meets.
        //
        // The same function as Scores; the sums run in another order, so a
        // score may differ in the last bits, and that difference depends on
        // the views the slot scored since its last ResetFold.
        // Slot: 0 or 1, one per seat whose views follow each other.
        public double[] FoldedScores(EncodedState state, int[] context, int[] options, int count, int slot)
        {
            if (m_FoldBias == null)
            {
                Fold();
            }
            int h1 = m_H1;
            double[] state1 = m_FoldState[slot];
            if (!m_FoldStarted[slot])
            {
                Array.Copy(m_FoldBias, state1, h1);
                m_FoldStarted[slot] = true;
            }
            FoldCards(state, slot, state1);
            int[] globals = state.Globals, seen = m_FoldGlobalsSeen[slot];
            for (int i = 0; i < globals.Length; i++)
            {
                int delta = globals[i] - seen[i];
                if (delta != 0)
                {
                    Add(state1, m_FoldGlobals, i * h1, delta, h1);
                    seen[i] = globals[i];
                }
            }
            double[] a = m_A1;
            Array.Copy(state1, a, h1);
            Add(a, m_FoldContext, context[0] * h1, 1, h1);
            int source = VocabIndex(context[1], false);
            if (!m_FoldSourceFilled[source])
            {
                FoldSource(source);
            }
            Add(a, m_FoldSources, source * h1, 1, h1);
            if (context[2] != 0)
            {
                Add(a, m_FoldContext, OptionEncoder.RequestKinds * h1, context[2], h1);
            }
            if (context[3] != 0)
            {
                Add(a, m_FoldContext, (OptionEncoder.RequestKinds + 1) * h1, context[3] / 10.0, h1);
            }
            for (int j = 0; j < h1; j++)
            {
                if (a[j] < 0)
                {
                    a[j] = 0;
                }
            }
            ValueNet.Dense(m_W2, m_B2, m_A1, m_A2, true);
            ValueNet.Dense(m_FoldOut, m_FoldOutBias, m_A2, m_V, false);
            return OptionScores(options, count);
        }

        // The cards' part of a slot's first-layer sum moved to this view: a
        // card row whose count changed adds the change times its row, and so
        // does each dynamic sum that changed.
        private void FoldCards(EncodedState state, int slot, double[] state1)
        {
            int h1 = m_H1;
            int[] cards = state.Cards;
            int[] rows = m_FoldRowsNext;
            if (rows.Length < state.CardCount)
            {
                rows = m_FoldRowsNext = new int[state.CardCount * 2];
            }
            Array.Clear(m_DynamicSums, 0, m_DynamicSums.Length);
            int[] delta = m_FoldRowDelta;
            for (int c = 0; c < state.CardCount; c++)
            {
                int at = c * StateEncoder.CardFields;
                int zone = cards[at];
                int row = zone * m_Vocab + VocabIndex(cards[at + 1], cards[at + 1] == 0);
                rows[c] = row;
                delta[row]++;
                AddDynamic(cards, at, zone);
            }
            int[] previous = m_FoldRows[slot];
            int previousCount = m_FoldRowCount[slot];
            for (int c = 0; c < previousCount; c++)
            {
                delta[previous[c]]--;
            }
            // Each changed row is applied once: the first visit applies its
            // change and clears it.
            ApplyRows(rows, state.CardCount, state1);
            ApplyRows(previous, previousCount, state1);
            m_FoldRowsNext = previous;
            m_FoldRows[slot] = rows;
            m_FoldRowCount[slot] = state.CardCount;
            double[] seen = m_FoldDynamicSeen[slot];
            for (int k = 0; k < m_DynamicSums.Length; k++)
            {
                double change = m_DynamicSums[k] - seen[k];
                if (change != 0)
                {
                    Add(state1, m_FoldDynamic, k * h1, change, h1);
                    seen[k] = m_DynamicSums[k];
                }
            }
        }

        private void ApplyRows(int[] rows, int count, double[] state1)
        {
            int h1 = m_H1;
            int[] delta = m_FoldRowDelta;
            for (int c = 0; c < count; c++)
            {
                int row = rows[c];
                int change = delta[row];
                if (change == 0)
                {
                    continue;
                }
                if (!m_FoldCardFilled[row])
                {
                    FoldCard(row);
                }
                Add(state1, m_FoldCards, row * h1, change, h1);
                delta[row] = 0;
            }
        }

        // Forgets the views the slots last scored: the next folded call of
        // each slot sums its view afresh. Ismcts calls it when a search
        // starts, so a folded score depends on that search alone and a
        // decision replays to the last bit in a new process.
        public void ResetFold()
        {
            for (int slot = 0; slot < 2; slot++)
            {
                m_FoldStarted[slot] = false;
                m_FoldRowCount[slot] = 0;
                if (m_FoldDynamicSeen[slot] != null)
                {
                    Array.Clear(m_FoldDynamicSeen[slot], 0, m_FoldDynamicSeen[slot].Length);
                    Array.Clear(m_FoldGlobalsSeen[slot], 0, m_FoldGlobalsSeen[slot].Length);
                }
            }
        }

        // a += scale * table[from .. from + n).
        private static void Add(double[] a, double[] table, int from, double scale, int n)
        {
            for (int j = 0; j < n; j++)
            {
                a[j] += scale * table[from + j];
            }
        }

        // FoldedScores' tables, each a row of H1 per part of the first
        // layer's input: cards by (zone, vocabulary row), dynamic sums by
        // (zone, feature), globals, the context (request kinds, then taken
        // and legal / 10) and source templates by vocabulary row; then the
        // trunk's last layer times the option weighting (Ho x H2) and its
        // bias. Per slot: the state part of the first layer's sum and the
        // view it was summed for (card rows, dynamic sums, globals).
        private double[] m_FoldBias, m_FoldCards, m_FoldDynamic, m_FoldGlobals, m_FoldContext, m_FoldSources, m_FoldOut, m_FoldOutBias;
        private bool[] m_FoldCardFilled, m_FoldSourceFilled;
        private int[] m_FoldRowDelta, m_FoldRowsNext;
        private readonly double[][] m_FoldState = new double[2][];
        private readonly bool[] m_FoldStarted = new bool[2];
        private readonly int[][] m_FoldRows = { new int[0], new int[0] };
        private readonly int[] m_FoldRowCount = new int[2];
        private readonly double[][] m_FoldDynamicSeen = new double[2][];
        private readonly int[][] m_FoldGlobalsSeen = new int[2][];

        private void Fold()
        {
            int h1 = m_H1, zones = StateEncoder.ZoneCount, globals = StateEncoder.GlobalCount, dynamic = ValueNet.DynamicCount;
            int globalAt = zones * m_Hidden, kindAt = globalAt + globals;
            m_FoldCards = new double[zones * m_Vocab * h1];
            m_FoldCardFilled = new bool[zones * m_Vocab];
            m_FoldRowDelta = new int[zones * m_Vocab];
            m_FoldRowsNext = new int[0];
            m_FoldSources = new double[m_Vocab * h1];
            m_FoldSourceFilled = new bool[m_Vocab];
            for (int slot = 0; slot < 2; slot++)
            {
                m_FoldState[slot] = new double[h1];
                m_FoldDynamicSeen[slot] = new double[zones * dynamic];
                m_FoldGlobalsSeen[slot] = new int[globals];
            }
            m_FoldDynamic = new double[zones * dynamic * h1];
            for (int z = 0; z < zones; z++)
            {
                for (int k = 0; k < dynamic; k++)
                {
                    int w = (z * dynamic + k) * m_Hidden, to = (z * dynamic + k) * h1;
                    for (int j = 0; j < h1; j++)
                    {
                        double sum = 0;
                        int row = j * m_Input + z * m_Hidden;
                        for (int h = 0; h < m_Hidden; h++)
                        {
                            sum += m_W1[row + h] * m_Dynamic[w + h];
                        }
                        m_FoldDynamic[to + j] = sum;
                    }
                }
            }
            // A global enters as (g - mean) * scale: g times (W1's column
            // times scale) per call, and - mean * scale times the column
            // once, in the bias.
            m_FoldGlobals = new double[globals * h1];
            double[] bias = new double[h1];
            for (int j = 0; j < h1; j++)
            {
                double sum = m_B1[j];
                for (int i = 0; i < globals; i++)
                {
                    double column = m_W1[j * m_Input + globalAt + i];
                    m_FoldGlobals[i * h1 + j] = column * m_GlobalScale[i];
                    sum -= column * m_GlobalMean[i] * m_GlobalScale[i];
                }
                bias[j] = sum;
            }
            m_FoldContext = new double[(OptionEncoder.RequestKinds + 2) * h1];
            int rest = kindAt + OptionEncoder.RequestKinds + m_TemplateInputs;
            for (int j = 0; j < h1; j++)
            {
                int row = j * m_Input;
                for (int k = 0; k < OptionEncoder.RequestKinds; k++)
                {
                    m_FoldContext[k * h1 + j] = m_W1[row + kindAt + k];
                }
                m_FoldContext[OptionEncoder.RequestKinds * h1 + j] = m_W1[row + rest];
                m_FoldContext[(OptionEncoder.RequestKinds + 1) * h1 + j] = m_W1[row + rest + 1];
            }
            // v = Wout^T (Wt a2 + bt) + wb = (Wout^T Wt) a2 + (Wout^T bt + wb):
            // the trunk's output layer has no ReLU.
            int ho = m_OptionHidden;
            m_FoldOut = new double[ho * m_H2];
            m_FoldOutBias = new double[ho];
            for (int h = 0; h < ho; h++)
            {
                double b = m_OutBias[h];
                for (int t = 0; t < m_Trunk; t++)
                {
                    b += m_Out[t * ho + h] * m_Bt[t];
                }
                m_FoldOutBias[h] = b;
                for (int k = 0; k < m_H2; k++)
                {
                    double sum = 0;
                    for (int t = 0; t < m_Trunk; t++)
                    {
                        sum += m_Out[t * ho + h] * m_Wt[t * m_H2 + k];
                    }
                    m_FoldOut[h * m_H2 + k] = sum;
                }
            }
            m_FoldBias = bias;
        }

        // W1 times the zone vector of one card row (zone, vocabulary row).
        private void FoldCard(int row)
        {
            int h1 = m_H1, zone = row / m_Vocab, from = row * m_Hidden, to = row * h1;
            for (int j = 0; j < h1; j++)
            {
                double sum = 0;
                int w = j * m_Input + zone * m_Hidden;
                for (int h = 0; h < m_Hidden; h++)
                {
                    sum += m_W1[w + h] * m_Templates[from + h];
                }
                m_FoldCards[to + j] = sum;
            }
            m_FoldCardFilled[row] = true;
        }

        // W1 times a source template's inputs (the requesting card).
        private void FoldSource(int v)
        {
            int h1 = m_H1, at = StateEncoder.ZoneCount * m_Hidden + StateEncoder.GlobalCount + OptionEncoder.RequestKinds;
            for (int j = 0; j < h1; j++)
            {
                double sum = 0;
                int w = j * m_Input + at;
                for (int f = 0; f < m_TemplateInputs; f++)
                {
                    sum += m_W1[w + f] * m_TemplateInputsTable[v * m_TemplateInputs + f];
                }
                m_FoldSources[v * h1 + j] = sum;
            }
            m_FoldSourceFilled[v] = true;
        }

        private int VocabIndex(int template, bool hidden)
        {
            if (hidden)
            {
                return 1;
            }
            if (template == 0)
            {
                return 0;
            }
            return m_Index.TryGetValue(template, out int v) ? v : 0;
        }
    }
}
