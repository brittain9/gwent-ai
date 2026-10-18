using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using GwentGameplay;

namespace GwentHost
{
    // Reference tables of the client's rules (its enums, its card power
    // arithmetic and its graph loader), read from the client's own
    // compiled code in this runtime:
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --self-test loader-dump [--out DIR]
    //   ... --self-test enum-dump [--out DIR]
    //   ... --self-test cardpower-dump [--out DIR]
    //
    // Each writes OUT/<name>-<runtime>.txt (OUT defaults to
    // .runtime/engine/oracle under the current directory; runtime is mono or
    // coreclr) and exits 0. The first lines start with "#": the runtime and
    // the build. Every other line is "\n"-terminated ASCII, numbers in
    // invariant decimal. Each dump's format is given above its code.
    internal static class OracleDumps
    {
        public static readonly string[] Names = { "loader-dump", "enum-dump", "cardpower-dump" };
        // The namespaces whose enums the dump lists.
        private static readonly string[] EnumNamespaces = { "GwentGameplay", "GwentGameplay.Settings", "GwentCore" };
        public const int ExpectedEnums = 74;

        public static int Run(string name, string client, string outDir)
        {
            outDir = outDir ?? Path.Combine(Path.Combine(".runtime", "engine"), "oracle");
            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir, $"{name}-{RuntimeName()}.txt");
            List<string> lines = new List<string> { "# " + name, "# runtime " + RuntimeDescription(), "# build " + BuildStamp() };
            int status;
            switch (name)
            {
                case "loader-dump":
                    status = LoaderDump(client, lines);
                    break;
                case "enum-dump":
                    status = EnumDump(lines);
                    break;
                case "cardpower-dump":
                    status = CardPowerDump(lines);
                    break;
                default:
                    throw new ArgumentException(name);
            }
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                foreach (string line in lines)
                {
                    writer.WriteLine(line);
                }
            }
            Console.WriteLine($"{name}: {lines.Count} lines to {path}");
            return status;
        }

        // --- enum-dump ---------------------------------------------------
        //
        // "enums <count>", then per enum (by full name, ordinal order):
        //   enum <full name> <flags|plain> <defined names>
        //   value <int> <name>            each defined name, in Enum.GetNames order
        //   fmt <int> <ToString()>        the formatting cases, by value
        // Formatting cases: plain enums: 0 and -1 when undefined, min - 1
        // and max + 1. [Flags] enums: 0, -1, every single bit 1 << k
        // (k = 0..31), every OR of two defined non-zero values, and every
        // defined non-zero value OR the lowest bit no defined value uses.
        private static int EnumDump(List<string> lines)
        {
            Assembly engine = typeof(GameController).Assembly;
            List<Type> enums = engine.GetTypes()
                .Where(type => type.IsEnum && !type.IsNested && EnumNamespaces.Contains(type.Namespace))
                .OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
            lines.Add("enums " + enums.Count);
            foreach (Type type in enums)
            {
                bool flags = type.IsDefined(typeof(FlagsAttribute), false);
                string[] names = Enum.GetNames(type);
                lines.Add($"enum {type.FullName} {(flags ? "flags" : "plain")} {names.Length}");
                List<int> defined = new List<int>();
                foreach (string valueName in names)
                {
                    int value = Convert.ToInt32(Enum.Parse(type, valueName), CultureInfo.InvariantCulture);
                    defined.Add(value);
                    lines.Add($"value {Int(value)} {valueName}");
                }
                HashSet<int> cases = new HashSet<int>();
                if (flags)
                {
                    cases.Add(0);
                    cases.Add(-1);
                    int used = 0;
                    for (int bit = 0; bit < 32; bit++)
                    {
                        cases.Add(1 << bit);
                    }
                    List<int> nonZero = defined.Where(value => value != 0).Distinct().ToList();
                    foreach (int value in nonZero)
                    {
                        used |= value;
                        foreach (int other in nonZero)
                        {
                            cases.Add(value | other);
                        }
                    }
                    int free = 0;
                    for (int bit = 0; bit < 32 && free == 0; bit++)
                    {
                        free = (used & (1 << bit)) == 0 ? 1 << bit : 0;
                    }
                    if (free != 0)
                    {
                        foreach (int value in nonZero)
                        {
                            cases.Add(value | free);
                        }
                    }
                }
                else
                {
                    foreach (int value in new[] { 0, -1 })
                    {
                        if (!defined.Contains(value))
                        {
                            cases.Add(value);
                        }
                    }
                    if (defined.Count > 0)
                    {
                        cases.Add(defined.Min() - 1);
                        cases.Add(defined.Max() + 1);
                    }
                }
                foreach (int value in cases.OrderBy(value => value))
                {
                    lines.Add($"fmt {Int(value)} {Enum.ToObject(type, value)}");
                }
            }
            if (enums.Count != ExpectedEnums)
            {
                Console.WriteLine($"enum-dump: {enums.Count} enums, expected {ExpectedEnums}");
                return 1;
            }
            return 0;
        }

        // --- cardpower-dump ----------------------------------------------
        //
        // CardPower.MultiplyValue(value, factor) (computed as
        // floor((float)v * ((float)f / 100f))), the client's own private
        // method, called on this runtime over values -10,000 to 10,000 and
        // factors -1,000 to 1,000:
        //   range <first value> <last value> <first factor> <last factor>
        //   M <factor> <mismatches> <fnv>   per factor: how many values differ
        //                                   from the exact floor(v * f / 100),
        //                                   and FNV-1a 64 (hex, 16 digits) over
        //                                   the results as int32 little-endian,
        //                                   values ascending
        //   R <factor> <results...>         the full row, for factors that are
        //                                   multiples of 25
        private const int FirstValue = -10000, LastValue = 10000, FirstFactor = -1000, LastFactor = 1000;

        private static int CardPowerDump(List<string> lines)
        {
            MethodInfo method = typeof(CardPower).GetMethod("MultiplyValue", BindingFlags.Instance | BindingFlags.NonPublic);
            object power = FormatterServices.GetUninitializedObject(typeof(CardPower));
            Func<int, int, int> multiply = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>), power, method);
            lines.Add($"range {FirstValue} {LastValue} {FirstFactor} {LastFactor}");
            int multiplesOf25Mismatched = 0;
            StringBuilder row = new StringBuilder();
            for (int factor = FirstFactor; factor <= LastFactor; factor++)
            {
                ulong hash = 14695981039346656037UL;
                int mismatches = 0;
                bool full = factor % 25 == 0;
                row.Length = 0;
                if (full)
                {
                    row.Append("R ").Append(Int(factor));
                }
                for (int value = FirstValue; value <= LastValue; value++)
                {
                    int result = multiply(value, factor);
                    mismatches += result == FloorDiv((long)value * factor, 100) ? 0 : 1;
                    for (int b = 0; b < 4; b++)
                    {
                        hash = (hash ^ (byte)(result >> (8 * b))) * 1099511628211UL;
                    }
                    if (full)
                    {
                        row.Append(' ').Append(Int(result));
                    }
                }
                lines.Add($"M {Int(factor)} {Int(mismatches)} {hash:x16}");
                if (full)
                {
                    lines.Add(row.ToString());
                    multiplesOf25Mismatched += mismatches > 0 ? 1 : 0;
                }
            }
            // The claim checked: every multiple of 25 is the exact rational.
            Console.WriteLine($"cardpower-dump: {multiplesOf25Mismatched} factors that are multiples of 25 differ from the exact floor");
            return multiplesOf25Mismatched == 0 ? 0 : 1;
        }

        private static long FloorDiv(long a, long b)
        {
            long q = a / b;
            return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
        }

        // --- loader-dump -------------------------------------------------
        //
        // Part 1, per type the loader can build (every concrete
        // IPersistentData type of the engine assembly, by full name), the
        // reflection order PersistentDataHelper.GetPersistentDataInfo gives
        // (base class fields first, then GetFields(DeclaredOnly)):
        //   type <full name>
        //   persist <key> <declaring type>.<field>   PersistentFields, enumeration order
        //   in <declaring type>.<field> <field type>  InputFields, in order
        //   out <declaring type>.<field> <field type> OutputFields, in order
        // Part 2, every loaded graph (card templates by id, then the location
        // token abilities by index), every flow node in Nodes order:
        //   graph template <id> | graph token <index> <token>
        //   node <node id> <type> in <fields> out <fields>
        // where <fields> are the field names of the node's Inputs and Outputs
        // arrays as the loader filled them, joined by ",", "-" when empty, an
        // array entry no field holds as "?<AVar type>", and an array not built
        // as "null".
        private static int LoaderDump(string client, List<string> lines)
        {
            GameSharedData shared = Host.Engine(client, out HarmonyLib.Harmony harmony, out RedLogger.IRedLogger logger);
            Assembly engine = typeof(GameController).Assembly;
            List<Type> types = engine.GetTypes()
                .Where(type => !type.IsAbstract && !type.IsInterface && !type.ContainsGenericParameters && PersistentDataHelper.Type.IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();
            lines.Add("types " + types.Count);
            foreach (Type type in types)
            {
                PersistentDataInfo info = PersistentDataHelper.GetPersistentDataInfo(type);
                lines.Add("type " + type.FullName);
                foreach (KeyValuePair<string, KeyValuePair<PersistentAttribute, FieldInfo>> field in info.PersistentFields)
                {
                    lines.Add($"persist {field.Key} {Field(field.Value.Value)}");
                }
                foreach (KeyValuePair<InputAttribute, FieldInfo> field in info.InputFields)
                {
                    lines.Add($"in {Field(field.Value)} {TypeName(field.Value.FieldType)}");
                }
                foreach (KeyValuePair<OutputAttribute, FieldInfo> field in info.OutputFields)
                {
                    lines.Add($"out {Field(field.Value)} {TypeName(field.Value.FieldType)}");
                }
            }
            int graphs = 0;
            foreach (KeyValuePair<int, RuntimeCardTemplate> template in shared.SharedRuntimeTemplates.OrderBy(entry => entry.Key))
            {
                if (template.Value.Ability != null)
                {
                    lines.Add("graph template " + Int(template.Key));
                    Graph(template.Value.Ability, lines);
                    graphs++;
                }
            }
            LocationTokenAbility[] tokens = shared.LocationTokenAbilities ?? new LocationTokenAbility[0];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] != null)
                {
                    lines.Add($"graph token {Int(i)} {Int((int)tokens[i].LocationToken)}");
                    Graph(tokens[i], lines);
                    graphs++;
                }
            }
            Console.WriteLine($"loader-dump: {types.Count} types, {graphs} graphs");
            return 0;
        }

        private static void Graph(AAbility ability, List<string> lines)
        {
            foreach (AAbilityNode node in ability.Nodes)
            {
                if (node is AFlowNode flow)
                {
                    lines.Add($"node {Int(node.Id)} {node.GetType().Name} in {Vars(flow, flow.Inputs)} out {Vars(flow, flow.Outputs)}");
                }
            }
        }

        private static string Vars(AFlowNode node, AVar[] vars)
        {
            if (vars == null)
            {
                return "null";
            }
            if (vars.Length == 0)
            {
                return "-";
            }
            List<FieldInfo> fields = new List<FieldInfo>();
            for (Type type = node.GetType(); type != null; type = type.BaseType)
            {
                fields.AddRange(type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
            }
            return string.Join(",", vars.Select(var =>
            {
                FieldInfo field = fields.FirstOrDefault(candidate => !candidate.FieldType.IsValueType && ReferenceEquals(candidate.GetValue(node), var));
                return field == null ? "?" + (var == null ? "null" : var.GetType().Name) : field.Name;
            }).ToArray());
        }

        private static string Field(FieldInfo field)
        {
            return field.DeclaringType.Name + "." + field.Name;
        }

        // A field type as C# names it, generic arguments included.
        private static string TypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }
            string name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName).ToArray()) + ">";
        }

        // --- the header --------------------------------------------------

        public static string RuntimeName()
        {
#if NETCOREAPP
            return "coreclr";
#else
            return "mono";
#endif
        }

        private static string RuntimeDescription()
        {
#if NETCOREAPP
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + "; float steps single precision";
#else
            Type mono = Type.GetType("Mono.Runtime");
            MethodInfo display = mono?.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
            return "Mono " + (display?.Invoke(null, null) ?? Environment.Version.ToString()) + "; -O=-float32 (double-precision float steps, as Unity's Mono)";
#endif
        }

        // The build stamp beside the host (dist/host/build.json), else "unknown".
        private static string BuildStamp()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(OracleDumps).Assembly.Location) ?? ".", "build.json");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "unknown";
        }

        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
