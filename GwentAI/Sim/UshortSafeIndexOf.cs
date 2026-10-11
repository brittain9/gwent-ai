using System;
using System.Reflection;
using HarmonyLib;

namespace GwentAI.Sim
{
    // Keeps Mono 6.14's JIT from reading past the end of a ushort[].
    // GenericEqualityComparer<ushort>
    // inlines UInt16.Equals into IndexOf and LastIndexOf, and the inlined
    // call null-checks `this` with a 4-byte read of a 2-byte element. For the
    // last element of an array that ends a mapped region the extra 2 bytes
    // fault, and the process dies with SIGSEGV. The engine hits it through
    // List<ushort>.Contains, Remove and IndexOf on card id lists.
    //
    // A plain `==` loop has no inlined Equals and so no null check. The
    // prefixes below replace both methods with such loops. They keep the
    // original's results and its exceptions: the original has no argument
    // checks of its own (Array.IndexOf makes them) and fails only on the
    // element access, as the loops do.
    //
    // On by default; GWENT_USHORT_SAFE_INDEXOF=0 turns it off. It changes no
    // play (identical traces on and off). The host installs it. The
    // client's Unity Mono has no IndexOf on this comparer, so the mod
    // does not.
    public static class UshortSafeIndexOf
    {
        public const string Flag = "GWENT_USHORT_SAFE_INDEXOF";

        public static bool Enabled => Environment.GetEnvironmentVariable(Flag) != "0";

        public static void Install(HarmonyLib.Harmony harmony)
        {
            if (!Enabled)
            {
                return;
            }
            Type comparer = AccessTools.TypeByName("System.Collections.Generic.GenericEqualityComparer`1")
                .MakeGenericType(typeof(ushort));
            harmony.Patch(Method(comparer, "IndexOf"),
                prefix: new HarmonyMethod(typeof(UshortSafeIndexOf), nameof(IndexOfPrefix)));
            harmony.Patch(Method(comparer, "LastIndexOf"),
                prefix: new HarmonyMethod(typeof(UshortSafeIndexOf), nameof(LastIndexOfPrefix)));
        }

        private static MethodBase Method(Type comparer, string name)
        {
            Type[] parameters = { typeof(ushort[]), typeof(ushort), typeof(int), typeof(int) };
            MethodBase method = AccessTools.Method(comparer, name, parameters);
            if (method == null)
            {
                throw new InvalidOperationException($"{Flag}: {comparer} has no {name}(ushort[], ushort, int, int)");
            }
            return method;
        }

        private static bool IndexOfPrefix(ushort[] array, ushort value, int startIndex, int count, ref int __result)
        {
            int end = startIndex + count;
            for (int i = startIndex; i < end; i++)
            {
                if (array[i] == value)
                {
                    __result = i;
                    return false;
                }
            }
            __result = -1;
            return false;
        }

        private static bool LastIndexOfPrefix(ushort[] array, ushort value, int startIndex, int count, ref int __result)
        {
            int end = startIndex - count + 1;
            for (int i = startIndex; i >= end; i--)
            {
                if (array[i] == value)
                {
                    __result = i;
                    return false;
                }
            }
            __result = -1;
            return false;
        }
    }
}
