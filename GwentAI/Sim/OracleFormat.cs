using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GwentAI.Sim
{
    // The text of the two oracle files written beside the action trace: the
    // legal actions at each decision and the state dump. Engine-free, so the
    // formats have golden tests (GwentAI.Tests/OracleFormatTests.cs).
    //
    // The formats:
    //   ID.legal: "One line per request delivered to a seat that decides.
    //   <request id> <type> <player>: followed by the Actions.Legal keys in
    //   list order, separated by ' | '. Both the grouped-plays and the
    //   full-plays variants are written, and the observer is the acting seat."
    //   ID.state: "At every decision, and with --state-every-action after
    //   every action. Text, one record per line: C ... for every registered
    //   card in id order; Z <player> <loc> <ids...> for every zone;
    //   T <player> <row> <tokens>; P <player> <passed> <crowns> <initialMove>
    //   <restrictions>; R <round fields>; Q <queue length> <instances: id
    //   state start current>; G <seed> <count>."
    //
    // Every number is invariant-culture decimal; every enum is its integer
    // value (never Enum.ToString, whose flag formatting differs between
    // runtimes); lines end in "\n".
    public static class OracleFormat
    {
        public const string Full = "full";
        public const string Grouped = "grouped";

        // "<request id> <type> <player> <variant>:" then " " and the keys
        // joined by " | ", or nothing when there is no key.
        public static string Legal(int requestId, string type, string player, string variant, IList<string> keys)
        {
            StringBuilder line = new StringBuilder();
            line.Append(Int(requestId)).Append(' ').Append(type).Append(' ').Append(player).Append(' ').Append(variant).Append(':');
            for (int i = 0; i < keys.Count; i++)
            {
                line.Append(i == 0 ? " " : " | ").Append(keys[i]);
            }
            return line.ToString();
        }

        // The header of one state record: a decision (the request delivered)
        // or the state after an action. `traceLine` is the 1-based line of
        // ID.txt whose ApplyAction delivered the request or just returned.
        public static string DecisionHeader(int traceLine, int requestId, string type, string player)
        {
            return "D " + Int(traceLine) + " " + Int(requestId) + " " + type + " " + player;
        }

        public static string ActionHeader(int traceLine)
        {
            return "A " + Int(traceLine);
        }

        // The end of one state record.
        public const string End = "E";

        public static string Card(int id, int template, int owner, int location, int index, int basePower, int permanentPower,
                                  int currentPower, int armor, int tokens, int timer, int visibility, int flags, int playedBy, int faction)
        {
            return Join('C', id, template, owner, location, index, basePower, permanentPower, currentPower, armor, tokens, timer, visibility,
                        flags, playedBy, faction);
        }

        // Card flags: bits of the C line's <flags>.
        public const int WaitingToDie = 1;
        public const int InExecutionStack = 2;
        public const int BeingPlayed = 4;
        public const int CanBePlayed = 8;

        // "Z <player> <loc>" then " <id>" for each card in the zone's order.
        public static string Zone(int player, int location, IList<int> ids)
        {
            StringBuilder line = new StringBuilder();
            line.Append("Z ").Append(Int(player)).Append(' ').Append(Int(location));
            foreach (int id in ids)
            {
                line.Append(' ').Append(Int(id));
            }
            return line.ToString();
        }

        public static string RowToken(int player, int row, int token)
        {
            return Join('T', player, row, token);
        }

        // P <player> <status> <passed> <crowns> <initialMove> <turnsWithoutPlaying>
        //   <enabled> <forceDisabled> <forcePass> <forcePlayCards> <forcePlayLocations>
        //   <forcedTemplateTargets> <selectionCards> <selectionLocations> <forceMulliganCards>
        // Booleans are 0 or 1; the lists are List(...) or Locations(...).
        public static string Player(int player, int status, bool passed, int crowns, bool initialMove, int turnsWithoutPlaying,
                                    int enabled, int forceDisabled, bool forcePass, string forcePlayCards, string forcePlayLocations,
                                    string forcedTemplateTargets, string selectionCards, string selectionLocations, string forceMulliganCards)
        {
            return "P " + Int(player) + " " + Int(status) + " " + Bool(passed) + " " + Int(crowns) + " " + Bool(initialMove) + " " +
                   Int(turnsWithoutPlaying) + " " + Int(enabled) + " " + Int(forceDisabled) + " " + Bool(forcePass) + " " + forcePlayCards + " " +
                   forcePlayLocations + " " + forcedTemplateTargets + " " + selectionCards + " " + selectionLocations + " " + forceMulliganCards;
        }

        // R <current round index> <current player> <game state> then one
        // " <id>:<starting player>:<winner>:<turn index>:<p1 score>:<p2 score>"
        // per round the manager holds (null entries skipped).
        public static string Round(int currentIndex, int currentPlayer, int gameState, IList<int[]> rounds)
        {
            StringBuilder line = new StringBuilder();
            line.Append("R ").Append(Int(currentIndex)).Append(' ').Append(Int(currentPlayer)).Append(' ').Append(Int(gameState));
            foreach (int[] round in rounds)
            {
                line.Append(' ');
                for (int i = 0; i < round.Length; i++)
                {
                    line.Append(i == 0 ? "" : ":").Append(Int(round[i]));
                }
            }
            return line.ToString();
        }

        // X <request id> <request type> <player> <target player> <expired> <ability instance id or -1>
        public static string Request(int id, int type, int player, int target, bool expired, int instance)
        {
            return "X " + Int(id) + " " + Int(type) + " " + Int(player) + " " + Int(target) + " " + Bool(expired) + " " + Int(instance);
        }

        // Q <queued actions> <instances> then " <id> <state> <start> <current> <share>"
        // per active ability instance in order: start and current are the
        // node ids of the start trigger and the current node (-1 for none);
        // share is the index of the first instance holding the same trigger
        // info object (its own index when none before it does).
        public static string Queue(int queued, IList<int[]> instances)
        {
            StringBuilder line = new StringBuilder();
            line.Append("Q ").Append(Int(queued)).Append(' ').Append(Int(instances.Count));
            foreach (int[] instance in instances)
            {
                foreach (int value in instance)
                {
                    line.Append(' ').Append(Int(value));
                }
            }
            return line.ToString();
        }

        // G <initial seed> <draws since creation>
        public static string Generator(int seed, uint count)
        {
            return "G " + Int(seed) + " " + count.ToString(CultureInfo.InvariantCulture);
        }

        // A list field: "-" when empty, else the values joined by ",".
        public static string List(IEnumerable<int> values)
        {
            StringBuilder text = new StringBuilder();
            foreach (int value in values)
            {
                text.Append(text.Length == 0 ? "" : ",").Append(Int(value));
            }
            return text.Length == 0 ? "-" : text.ToString();
        }

        // Rows as "<player>.<location>", joined by ","; "-" when empty.
        public static string Locations(IEnumerable<KeyValuePair<int, int>> rows)
        {
            StringBuilder text = new StringBuilder();
            foreach (KeyValuePair<int, int> row in rows)
            {
                text.Append(text.Length == 0 ? "" : ",").Append(Int(row.Key)).Append('.').Append(Int(row.Value));
            }
            return text.Length == 0 ? "-" : text.ToString();
        }

        // For each element, the index of the first element that is the same
        // object (reference equality): instances that shared one trigger info
        // in the source share one in a faithful copy.
        public static int[] ShareGroups<T>(IList<T> items) where T : class
        {
            int[] groups = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                groups[i] = i;
                for (int j = 0; j < i; j++)
                {
                    if (ReferenceEquals(items[i], items[j]))
                    {
                        groups[i] = j;
                        break;
                    }
                }
            }
            return groups;
        }

        private static string Join(char kind, params int[] values)
        {
            StringBuilder line = new StringBuilder();
            line.Append(kind);
            foreach (int value in values)
            {
                line.Append(' ').Append(Int(value));
            }
            return line.ToString();
        }

        private static string Int(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Bool(bool value)
        {
            return value ? "1" : "0";
        }
    }
}
