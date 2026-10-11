using System;
using System.Collections.Generic;
using System.Reflection;
using GwentGameplay;
using HarmonyLib;

namespace GwentHost
{
    // Gives the host the client's sort order. The engine sorts with
    // List<T>.Sort and Array.Sort, which are unstable, and the stock search
    // sorts its candidate moves by score, so how ties land decides moves.
    // Unity's mscorlib (Mono's class libraries of its day) sorts with a plain
    // quicksort; Mono 6 uses CoreFX's introsort, which puts ties in a
    // different order, so the same search would pick different moves.
    //
    // Every generic sort with a comparer or Comparison ends in
    // ArraySortHelper<T>.IntroSort. Its reference-type code is shared, so the
    // object instantiation covers every sort of class elements. Each value
    // type has its own code and needs its own patch. The value types listed
    // are all that the gameplay code sorts with a comparer; a Cecil scan of
    // the client's assemblies found the rest only in Unity UI code. The
    // replacement is Mono's Array.qsort<T> (MIT), as Unity ships it.
    //
    // On .NET (GwentHost.Core) the same sorts are replaced at the engine's
    // call sites instead (UnitySort.Core.cs).
    public static partial class UnitySort
    {
        private static readonly Type[] s_ElementTypes =
        {
            typeof(object),
            typeof(ExecutionStackEntry), // PutCardInExecutionStackAction.Add
            typeof(CardDefinition), // HasNCopiesOfDefinitions.ExecuteImpl
        };

#if !NETCOREAPP
        private static readonly Dictionary<Type, Action<Array, int, int, Delegate>> s_Sorters =
            new Dictionary<Type, Action<Array, int, int, Delegate>>();

        public static void Install(HarmonyLib.Harmony harmony)
        {
            Type helper = AccessTools.TypeByName("System.Collections.Generic.ArraySortHelper`1");
            foreach (Type element in s_ElementTypes)
            {
                harmony.Patch(AccessTools.Method(helper.MakeGenericType(element), "IntroSort"),
                    prefix: new HarmonyMethod(typeof(UnitySort), nameof(IntroSortPrefix)));
            }
        }

        // IntroSort(T[] keys, int lo, int hi, int depthLimit, Comparison<T> comparer)
        private static bool IntroSortPrefix(Array keys, int lo, int hi, Delegate comparer)
        {
            Sorter(keys.GetType().GetElementType())(keys, lo, hi, comparer);
            return false;
        }

        private static Action<Array, int, int, Delegate> Sorter(Type element)
        {
            lock (s_Sorters)
            {
                if (!s_Sorters.TryGetValue(element, out Action<Array, int, int, Delegate> sorter))
                {
                    MethodInfo method = typeof(UnitySort).GetMethod(nameof(Sort), BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(element);
                    sorter = (Action<Array, int, int, Delegate>)Delegate.CreateDelegate(typeof(Action<Array, int, int, Delegate>), method);
                    s_Sorters.Add(element, sorter);
                }
                return sorter;
            }
        }

        private static void Sort<T>(Array keys, int lo, int hi, Delegate comparison)
        {
            QuickSort((T[])keys, lo, hi, (Comparison<T>)comparison);
        }

#endif

        private static void QuickSort<T>(T[] array, int low, int high, Comparison<T> comparison)
        {
            if (low >= high)
            {
                return;
            }
            int left = low;
            int right = high;
            T pivot = array[left + (right - left) / 2];
            while (true)
            {
                if (left < high && comparison(array[left], pivot) < 0)
                {
                    left++;
                    continue;
                }
                while (right > low && comparison(pivot, array[right]) < 0)
                {
                    right--;
                }
                if (left > right)
                {
                    break;
                }
                T swap = array[left];
                array[left] = array[right];
                array[right] = swap;
                left++;
                right--;
            }
            if (low < right)
            {
                QuickSort(array, low, right, comparison);
            }
            if (left < high)
            {
                QuickSort(array, left, high, comparison);
            }
        }
    }
}
