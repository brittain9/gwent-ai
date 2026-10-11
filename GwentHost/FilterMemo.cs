using System;
using System.Collections.Generic;
using GwentGameplay;
using HarmonyLib;

namespace GwentHost
{
    // CardDefinitionHelper.FilterDefinitions scans every card template (about
    // a thousand) for each call of a "get card definitions" ability node.
    // The scan reads only the template set, which is loaded once and never
    // changes, and the call's arguments, so its result is a function of the
    // arguments. The first call with a given argument tuple runs the
    // original and records the template ids it appended; later calls append
    // the same definitions from the record, new values each time.
    //
    // Memoized only when the templates are the shared template dictionary's
    // value collection (GetCardsDefinitionsNode). The deck-based nodes pass a
    // list built from the deck on every call and run the original.
    internal static class FilterMemo
    {
        private const int MaxEntries = 8192;
        private const int MaxWords = 8;

        private static readonly object s_Lock = new object();
        private static readonly Dictionary<int, List<Entry>> s_Buckets = new Dictionary<int, List<Entry>>();
        private static object s_Source;
        private static int s_SourceCount;
        private static int s_Entries;

        // Entries recorded so far (the self-test checks the memo is live).
        public static int EntryCount
        {
            get
            {
                lock (s_Lock)
                {
                    return s_Entries;
                }
            }
        }

