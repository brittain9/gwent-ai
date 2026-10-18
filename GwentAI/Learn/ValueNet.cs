using System;
using System.Collections.Generic;
using System.IO;
using GwentAI.Search;
using GwentGameplay;

namespace GwentAI.Learn
{
    // A learned value: the chance that a seat wins the match at a round
    // start. Two kinds of file:
    //
    // - File version 1 (fitted offline): one net on the seat's view
    //   (StateEncoder), with two auxiliary heads (the chance to win the
    //   current round, the round's final point margin in tens).
    // - File version 2 (fitted offline): a full-state ensemble.
    //   Each member reads the full-state view of the determinized game (the
    //   opponent's whole hand listed, StateEncoder.Encode with fullState)
    //   and its mirror; a member's chance is logistic(g(view) - g(mirror)),
    //   antisymmetric by construction. The value is the members' mean minus
    //   Pessimism times their SD (a cautious value where they disagree),
    //   clipped to [0, 1]. Mode 1 scores
    //   round starts (the search's round horizon); mode 2 scores turn
    //   starts inside a round (a round-3 rollout cut after a few turns).
    //
    // A member: every card adds a vector to its zone's sum. The per-card
    // map is linear, so it splits into a template part, precomputed here per
    // zone and template, and a part linear in the card's dynamic features,
    // which are summed per zone first and multiplied once. The 13 zone sums
    // and the scaled globals feed an MLP trunk (two ReLU layers) and the
    // heads. Weights are float32, sums are double; one instance is not
    // thread-safe (it reuses its buffers: no allocation per call).
    //
    // The file the AI embeds has the static columns of its template table
    // (the client's card stats) zeroed; Complete fills them when the net is
    // loaded for play.
    public sealed class ValueNet : IRoundEvaluator
    {
        public const int Magic = ValueModel.Magic; // "GVN1"
        public const int FileVersion = ValueModel.FileVersion;
        public const int FullStateVersion = ValueModel.FullStateVersion;
        // Dynamic features per card: power, base power, armour (in tens),
        // 13 ECardToken bits, position in the row (in tens).
        public const int DynamicCount = 17;
        public const int TokenBits = 13;
        public const int Outputs = 3;

        private readonly Member[] m_Members;
        private readonly EncodedState m_State = new EncodedState();
        private readonly EncodedState m_Mirror = new EncodedState();
        private readonly double[] m_Out = new double[Outputs];
        private readonly double[] m_Chances;

        // True for a full-state ensemble (file version 2).
        public readonly bool FullState;
        // A full-state file of mode 2: it scores turn starts, not round starts.
        public readonly bool TurnStart;
        // k in mean - k SD (full-state files).
        public readonly double Pessimism;

        public string Name;
        // Calls and time spent (encoding and forward pass), for timing reports.
        public long Calls;
        public long Ticks;

        private ValueNet(BinaryReader reader)
        {
            if (reader.ReadInt32() != Magic)
            {
                throw new InvalidDataException("Not a value net file");
            }
            int fileVersion = reader.ReadInt32();
            if (fileVersion == FileVersion)
            {
                m_Members = new[] { new Member(reader) };
            }
            else if (fileVersion == FullStateVersion)
            {
                CheckEncoding(fileVersion, reader.ReadInt32());
                int mode = reader.ReadInt32();
                int count = reader.ReadInt32();
                if ((mode != 1 && mode != 2) || count < 1)
                {
                    throw new InvalidDataException($"Full-state value net: mode {mode}, {count} members");
                }
                FullState = true;
                TurnStart = mode == 2;
                Pessimism = reader.ReadSingle();
                m_Members = new Member[count];
                for (int i = 0; i < count; i++)
                {
                    int length = reader.ReadInt32();
                    using (BinaryReader member = new BinaryReader(new MemoryStream(reader.ReadBytes(length))))
                    {
                        if (member.ReadInt32() != Magic || member.ReadInt32() != FileVersion)
                        {
                            throw new InvalidDataException("Full-state value net: a member is not a version 1 net");
                        }
                        m_Members[i] = new Member(member);
                    }
                }
            }
            else
            {
                throw new InvalidDataException($"Value net file version {fileVersion}; this build reads {FileVersion} and {FullStateVersion}");
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                throw new InvalidDataException("Value net file has trailing data");
            }
            m_Chances = new double[m_Members.Length];
        }

