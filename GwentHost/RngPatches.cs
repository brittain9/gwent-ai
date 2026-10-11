using System;
using System.Reflection;
using GwentCore;
using HarmonyLib;

namespace GwentHost
{
    // The engine's random generator, GwentCore.MersenneTwisterRandom, has two
    // private methods that run on every seeding and every refill of its
    // state: the seeding loop (624 steps) and the refill ("twist", every 624
    // draws). The publisher's obfuscator flattened both into switch loops
    // that also call Convert.ToBoolean for each step. A game copy makes a
    // generator (the controller's constructor seeds one) and then copies it
    // by re-seeding it and replaying every draw since the match began, and
    // the search's belief makes one per determinization, so seeding and
    // refilling are 1.4% of a search game.
    //
    // The replacements run the same arithmetic on the same state in plain
    // loops: the same words in the same slots (the slot the seeding loop
    // never writes included), the same return value. The generator's
    // quirks (the output word counts up) are
    // state, not code, and stay.
    //
    // The private fields have no readable names; they are found by type and
    // declaration order: int (seed), uint (draw count), uint[625] (state),
    // uint (output word), int (draws left), uint (last output, doubled).
    internal static class RngPatches
    {
        private static AccessTools.FieldRef<MersenneTwisterRandom, uint[]> s_State;
        private static AccessTools.FieldRef<MersenneTwisterRandom, uint> s_Word;
        private static AccessTools.FieldRef<MersenneTwisterRandom, int> s_Left;

        public static void Install(Harmony harmony)
        {
            FieldInfo[] fields = typeof(MersenneTwisterRandom).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Type[] expected = { typeof(int), typeof(uint), typeof(uint[]), typeof(uint), typeof(int), typeof(uint) };
            bool ok = fields.Length == expected.Length;
            for (int i = 0; ok && i < expected.Length; i++)
            {
                ok = fields[i].FieldType == expected[i];
            }
            if (!ok)
            {
                throw new InvalidOperationException("MersenneTwisterRandom's fields are not the ones the RNG patches were written for");
            }
            s_State = AccessTools.FieldRefAccess<MersenneTwisterRandom, uint[]>(fields[2]);
            s_Word = AccessTools.FieldRefAccess<MersenneTwisterRandom, uint>(fields[3]);
            s_Left = AccessTools.FieldRefAccess<MersenneTwisterRandom, int>(fields[4]);

            MethodInfo seed = Only(m => m.ReturnType == typeof(void) && Takes(m, typeof(uint)), "the seeding method");
            MethodInfo refill = Only(m => m.ReturnType == typeof(uint) && m.GetParameters().Length == 0, "the refill method");
            harmony.Patch(seed, prefix: new HarmonyMethod(typeof(RngPatches), nameof(SeedPrefix)));
            harmony.Patch(refill, prefix: new HarmonyMethod(typeof(RngPatches), nameof(RefillPrefix)));
        }

        private static bool Takes(MethodInfo method, Type parameter)
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == parameter;
        }

        // The class's only private instance method of this shape.
        private static MethodInfo Only(Predicate<MethodInfo> shape, string what)
        {
            MethodInfo found = null;
            foreach (MethodInfo method in typeof(MersenneTwisterRandom).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.IsSpecialName && shape(method))
                {
                    if (found != null)
                    {
                        throw new InvalidOperationException($"MersenneTwisterRandom has more than one candidate for {what}");
                    }
                    found = method;
                }
            }
            if (found == null)
            {
                throw new InvalidOperationException($"MersenneTwisterRandom has no {what}");
            }
            return found;
        }

        // Seeding: the state's first word is the seed made odd, each next
        // word the one before times 69069 (mod 2^32), 623 words in all. Word
        // 624 is not written.
        private static bool SeedPrefix(MersenneTwisterRandom __instance, uint __0)
        {
            uint word = __0 | 1;
            uint[] state = s_State(__instance);
            s_Left(__instance) = 0;
            state[0] = word;
            for (int i = 1; i < 624; i++)
            {
                word *= 69069;
                state[i] = word;
            }
            return false;
        }

        // Refill: the standard recurrence over the 624 words (the seeding
        // here is not the standard one), then the output word restarts at
        // the second state word and the draws left reset to 623. The refill
        // returns the tempered first word of the new state.
        private static bool RefillPrefix(MersenneTwisterRandom __instance, ref uint __result)
        {
            if (s_Left(__instance) < -1)
            {
                // Reachable only after the counter is driven below -1;
                // keep the original's behaviour exactly.
                return true;
            }
            uint[] state = s_State(__instance);
            s_Left(__instance) = 623;
            s_Word(__instance) = state[1];
            uint previous = state[0];
            uint current = state[1];
            int write = 0;
            int next = 2;
            int source = 397;
            for (int i = 0; i < 227; i++)
            {
                state[write++] = state[source++] ^ (((previous & 0x80000000u) | (current & 0x7FFFFFFFu)) >> 1) ^ ((current & 1) != 0 ? 0x9908B0DFu : 0u);
                previous = current;
                current = state[next++];
            }
            source = 0;
            for (int i = 0; i < 396; i++)
            {
                state[write++] = state[source++] ^ (((previous & 0x80000000u) | (current & 0x7FFFFFFFu)) >> 1) ^ ((current & 1) != 0 ? 0x9908B0DFu : 0u);
                previous = current;
                current = state[next++];
            }
            current = state[0];
            state[write] = state[source] ^ (((previous & 0x80000000u) | (current & 0x7FFFFFFFu)) >> 1) ^ ((current & 1) != 0 ? 0x9908B0DFu : 0u);
            current ^= current >> 11;
            current ^= (current << 7) & 0x9D2C5680u;
            current ^= (current << 15) & 0xEFC60000u;
            __result = current ^ (current >> 18);
            return false;
        }
    }
}
