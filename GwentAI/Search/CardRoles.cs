using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GwentAI.Search
{
    // The card roles of data/cards/roles.json (built by scripts/card_roles.py),
    // embedded in the mod as cards/roles.json and
    // read once: roles, answer kinds, exposure tags and graveyard reach by
    // template id.
    public static class CardRoles
    {
        private static Data s_Data;

        public static bool Has(int template, string role)
        {
            return Loaded.Roles.TryGetValue(template, out HashSet<string> roles) && roles.Contains(role);
        }

        // Which side the card's chosen target favours, from its payoffs: -1
        // when it only hurts a unit it may aim at either side ("damage_any",
        // "destroy_any": the opponent's side), +1 when it only helps one
        // ("boost_any", "armor_any": its own side), 0 otherwise (no such
        // payoff, or a modal card with both).
        public static int Polarity(int template)
        {
            if (!Loaded.Payoffs.TryGetValue(template, out HashSet<string> payoffs))
            {
                return 0;
            }
            bool hurts = payoffs.Contains("damage_any") || payoffs.Contains("destroy_any");
            bool helps = payoffs.Contains("boost_any") || payoffs.Contains("armor_any");
            return hurts == helps ? 0 : hurts ? -1 : 1;
        }

        // What the card does against opponent units (empty when nothing).
        public static List<AnswerKind> AnswerKinds(int template)
        {
            return Get(Loaded.Answers, template);
        }

        // What the card is exposed to (or protected by) on its own board.
        public static List<ExposureTag> Exposure(int template)
        {
            return Get(Loaded.Exposure, template);
        }

        // What the card can bring back from a graveyard.
        public static List<ReviveReach> ReviveReach(int template)
        {
            return Get(Loaded.Reach, template);
        }

        // Revive cards (leaders included) that can play this answer again
        // from a graveyard, and which graveyard they read.
        public static List<ReplaySource> ReplayableBy(int template)
        {
            return Get(Loaded.Replays, template);
        }

        // Every distinct answer in the card pool, by kind (for worst cases).
        public static Dictionary<string, List<AnswerKind>> AnswersByKind => Loaded.ByKind;

        // Row token name -> what it does to its row's units each turn.
        public static Dictionary<string, List<TokenEffect>> WeatherTokens => Loaded.Weather;

        public static Data Loaded => s_Data ?? (s_Data = Load());

        private static List<T> Get<T>(Dictionary<int, List<T>> map, int template)
        {
            return map.TryGetValue(template, out List<T> list) ? list : new List<T>();
        }

        private static Data Load()
        {
            return ParseAll(Resource("cards/roles.json"), Resource("cards/answer-params.json"));
        }

        // An embedded resource, or null. cards/answer-params.json holds the
        // numeric parameters (amounts, thresholds, counts): data from the
        // user's client; the build embeds it from .local when it exists
        // (scripts/answer_kinds.py writes it), and without it those values read -1.
        private static string Resource(string name)
        {
            using (Stream stream = typeof(CardRoles).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    return null;
                }
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static Dictionary<int, HashSet<string>> Parse(string json)
        {
            return ParseAll(json).Roles;
        }

        public static Data ParseAll(string json, string numbers = null)
        {
            JObject root = JObject.Parse(json);
            JObject local = numbers == null ? null : JObject.Parse(numbers);
            // The numbers belong to one roles.json (scripts/answer_kinds.py ties
            // them by hash); numbers for another one are not joined.
            if (local != null && (string)local["roles_sha256"] == Sha256(json))
            {
                Join(root, local);
            }
            Data data = new Data();
            HashSet<string> seen = new HashSet<string>();
            foreach (JProperty card in ((JObject)root["cards"]).Properties())
            {
                JToken value = card.Value;
                int id = (int)value["id"];
                HashSet<string> set = new HashSet<string>();
                foreach (JToken role in value["roles"])
                {
                    set.Add((string)role);
                }
                data.Roles[id] = set;
                HashSet<string> payoffs = new HashSet<string>();
                foreach (JToken payoff in Items(value["payoffs"]))
                {
                    payoffs.Add((string)payoff);
                }
                data.Payoffs[id] = payoffs;
                List<AnswerKind> answers = new List<AnswerKind>();
                foreach (JToken entry in Items(value["answer_kinds"]))
                {
                    AnswerKind answer = AnswerKind.Parse(entry);
                    answers.Add(answer);
                    if (seen.Add(answer.Kind + entry.ToString(Newtonsoft.Json.Formatting.None).Replace("\"via\":" + answer.Via, "")))
                    {
                        if (!data.ByKind.TryGetValue(answer.Kind, out List<AnswerKind> list))
                        {
                            data.ByKind[answer.Kind] = list = new List<AnswerKind>();
                        }
                        list.Add(answer);
                    }
                }
                if (answers.Count > 0)
                {
                    data.Answers[id] = answers;
                }
                List<ExposureTag> tags = new List<ExposureTag>();
                foreach (JToken entry in Items(value["exposure"]))
                {
                    tags.Add(ExposureTag.Parse(entry));
                }
                if (tags.Count > 0)
                {
                    data.Exposure[id] = tags;
                }
                List<ReviveReach> reach = new List<ReviveReach>();
                foreach (JToken entry in Items(value["revive_reach"]))
                {
                    reach.Add(Search.ReviveReach.Parse(entry));
                }
                if (reach.Count > 0)
                {
                    data.Reach[id] = reach;
                }
                List<ReplaySource> replays = new List<ReplaySource>();
                foreach (JToken entry in Items(value["replayable_by"]))
                {
                    replays.Add(new ReplaySource { Id = (int)entry["id"], From = (string)entry["from"], To = (string)entry["to"] });
                }
                if (replays.Count > 0)
                {
                    data.Replays[id] = replays;
                }
            }
            if (root["weather_tokens"] is JObject weather)
            {
                foreach (JProperty token in weather.Properties())
                {
                    List<TokenEffect> effects = new List<TokenEffect>();
                    foreach (JToken entry in token.Value)
                    {
                        effects.Add(TokenEffect.Parse(entry));
                    }
                    data.Weather[token.Name] = effects;
                }
            }
            return data;
        }

        private static string Sha256(string text)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(new System.Text.UTF8Encoding(false).GetBytes(text));
                System.Text.StringBuilder hex = new System.Text.StringBuilder();
                foreach (byte b in hash)
                {
                    hex.Append(b.ToString("x2"));
                }
                return hex.ToString();
            }
        }

        // Merges the local numbers into roles.json's lists, index by index
        // (scripts/answer_kinds.py split/join).
        private static void Join(JObject root, JObject numbers)
        {
            if (numbers["cards"] is JObject cards)
            {
                foreach (JProperty card in cards.Properties())
                {
                    if (!(root["cards"]?[card.Name] is JObject target))
                    {
                        continue;
                    }
                    foreach (JProperty field in ((JObject)card.Value).Properties())
                    {
                        JoinList(target[field.Name] as JArray, field.Value as JArray);
                    }
                }
            }
            if (numbers["weather_tokens"] is JObject weather && root["weather_tokens"] is JObject tokens)
            {
                foreach (JProperty token in weather.Properties())
                {
                    JoinList(tokens[token.Name] as JArray, token.Value as JArray);
                }
            }
        }

        private static void JoinList(JArray entries, JArray values)
        {
            if (entries == null || values == null)
            {
                return;
            }
            for (int i = 0; i < entries.Count && i < values.Count; i++)
            {
                if (entries[i] is JObject entry && values[i] is JObject extra)
                {
                    foreach (JProperty p in extra.Properties())
                    {
                        entry[p.Name] = p.Value;
                    }
                }
            }
        }

        private static IEnumerable<JToken> Items(JToken token)
        {
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    yield return item;
                }
            }
        }

        internal static int Int(JToken token, int otherwise)
        {
            return token != null && token.Type == JTokenType.Integer ? (int)token : otherwise;
        }

        internal static int Mask(JToken token, string[] names)
        {
            int mask = 0;
            foreach (JToken item in Items(token))
            {
                int index = System.Array.IndexOf(names, (string)item);
                if (index >= 0)
                {
                    mask |= 1 << index;
                }
            }
            return mask;
        }

        public sealed class Data
        {
            public readonly Dictionary<int, HashSet<string>> Roles = new Dictionary<int, HashSet<string>>();
            public readonly Dictionary<int, HashSet<string>> Payoffs = new Dictionary<int, HashSet<string>>();
            public readonly Dictionary<int, List<AnswerKind>> Answers = new Dictionary<int, List<AnswerKind>>();
            public readonly Dictionary<int, List<ExposureTag>> Exposure = new Dictionary<int, List<ExposureTag>>();
            public readonly Dictionary<int, List<ReviveReach>> Reach = new Dictionary<int, List<ReviveReach>>();
            public readonly Dictionary<int, List<ReplaySource>> Replays = new Dictionary<int, List<ReplaySource>>();
            public readonly Dictionary<string, List<AnswerKind>> ByKind = new Dictionary<string, List<AnswerKind>>();
            public readonly Dictionary<string, List<TokenEffect>> Weather = new Dictionary<string, List<TokenEffect>>();
        }
    }

    // One answer of roles.json's answer_kinds. Unknown or run-time values are -1.
    public sealed class AnswerKind
    {
        // Tier bits as in the engine's ECardTier: leader 1, bronze 2, silver 4, gold 8.
        public static readonly string[] TierNames = { "leader", "bronze", "silver", "gold" };

        public string Kind;
        public string Scope; // board | opponent
        public string Select; // chosen, adjacent, highest, lowest, random, first_n, row, all, event
        public bool TiesAll; // every tied unit (HighestLevel / LowestLevel)
        public int Levels = 1;
        public int Count = 1; // targets chosen; -1 when set at run time
        public int Amount = -1; // damage, set value, or percent; -1 when dynamic
        public int Hits = 1; // repeats of the effect in a fixed loop
        public bool Row; // the targets are one row
        public bool EqualPower; // also every other unit with the target's power (Ballista)
        public int RowScoreMin = -1; // the row must total at least this (Igni); -1 without the local numbers
        public string PowerOp; // "<=", ">=", ... on the target's power, or null
        public int PowerValue = -1;
        public string PowerState; // boosted, wounded, ... or null
        public int Tiers = 15;
        public bool SkipsNotTargetable;
        public bool ResetToTemplate; // reset includes template_power
        public List<string> Tokens = new List<string>(); // weather tokens it adds
        public string Trigger; // deploy, engine, timer, deathwish, other
        public bool OneMode; // one option of a choose-one
        public int Via; // the created template the answer comes from, or 0

        public static AnswerKind Parse(JToken e)
        {
            AnswerKind a = new AnswerKind
            {
                Kind = (string)e["kind"],
                Scope = (string)e["scope"],
                Select = (string)e["select"],
                TiesAll = (string)e["ties"] == "all",
                Levels = CardRoles.Int(e["levels"], 1),
                Count = e["count"] == null ? 1 : CardRoles.Int(e["count"], -1),
                Amount = CardRoles.Int(e["amount"], -1),
                Hits = CardRoles.Int(e["hits"], 1),
                Row = e["row"] != null && (bool)e["row"],
                EqualPower = e["equal_power"] != null && (bool)e["equal_power"],
                PowerState = (string)e["power_state"],
                Tiers = e["tiers"] == null ? 15 : CardRoles.Mask(e["tiers"], TierNames),
                Trigger = (string)e["trigger"],
                OneMode = (string)e["mode"] == "some",
                Via = CardRoles.Int(e["via"], 0),
            };
            a.RowScoreMin = Threshold((string)e["row_score"], out _);
            string power = (string)e["power"];
            if (power != null)
            {
                a.PowerValue = Threshold(power, out a.PowerOp);
            }
            if (e["skips"] is JArray skips)
            {
                foreach (JToken s in skips)
                {
                    a.SkipsNotTargetable |= (string)s == "not_targetable";
                }
            }
            if (e["reset"] is JArray reset)
            {
                foreach (JToken r in reset)
                {
                    a.ResetToTemplate |= (string)r == "template_power";
                }
            }
            if (e["tokens"] is JArray tokens)
            {
                foreach (JToken t in tokens)
                {
                    a.Tokens.Add((string)t);
                }
            }
            return a;
        }

        // ">=N" -> N with op ">="; anything unreadable -> -1.
        private static int Threshold(string text, out string op)
        {
            op = null;
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }
            int digits = 0;
            while (digits < text.Length && !char.IsDigit(text[digits]))
            {
                digits++;
            }
            op = text.Substring(0, digits);
            int value;
            return digits < text.Length && int.TryParse(text.Substring(digits), out value) ? value : -1;
        }
    }

    // One exposure tag: what a card on its own board can lose, or keeps.
    public sealed class ExposureTag
    {
        public string Tag; // boosts_self, strengthens_self, value_to_come, recoverable, modal_tall, modal_spread, self_damage, armor
        public string Via; // value_to_come / recoverable: engine, timer, deathwish, resilience, comes_back
        public int Amount = -1;
        public int Created; // the created template it comes from, or 0

        public static ExposureTag Parse(JToken e)
        {
            JToken via = e["via"];
            return new ExposureTag
            {
                Tag = (string)e["tag"],
                Via = via != null && via.Type == JTokenType.String ? (string)via : null,
                Amount = CardRoles.Int(e["amount"], -1),
                Created = via != null && via.Type == JTokenType.Integer ? (int)via : CardRoles.Int(e["spawned"], 0),
            };
        }
    }

    public sealed class ReviveReach
    {
        public string From; // own, opp, both
        public bool Units;
        public bool Specials;
        public int Tiers;
        public string To; // play, hand, deck
        public HashSet<int> Targets; // null: any card of the types and tiers

        public static ReviveReach Parse(JToken e)
        {
            ReviveReach r = new ReviveReach
            {
                From = (string)e["from"],
                Tiers = CardRoles.Mask(e["tiers"], AnswerKind.TierNames),
                To = (string)e["to"],
            };
            foreach (JToken t in e["types"])
            {
                r.Units |= (string)t == "unit";
                r.Specials |= (string)t == "special";
            }
            if (e["targets"] is JArray targets)
            {
                r.Targets = new HashSet<int>();
                foreach (JToken t in targets)
                {
                    r.Targets.Add((int)t);
                }
            }
            return r;
        }

        // Can this reach bring back a unit of this template and tier bit?
        public bool ReachesUnit(int template, int tier)
        {
            return Targets != null ? Targets.Contains(template) : Units && (Tiers & tier) != 0;
        }
    }

    public struct ReplaySource
    {
        public int Id;
        public string From; // the reviver's own graveyard, the opponent's, or both
        public string To; // play, hand, deck, owner_deck (back into the card owner's deck)
    }

    public sealed class TokenEffect
    {
        public string Op; // damage, boost
        public string Select; // row, highest, lowest, random, first_n
        public int Amount = -1;
        public int Count = -1;

        public static TokenEffect Parse(JToken e)
        {
            return new TokenEffect
            {
                Op = (string)e["op"],
                Select = (string)e["select"],
                Amount = CardRoles.Int(e["amount"], -1),
                Count = CardRoles.Int(e["count"], -1),
            };
        }
    }
}