        // A net on the weights of another, which stay shared (nothing writes
        // them after the constructors): its own buffers and counters.
        private ValueNet(ValueNet weights)
        {
            m_Members = new Member[weights.m_Members.Length];
            for (int i = 0; i < m_Members.Length; i++)
            {
                m_Members[i] = weights.m_Members[i].Fresh();
            }
            m_Chances = new double[m_Members.Length];
            FullState = weights.FullState;
            TurnStart = weights.TurnStart;
            Pessimism = weights.Pessimism;
            Name = weights.Name;
            Bytes = weights.Bytes;
        }

        // A new net of the same weights with its own buffers and counters.
        public ValueNet Fresh(string name)
        {
            return new ValueNet(this) { Name = name };
        }

        private static void CheckEncoding(int fileVersion, int encoding)
        {
            if (encoding != StateEncoder.Version)
            {
                throw new InvalidDataException($"Value net file version {fileVersion}, encoding {encoding}; this build reads encoding {StateEncoder.Version}");
            }
        }

        // The weights are parsed once a process (NetCache): every call after
        // the first for a file builds only the buffers.
        public static ValueNet Load(string path)
        {
            return s_Cache.FromFile(path).Fresh(Path.GetFileNameWithoutExtension(path));
        }

        // A net from the bytes of its .vnet file, which it keeps (Bytes):
        // search workers receive the coordinator's net this way.
        public static ValueNet FromBytes(byte[] bytes, string name)
        {
            return s_Cache.FromBytes(bytes).Fresh(name);
        }

        private static readonly NetCache<ValueNet> s_Cache = new NetCache<ValueNet>(bytes =>
        {
            using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
            {
                return new ValueNet(reader) { Bytes = bytes };
            }
        });

        // The .vnet file this net was read from.
        public byte[] Bytes { get; private set; }

        // A copy of a .vnet file's bytes with every template row's static
        // columns written from the client's card table (ValueModel): the AI
        // embeds the file with them zeroed, and the filled bytes equal the
        // fitted file's. A file that has them already is returned unchanged
        // (the same values are written over them). Throws when the file names
        // a card the client does not have.
        public static byte[] Complete(byte[] stripped, GameSharedData shared)
        {
            if (shared == null)
            {
                throw new ArgumentNullException(nameof(shared), "The value net's static columns come from the client's shared game data, which is not loaded yet");
            }
            return ValueModel.Complete(stripped, id =>
            {
                RuntimeCardTemplate runtime;
                return shared.SharedRuntimeTemplates.TryGetValue(id, out runtime) ? PolicyModel.Columns(runtime.Template) : null;
            });
        }