        public static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(CardDefinitionHelper), nameof(CardDefinitionHelper.FilterDefinitions)),
                prefix: new HarmonyMethod(typeof(FilterMemo), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(FilterMemo), nameof(Postfix)));
        }

        // One argument tuple and the template ids its scan matched.
        private sealed class Entry
        {
            public int Hash;
            public bool AvailabilityActive;
            public ulong[] Availability;
            public long Kind, Faction, Tier, Type, Tokens, PlayerSide, OpponentSide, Compare;
            public bool CategoryActive;
            public ulong[] Category;
            public bool Premium;
            public int[] Valid;
            public int[] TemplateIds;
        }

        private sealed class Pending
        {
            public Entry Entry;
            public int Start;
            public object Source;
            public int SourceCount;
        }

        private static bool Prefix(IEnumerable<RuntimeCardTemplate> templates, List<CardDefinition> definitions,
            EnumBitArray<EAvailability> availability, ECardKind kind, EFactionId factionId, ECardTier tier,
            ECardType type, ECardToken tokens, ELocation playerSide, ELocation opponentSide, CardCategory category,
            ECategoryCompareMethod compareMethod, bool premium, List<CardDefinition> validDefinitions, out Pending __state)
        {
            __state = null;
            Dictionary<int, RuntimeCardTemplate>.ValueCollection source = templates as Dictionary<int, RuntimeCardTemplate>.ValueCollection;
            if (source == null)
            {
                return true;
            }
            bool availabilityActive = availability != null && availability.IsAnyBitSet();
            ulong[] availabilityWords = availabilityActive ? availability.BitMasks : null;
            bool categoryActive = category != null && category.Count != 0;
            ulong[] categoryWords = categoryActive ? category.Value.BitMasks : null;
            bool validActive = validDefinitions != null && validDefinitions.Count > 0;
            if ((availabilityWords != null && availabilityWords.Length > MaxWords) || (categoryWords != null && categoryWords.Length > MaxWords))
            {
                return true;
            }
            int hash = Hash(availabilityWords, categoryWords, validActive ? validDefinitions : null,
                (long)kind, (long)factionId, (long)tier, (long)type, (long)tokens, (long)playerSide, (long)opponentSide,
                (long)compareMethod, premium);
            int sourceCount = source.Count;
            lock (s_Lock)
            {
                if (!ReferenceEquals(s_Source, source) || s_SourceCount != sourceCount)
                {
                    s_Buckets.Clear();
                    s_Entries = 0;
                    s_Source = source;
                    s_SourceCount = sourceCount;
                }
                if (s_Buckets.TryGetValue(hash, out List<Entry> bucket))
                {
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        Entry entry = bucket[i];
                        if (Matches(entry, availabilityActive, availabilityWords, (long)kind, (long)factionId, (long)tier,
                                (long)type, (long)tokens, (long)playerSide, (long)opponentSide, categoryActive, categoryWords,
                                (long)compareMethod, premium, validActive ? validDefinitions : null))
                        {
                            int[] ids = entry.TemplateIds;
                            for (int t = 0; t < ids.Length; t++)
                            {
                                definitions.Add(new CardDefinition(ids[t], premium));
                            }
                            return false;
                        }
                    }
                }
                if (s_Entries >= MaxEntries)
                {
                    return true;
                }
            }
            __state = new Pending
            {
                Start = definitions.Count,
                Source = source,
                SourceCount = sourceCount,
                Entry = new Entry
                {
                    Hash = hash,
                    AvailabilityActive = availabilityActive,
                    Availability = Copy(availabilityWords),
                    Kind = (long)kind, Faction = (long)factionId, Tier = (long)tier, Type = (long)type, Tokens = (long)tokens,
                    PlayerSide = (long)playerSide, OpponentSide = (long)opponentSide, Compare = (long)compareMethod,
                    CategoryActive = categoryActive,
                    Category = Copy(categoryWords),
                    Premium = premium,
                    Valid = validActive ? Ids(validDefinitions) : null,
                }
            };
            return true;
        }

        // Runs after the original appended its matches (not when it threw).
        private static void Postfix(List<CardDefinition> definitions, Pending __state)
        {
            if (__state == null)
            {
                return;
            }
            int count = definitions.Count - __state.Start;
            int[] ids = new int[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = definitions[__state.Start + i].TemplateId;
            }
            __state.Entry.TemplateIds = ids;
            lock (s_Lock)
            {
                if (!ReferenceEquals(s_Source, __state.Source) || s_SourceCount != __state.SourceCount || s_Entries >= MaxEntries)
                {
                    return;
                }
                if (!s_Buckets.TryGetValue(__state.Entry.Hash, out List<Entry> bucket))
                {
                    bucket = new List<Entry>(1);
                    s_Buckets.Add(__state.Entry.Hash, bucket);
                }
                bucket.Add(__state.Entry);
                s_Entries++;
            }
        }

        private static ulong[] Copy(ulong[] words)
        {
            return words == null ? null : (ulong[])words.Clone();
        }

        private static int[] Ids(List<CardDefinition> definitions)
        {
            int[] ids = new int[definitions.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = definitions[i].TemplateId;
            }
            return ids;
        }

        private static int Hash(ulong[] availability, ulong[] category, List<CardDefinition> valid, long kind, long faction,
            long tier, long type, long tokens, long playerSide, long opponentSide, long compare, bool premium)
        {
            ulong h = 14695981039346656037UL;
            h = Mix(h, availability == null ? 0UL : 1UL);
            if (availability != null)
            {
                for (int i = 0; i < availability.Length; i++)
                {
                    h = Mix(h, availability[i]);
                }
            }
            h = Mix(h, category == null ? 0UL : 1UL);
            if (category != null)
            {
                for (int i = 0; i < category.Length; i++)
                {
                    h = Mix(h, category[i]);
                }
            }
            h = Mix(h, valid == null ? 0UL : (ulong)valid.Count + 1UL);
            if (valid != null)
            {
                for (int i = 0; i < valid.Count; i++)
                {
                    h = Mix(h, (ulong)valid[i].TemplateId);
                }
            }
            h = Mix(h, (ulong)kind);
            h = Mix(h, (ulong)faction);
            h = Mix(h, (ulong)tier);
            h = Mix(h, (ulong)type);
            h = Mix(h, (ulong)tokens);
            h = Mix(h, (ulong)playerSide);
            h = Mix(h, (ulong)opponentSide);
            h = Mix(h, (ulong)compare);
            h = Mix(h, premium ? 1UL : 0UL);
            return (int)(h ^ (h >> 32));
        }

        private static ulong Mix(ulong h, ulong value)
        {
            return (h ^ value) * 1099511628211UL;
        }

        private static bool Matches(Entry entry, bool availabilityActive, ulong[] availability, long kind, long faction,
            long tier, long type, long tokens, long playerSide, long opponentSide, bool categoryActive, ulong[] category,
            long compare, bool premium, List<CardDefinition> valid)
        {
            if (entry.AvailabilityActive != availabilityActive || entry.CategoryActive != categoryActive ||
                entry.Kind != kind || entry.Faction != faction || entry.Tier != tier || entry.Type != type ||
                entry.Tokens != tokens || entry.PlayerSide != playerSide || entry.OpponentSide != opponentSide ||
                entry.Compare != compare || entry.Premium != premium)
            {
                return false;
            }
            if (!SameWords(entry.Availability, availability) || !SameWords(entry.Category, category))
            {
                return false;
            }
            if ((entry.Valid == null) != (valid == null))
            {
                return false;
            }
            if (valid != null)
            {
                if (entry.Valid.Length != valid.Count)
                {
                    return false;
                }
                for (int i = 0; i < entry.Valid.Length; i++)
                {
                    if (entry.Valid[i] != valid[i].TemplateId)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool SameWords(ulong[] a, ulong[] b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
