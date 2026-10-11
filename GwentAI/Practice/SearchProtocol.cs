using System;
using System.Collections.Generic;
using System.Linq;
using GwentAI.Learn;
using GwentAI.Search;
using GwentCore;
using GwentGameplay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GwentAI.Practice
{
    // The wire protocol between a search coordinator (WorkerSearch, in the
    // game's process) and search worker processes (SearchWorker, headless
    // hosts): one JSON object per line over TCP. The coordinator sends
    //
    //   setup     once per game and seat: the game (seed, starting player,
    //             both decks in deal order), the seat, its prior (the decks
    //             its belief expects), the belief and the search settings,
    //             whole, with their models as the files' contents;
    //   search    once per decision: the moves played so far, the open
    //             request's type, the steps already chosen in this response,
    //             the opponent cards the seat remembers, the worker's seed
    //             and its budget caps;
    //   stop      end the search and report;
    //   resume    a work-budget search only: go on past the checkpoint the
    //             worker reported.
    //
    // The worker answers a search with ready (the position reached, with its
    // fingerprint), progress lines while it searches (ranked root
    // statistics), and done (final roots with their children, and the
    // search's cost), or error. A work-budget search (SearchSettings.WorkBudgetMs)
    // sends checkpoint lines instead of progress: the root statistics at each
    // multiple of the checkpoint spacing, after which the worker waits for the
    // coordinator's resume or stop. Steps are named by their keys (GameAction),
    // which never hold card instance ids, so the coordinator maps the chosen
    // key to a step of its own game.
    //
    // An oracle search (SearchSettings.Oracle, a cheating reference) needs
    // no hidden cards on the wire: the replayed game holds the real ones
    // (same seed, decks in deal order and moves). Search and ready then also
    // carry the hidden state as text (Fingerprint.Hidden), and a worker
    // whose replay holds other hidden cards does not count. Without oracle
    // neither message carries it. A hand-oracle search (HandOracle) needs only
    // the hands on the wire for the same check (Fingerprint.Hidden).
    public class WorkerMessage
    {
        public const string Setup = "setup", Search = "search", Stop = "stop", Resume = "resume";
        public const string Ready = "ready", Progress = "progress", Checkpoint = "checkpoint", Done = "done", Error = "error";

        public string Type;
        // The search this message belongs to (search and its answers, stop).
        public int Id;
        // The setup a search refers to.
        public string Context;

        // setup
        public int Seed;
        public string Starting;
        public DeckSpec P1;
        public DeckSpec P2;
        public string Observer;
        // The decks the seat's belief expects (Belief's meta).
        public List<DeckSpec> Prior;
        // The seat's belief and search settings, every public field of
        // each: a field added to either travels with no change here
        // (WireFields and ModelConverter). The settings' models (round
        // evaluator, value and policy nets, mid-round evaluator) travel as
        // their files' contents, never as paths: the game may run under
        // Wine and the workers on Linux.
        public Belief Belief;
        public SearchSettings Parameters;

        // search
        public List<string> Moves;
        public string Request;
        public List<string> Prefix;
        // [instance id, template id] pairs (Belief.Remember).
        public List<int[]> Remembered;
        public int SearchSeed;
        // The worker's hard caps; the coordinator stops it earlier.
        public int Iterations;
        public int TimeBudgetMs;
        // A work-budget search: the checkpoint spacing in work units
        // (WorkCheckpoints.Spacing); 0 for a search without one.
        public long CheckpointUnits;
        // The second look's search (SecondLook): the root steps it is
        // restricted to. Its settings are the setup's played to the end of
        // the match (SearchSettings.WholeMatch). Null for a main search.
        public List<string> RootKeys;
        // The coordinator's fingerprint of the live position, for the
        // worker's log.
        public string Fingerprint;
        // Oracle and hand-oracle searches only (search and ready): the hidden
        // state (Fingerprint.Hidden); null otherwise.
        public string Hidden;

        // ready, progress, checkpoint, done, error
        public string Message;
        // checkpoint: its number (the first is 1), the worker's work so far.
        public int Index;
        public WorkTally Work;
        public double ReplayMs;
        public int Unfinished;
        public int Evaluated;
        public double CloneMs, DeterminizeMs, RunMs, DecideMs;
        public long Updates;
        public string Stall;
        public int ShownMismatches;
        public List<RootReport> Roots;

        private static readonly JsonSerializerSettings s_Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new ExactDoubleConverter(), new ModelConverter() },
        };

        public string Serialize()
        {
            return JsonConvert.SerializeObject(this, s_Json);
        }

        public static WorkerMessage Parse(string line)
        {
            return JsonConvert.DeserializeObject<WorkerMessage>(line, s_Json);
        }

        public static List<int[]> RememberedList(IDictionary<ushort, int> remembered)
        {
            return remembered == null ? null : remembered.Select(entry => new[] { (int)entry.Key, entry.Value }).ToList();
        }

        public static Dictionary<ushort, int> RememberedMap(List<int[]> remembered)
        {
            Dictionary<ushort, int> map = new Dictionary<ushort, int>();
            if (remembered != null)
            {
                foreach (int[] entry in remembered)
                {
                    map[(ushort)entry[0]] = entry[1];
                }
            }
            return map;
        }
    }

    // Doubles cross the wire as their IEEE 754 bits ("x" and 16 hex
    // digits), not as decimal text. A decimal text comes back exact only
    // through the parser that wrote it: Mono's double.Parse reads about 3 in
    // 10,000 shortest round-trip texts (.NET's format) 1 ulp off, and .NET
    // reads about 1 in 20,000 of Mono's "R" texts 1 ulp off (measured).
    // The game and its workers can run on different runtimes (Unity's Mono, Mono, .NET), and a summed reward 1 ulp
    // off can break a tie the other way. A plain JSON number is still read.
    public sealed class ExactDoubleConverter : JsonConverter
    {
        public override bool CanConvert(Type type)
        {
            return type == typeof(double);
        }

        public static string Encode(double value)
        {
            return "x" + BitConverter.DoubleToInt64Bits(value).ToString("x16");
        }

        public static double Decode(object value)
        {
            string text = value as string;
            if (text != null && text.Length == 17 && text[0] == 'x')
            {
                return BitConverter.Int64BitsToDouble(Convert.ToInt64(text.Substring(1), 16));
            }
            return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(Encode((double)value));
        }

        public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
        {
            return Decode(reader.Value);
        }
    }

    // The search settings' models cross the wire as the contents of the
    // files they were loaded from: the round evaluator (its JSON text, which
    // the worker parses as the game did), the nets (their bytes), the
    // mid-round evaluator (its JSON text), and a blend as its two parts and
    // its weight. Nothing but contents travels, so a worker needs no path
    // to a file and reads the same weights the game does. A model built
    // another way than from its file (no text or bytes to send) is refused
    // here, when the coordinator is built (WorkerSearch), not at the first
    // decision.
    public sealed class ModelConverter : JsonConverter
    {
        public override bool CanConvert(Type type)
        {
            return typeof(IRoundEvaluator).IsAssignableFrom(type) || type == typeof(MidEvaluator) || type == typeof(PolicyNet);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case Evaluator evaluator:
                    Text(writer, "evaluator", evaluator.Json, value);
                    break;
                case MidEvaluator mid:
                    Text(writer, "mid", mid.Json, value);
                    break;
                case ValueNet net:
                    Bytes(writer, "valuenet", net.Bytes, net.Name, value);
                    break;
                case PolicyNet policy:
                    Bytes(writer, "policynet", policy.Bytes, policy.Name, value);
                    break;
                case BlendEvaluator blend:
                    writer.WritePropertyName("Kind");
                    writer.WriteValue("blend");
                    writer.WritePropertyName("Weight");
                    writer.WriteValue(ExactDoubleConverter.Encode(blend.NetWeight));
                    writer.WritePropertyName("Linear");
                    WriteJson(writer, blend.Linear, serializer);
                    writer.WritePropertyName("Net");
                    WriteJson(writer, blend.Net, serializer);
                    break;
                case RoundGateEvaluator gate:
                    writer.WritePropertyName("Kind");
                    writer.WriteValue("roundgate");
                    writer.WritePropertyName("FromRound");
                    writer.WriteValue(gate.FromRound);
                    writer.WritePropertyName("Linear");
                    WriteJson(writer, gate.Linear, serializer);
                    writer.WritePropertyName("Net");
                    WriteJson(writer, gate.Net, serializer);
                    break;
                default:
                    throw new JsonSerializationException("Search workers cannot carry a " + value.GetType().Name + ": add it to ModelConverter");
            }
            writer.WriteEndObject();
        }

        private static void Text(JsonWriter writer, string kind, string text, object model)
        {
            if (text == null)
            {
                throw new JsonSerializationException("Search workers need the " + model.GetType().Name + "'s source text (build it with Parse or Load)");
            }
            writer.WritePropertyName("Kind");
            writer.WriteValue(kind);
            writer.WritePropertyName("Text");
            writer.WriteValue(text);
        }

        private static void Bytes(JsonWriter writer, string kind, byte[] bytes, string name, object model)
        {
            if (bytes == null)
            {
                throw new JsonSerializationException("Search workers need the " + model.GetType().Name + "'s file bytes (build it with Load or FromBytes)");
            }
            writer.WritePropertyName("Kind");
            writer.WriteValue(kind);
            if (name != null)
            {
                writer.WritePropertyName("Name");
                writer.WriteValue(name);
            }
            writer.WritePropertyName("Bytes");
            writer.WriteValue(Convert.ToBase64String(bytes));
        }

        public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
        {
            return Read(JObject.Load(reader));
        }

        private static object Read(JObject model)
        {
            string kind = (string)model["Kind"];
            switch (kind)
            {
                case "evaluator":
                    return Evaluator.Parse((string)model["Text"]);
                case "mid":
                    return MidEvaluator.Parse((string)model["Text"]);
                case "valuenet":
                    return ValueNet.FromBytes(Convert.FromBase64String((string)model["Bytes"]), (string)model["Name"]);
                case "policynet":
                    return PolicyNet.FromBytes(Convert.FromBase64String((string)model["Bytes"]), (string)model["Name"]);
                case "blend":
                    return new BlendEvaluator((IRoundEvaluator)Read((JObject)model["Linear"]), (IRoundEvaluator)Read((JObject)model["Net"]),
                                              ExactDoubleConverter.Decode(((JValue)model["Weight"]).Value));
                case "roundgate":
                    return new RoundGateEvaluator((IRoundEvaluator)Read((JObject)model["Linear"]), (IRoundEvaluator)Read((JObject)model["Net"]),
                                                  (int)model["FromRound"]);
                default:
                    throw new JsonSerializationException("Unknown model kind " + kind);
            }
        }
    }

    // Copies every public instance field of one object onto another of the
    // same type (a worker's belief takes the coordinator's fields this way:
    // a field added to Belief needs no list here).
    public static class WireFields
    {
        public static void Copy<T>(T from, T to)
        {
            foreach (System.Reflection.FieldInfo field in typeof(T).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                field.SetValue(to, field.GetValue(from));
            }
        }
    }

    // A root step's statistics on the wire (RootStat).
    public class RootReport
    {
        public string Key;
        public int Visits;
        public double Reward;
        public double Squares;
        public double Margin;
        public double MarginSquares;
        public int MarginCount;
        public List<RootReport> Children;

        public static RootReport Of(RootStat stat, bool withChildren)
        {
            return new RootReport
            {
                Key = stat.Key, Visits = stat.Visits, Reward = stat.Reward, Squares = stat.RewardSquares,
                Margin = stat.Margin, MarginSquares = stat.MarginSquares, MarginCount = stat.MarginCount,
                Children = withChildren && stat.Children != null ? stat.Children.Select(child => Of(child, false)).ToList() : null,
            };
        }

        public static List<RootReport> Of(IEnumerable<RootStat> stats, bool withChildren)
        {
            return stats.Select(stat => Of(stat, withChildren)).ToList();
        }

        public RootStat ToStat()
        {
            return new RootStat
            {
                Key = Key, Visits = Visits, Reward = Reward, RewardSquares = Squares, Margin = Margin, MarginSquares = MarginSquares, MarginCount = MarginCount,
            };
        }

        public static IEnumerable<RootStat> ToStats(IEnumerable<RootReport> reports)
        {
            return (reports ?? Enumerable.Empty<RootReport>()).Select(report => report.ToStat());
        }
    }

    // What a seat sees of a position when it decides, as text, so a worker's
    // replayed game can be checked against the live one before its search
    // counts: round, request, scores, hand and deck sizes, crowns, passes,
    // the seat's legal steps at this point of the response, and whether
    // actions are queued but not yet applied (a move the other seat just
    // made, which a replay of the applied moves cannot hold).
    public static class Fingerprint
    {
        public static string Of(GameController game, EPlayerId observer, ARequestAction request, ICollection<string> prefix, bool groupPlays)
        {
            Player me = game.PlayerManager.GetPlayer(observer);
            Player them = me.OpponentPlayer;
            IEnumerable<string> legal = Actions.Legal(request, me, prefix, groupPlays).Select(action => action.Key).OrderBy(key => key, StringComparer.Ordinal);
            return $"r{game.RoundManager.CurrentRoundIndex + 1} {request.GetRequestType()} pending {game.ActionManager.HasActions} " +
                   $"score {me.CurrentScore}-{them.CurrentScore} " +
                   $"hand {me.GetCardsInLocation(ELocation.Hand).Count}-{them.GetCardsInLocation(ELocation.Hand).Count} " +
                   $"deck {me.GetCardsInLocation(ELocation.Deck).Count}-{them.GetCardsInLocation(ELocation.Deck).Count} " +
                   $"crowns {me.Crowns}-{them.Crowns} passed {me.HasPassed}/{them.HasPassed} legal {string.Join("|", legal.ToArray())}";
        }

        // The text a search's worker must reproduce before it counts: the hidden
        // state of an oracle, the hands of a hand oracle, null otherwise.
        public static string Hidden(GameController game, SearchSettings settings)
        {
            return settings.Oracle ? Hidden(game) : settings.HandOracle ? Hands(game) : null;
        }

        // What a hand-oracle search sees beyond the seat's view, as text: both
        // hands (template ids).
        private static string Hands(GameController game)
        {
            IEnumerable<string> seats = new[] { EPlayerId.P1, EPlayerId.P2 }.Select(id =>
                $"{id} hand {Templates(game.PlayerManager.GetPlayer(id).GetCardsInLocation(ELocation.Hand))}");
            return string.Join(" | ", seats.ToArray());
        }

        // What an oracle search sees beyond the seat's view, as text: both
        // hands and both decks in order (template ids), and the random
        // generator (calls so far and its next draw, from a copy).
        public static string Hidden(GameController game)
        {
            MersenneTwisterRandom next = new MersenneTwisterRandom(0);
            next.Copy(game.RandomGenerator);
            IEnumerable<string> seats = new[] { EPlayerId.P1, EPlayerId.P2 }.Select(id =>
            {
                Player player = game.PlayerManager.GetPlayer(id);
                return $"{id} hand {Templates(player.GetCardsInLocation(ELocation.Hand))} deck {Templates(player.GetCardsInLocation(ELocation.Deck))}";
            });
            return string.Join(" | ", seats.ToArray()) + $" | rng {game.RandomGenerator.NumTimesCalledSinceCreation} {next.NextUnsigned()}";
        }

        private static string Templates(IEnumerable<Card> cards)
        {
            return string.Join(",", cards.Select(card => card.Definition.TemplateId.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        }
    }
}