        // P(seat wins the match) at a round start; in a finished game, the
        // result (as Evaluator).
        public double WinProbability(GameController game, EPlayerId seat, IDictionary<ushort, int> remembered)
        {
            if (TurnStart)
            {
                throw new InvalidOperationException($"Value net {Name} scores turn starts (mode 2), not round starts");
            }
            Player player = game.PlayerManager.GetPlayer(seat);
            int mine = player.Crowns;
            int theirs = player.OpponentPlayer.Crowns;
            if (mine >= 2 || theirs >= 2)
            {
                return mine == theirs ? 0.5 : mine > theirs ? 1 : 0;
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            StateEncoder.Encode(game, seat, remembered, true, m_State, FullState);
            double p = Evaluate(m_State)[0];
            Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            Calls++;
            return p;
        }

        // A turn-start file: P(seat wins the match) at a turn start inside a
        // round, from the full-state view (a cut rollout's leaf); in a
        // finished game, the result.
        public double TurnWinProbability(GameController game, EPlayerId seat)
        {
            Player player = game.PlayerManager.GetPlayer(seat);
            int mine = player.Crowns;
            int theirs = player.OpponentPlayer.Crowns;
            if (mine >= 2 || theirs >= 2)
            {
                return mine == theirs ? 0.5 : mine > theirs ? 1 : 0;
            }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            StateEncoder.Encode(game, seat, null, false, m_State, true);
            double p = Evaluate(m_State)[0];
            Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            Calls++;
            return p;
        }

        // A version 1 net: its heads for a seat view (P(win match), P(win
        // this round), round margin in tens). A full-state ensemble: its
        // value for a full-state view, alone in the array. The array is
        // reused by the next call.
        public double[] Evaluate(EncodedState state)
        {
            if (!FullState)
            {
                double[] raw = m_Members[0].Forward(state);
                m_Out[0] = Logistic(raw[0]);
                m_Out[1] = Logistic(raw[1]);
                m_Out[2] = raw[2];
                return m_Out;
            }
            StateEncoder.Mirror(state, m_Mirror);
            double sum = 0;
            for (int i = 0; i < m_Members.Length; i++)
            {
                double view = m_Members[i].Forward(state)[0];
                double mirror = m_Members[i].Forward(m_Mirror)[0];
                m_Chances[i] = Logistic(view - mirror);
                sum += m_Chances[i];
            }
            double mean = sum / m_Members.Length;
            double squares = 0;
            for (int i = 0; i < m_Members.Length; i++)
            {
                squares += (m_Chances[i] - mean) * (m_Chances[i] - mean);
            }
            double value = mean - Pessimism * Math.Sqrt(squares / m_Members.Length);
            m_Out[0] = Math.Min(Math.Max(value, 0), 1);
            m_Out[1] = 0;
            m_Out[2] = 0;
            return m_Out;
        }

        private static double Logistic(double x)
        {
            return 1 / (1 + Math.Exp(-x));
        }

        // One net of file version 1 (after its magic and version).
        private sealed class Member
        {
            private readonly int m_Vocab, m_TemplateInputs, m_Hidden, m_H1, m_H2, m_Input;
            private readonly Dictionary<int, int> m_Index = new Dictionary<int, int>();
            // Per zone and vocabulary index: the template part of the card map
            // plus the zone's bias, [Z][V][H].
            private readonly double[] m_Templates;
            // Weights are stored as float32 and widened once here: Mono
            // converts a float operand on every multiply otherwise.
            private readonly double[] m_Dynamic;    // [Z][D][H]
            private readonly double[] m_GlobalMean, m_GlobalScale;
            private readonly double[] m_W1, m_B1, m_W2, m_B2, m_WOut, m_BOut;
            private readonly double[] m_Zones, m_DynamicSums, m_X, m_A1, m_A2;
            private readonly double[] m_Out = new double[Outputs];

            internal Member(BinaryReader reader)
            {
                CheckEncoding(FileVersion, reader.ReadInt32());
                m_Vocab = reader.ReadInt32();
                int zones = reader.ReadInt32();
                m_TemplateInputs = reader.ReadInt32();
                m_Hidden = reader.ReadInt32();
                int dynamic = reader.ReadInt32();
                int globals = reader.ReadInt32();
                m_H1 = reader.ReadInt32();
                m_H2 = reader.ReadInt32();
                int outputs = reader.ReadInt32();
                if (zones != StateEncoder.ZoneCount || dynamic != DynamicCount || globals != StateEncoder.GlobalCount || outputs != Outputs)
                {
                    throw new InvalidDataException($"Value net shapes: {zones} zones, {dynamic} dynamic, {globals} globals, {outputs} outputs");
                }
                // Index 0: a template outside the vocabulary; 1: a hidden card.
                for (int v = 0; v < m_Vocab; v++)
                {
                    int template = reader.ReadInt32();
                    if (v >= 2)
                    {
                        m_Index[template] = v;
                    }
                }
                float[] templateInputs = Read(reader, m_Vocab * m_TemplateInputs);
                float[] projections = Read(reader, zones * m_TemplateInputs * m_Hidden);
                float[] zoneBias = Read(reader, zones * m_Hidden);
                m_Dynamic = Widen(Read(reader, zones * DynamicCount * m_Hidden));
                m_GlobalMean = Widen(Read(reader, globals));
                m_GlobalScale = Widen(Read(reader, globals));
                m_Input = zones * m_Hidden + globals;
                m_W1 = Widen(Read(reader, m_H1 * m_Input));
                m_B1 = Widen(Read(reader, m_H1));
                m_W2 = Widen(Read(reader, m_H2 * m_H1));
                m_B2 = Widen(Read(reader, m_H2));
                m_WOut = Widen(Read(reader, Outputs * m_H2));
                m_BOut = Widen(Read(reader, Outputs));

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
                m_Zones = new double[zones * m_Hidden];
                m_DynamicSums = new double[zones * DynamicCount];
                m_X = new double[m_Input];
                m_A1 = new double[m_H1];
                m_A2 = new double[m_H2];
            }

            // A member on the weights of another, which stay shared: its own buffers.
            private Member(Member weights)
            {
                m_Vocab = weights.m_Vocab;
                m_TemplateInputs = weights.m_TemplateInputs;
                m_Hidden = weights.m_Hidden;
                m_H1 = weights.m_H1;
                m_H2 = weights.m_H2;
                m_Input = weights.m_Input;
                m_Index = weights.m_Index;
                m_Templates = weights.m_Templates;
                m_Dynamic = weights.m_Dynamic;
                m_GlobalMean = weights.m_GlobalMean;
                m_GlobalScale = weights.m_GlobalScale;
                m_W1 = weights.m_W1;
                m_B1 = weights.m_B1;
                m_W2 = weights.m_W2;
                m_B2 = weights.m_B2;
                m_WOut = weights.m_WOut;
                m_BOut = weights.m_BOut;
                m_Zones = new double[weights.m_Zones.Length];
                m_DynamicSums = new double[weights.m_DynamicSums.Length];
                m_X = new double[m_Input];
                m_A1 = new double[m_H1];
                m_A2 = new double[m_H2];
            }

            internal Member Fresh()
            {
                return new Member(this);
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

            // The three heads before the logistic; the array is reused.
            internal double[] Forward(EncodedState state)
            {
                int zones = StateEncoder.ZoneCount;
                Array.Clear(m_Zones, 0, m_Zones.Length);
                Array.Clear(m_DynamicSums, 0, m_DynamicSums.Length);
                int[] cards = state.Cards;
                for (int c = 0; c < state.CardCount; c++)
                {
                    int at = c * StateEncoder.CardFields;
                    int zone = cards[at];
                    int template = cards[at + 1];
                    int v;
                    if (template == 0)
                    {
                        v = 1;
                    }
                    else if (!m_Index.TryGetValue(template, out v))
                    {
                        v = 0;
                    }
                    int from = (zone * m_Vocab + v) * m_Hidden;
                    int to = zone * m_Hidden;
                    for (int h = 0; h < m_Hidden; h++)
                    {
                        m_Zones[to + h] += m_Templates[from + h];
                    }
                    int d = zone * DynamicCount;
                    m_DynamicSums[d] += cards[at + 2] / 10.0;
                    m_DynamicSums[d + 1] += cards[at + 3] / 10.0;
                    m_DynamicSums[d + 2] += cards[at + 4] / 10.0;
                    int tokens = cards[at + 5];
                    for (int bit = 0; bit < TokenBits; bit++)
                    {
                        m_DynamicSums[d + 3 + bit] += (tokens >> bit) & 1;
                    }
                    m_DynamicSums[d + 3 + TokenBits] += cards[at + 6] / 10.0;
                }
                for (int z = 0; z < zones; z++)
                {
                    for (int k = 0; k < DynamicCount; k++)
                    {
                        double sum = m_DynamicSums[z * DynamicCount + k];
                        if (sum == 0)
                        {
                            continue;
                        }
                        int w = (z * DynamicCount + k) * m_Hidden;
                        int to = z * m_Hidden;
                        for (int h = 0; h < m_Hidden; h++)
                        {
                            m_Zones[to + h] += sum * m_Dynamic[w + h];
                        }
                    }
                }
                Array.Copy(m_Zones, m_X, m_Zones.Length);
                int[] globals = state.Globals;
                for (int i = 0; i < globals.Length; i++)
                {
                    m_X[m_Zones.Length + i] = (globals[i] - m_GlobalMean[i]) * m_GlobalScale[i];
                }
                Dense(m_W1, m_B1, m_X, m_A1, true);
                Dense(m_W2, m_B2, m_A1, m_A2, true);
                Dense(m_WOut, m_BOut, m_A2, m_Out, false);
                return m_Out;
            }
        }

        // y = W x + b, W row-major [y][x]; ReLU when asked.
        internal static void Dense(double[] weights, double[] bias, double[] x, double[] y, bool relu)
        {
            int n = x.Length;
            int blocked = n & ~3;
            for (int o = 0; o < y.Length; o++)
            {
                // Four partial sums: Mono's JIT keeps them in registers and
                // the multiplies overlap. The order of additions is fixed,
                // so results do not depend on the machine.
                double s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int row = o * n;
                int i = 0;
                for (; i < blocked; i += 4)
                {
                    int w = row + i;
                    s0 += weights[w] * x[i];
                    s1 += weights[w + 1] * x[i + 1];
                    s2 += weights[w + 2] * x[i + 2];
                    s3 += weights[w + 3] * x[i + 3];
                }
                for (; i < n; i++)
                {
                    s0 += weights[row + i] * x[i];
                }
                double sum = bias[o] + ((s0 + s1) + (s2 + s3));
                y[o] = relu && sum < 0 ? 0 : sum;
            }
        }
    }
}
