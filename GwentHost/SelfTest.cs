using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using GwentAI.Sim;

namespace GwentHost
{
    // Checks of the host's own patches, each in a process of its own because
    // a failing check can be a crash.
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --self-test ushort-indexof
    //
    // Installs GWENT_USHORT_SAFE_INDEXOF's patch as the host does (on unless
    // the variable is 0), prints how the patched comparer answers a grid of arguments
    // (a checksum, equal with and without the flag; the patch must not change
    // an answer or an exception), then runs the fault of
    // UshortSafeIndexOf: a ushort[] whose last element ends on a
    // page boundary, with the next page PROT_NONE, searched through
    // Array.IndexOf, Array.LastIndexOf and List<ushort>. With the flag
    // at 0 the process dies of SIGSEGV there; otherwise the last line is "survived" and
    // the exit status 0.
    public static class SelfTest
    {
        private const int PageSize = 4096;
        private const int ProtNone = 0;
        private const int ProtReadWrite = 3;
        private const ushort Absent = 65535;

        [DllImport("libc", SetLastError = true)]
        private static extern int mprotect(IntPtr address, UIntPtr length, int protection);

        public static int Run(string name, string client)
        {
            if (name == "engine-pack")
            {
                return EnginePackSelfTest.Run(client);
            }
            if (name != "ushort-indexof")
            {
                Console.Error.WriteLine($"GwentHost: no self-test {name}; known: ushort-indexof, engine-pack");
                return 2;
            }
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("gwent-host-self-test");
            UshortSafeIndexOf.Install(harmony);
            Console.WriteLine("ushort-indexof flag=" + (UshortSafeIndexOf.Enabled ? "on" : "off"));
            if (!ComparerGrid())
            {
                return 1;
            }
            return GuardPage();
        }

        // The comparer's answers over arrays of every small length, with
        // values present and absent and start and count in and out of range
        // (the comparer has no argument checks: out of range ends in the
        // element access). Answers inside the range are checked against a
        // plain loop; every answer goes into the checksum.
        private static bool ComparerGrid()
        {
            object comparer = System.Collections.Generic.EqualityComparer<ushort>.Default;
            Type[] parameters = { typeof(ushort[]), typeof(ushort), typeof(int), typeof(int) };
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo indexOf = comparer.GetType().GetMethod("IndexOf", Flags, null, parameters, null);
            MethodInfo lastIndexOf = comparer.GetType().GetMethod("LastIndexOf", Flags, null, parameters, null);
            if (indexOf == null || lastIndexOf == null)
            {
                Console.WriteLine("comparer has no IndexOf or LastIndexOf");
                return false;
            }
            ulong hash = 14695981039346656037UL;
            int cases = 0, mismatches = 0;
            ushort[] values = { 0, 1, 2, Absent };
            for (int length = -1; length <= 9; length++)
            {
                ushort[] array = length < 0 ? null : new ushort[length];
                for (int i = 0; i < length; i++)
                {
                    array[i] = (ushort)(i % 3);
                }
                foreach (ushort value in values)
                {
                    for (int start = -1; start <= Math.Max(length, 0) + 1; start++)
                    {
                        for (int count = -1; count <= Math.Max(length, 0) + 2; count++)
                        {
                            foreach (MethodInfo method in new[] { indexOf, lastIndexOf })
                            {
                                bool forward = method == indexOf;
                                string answer = Answer(method, comparer, array, value, start, count);
                                bool inRange = array != null && count >= 0 && (forward
                                    ? start >= 0 && start + count <= length
                                    : start < length && start - count + 1 >= 0);
                                if (inRange && answer != Expected(array, value, start, count, forward))
                                {
                                    mismatches++;
                                }
                                foreach (char c in answer)
                                {
                                    hash = (hash ^ c) * 1099511628211UL;
                                }
                                cases++;
                            }
                        }
                    }
                }
            }
            foreach (MethodInfo method in new[] { indexOf, lastIndexOf })
            {
                // Sums that wrap around int.
                foreach (int start in new[] { int.MaxValue, int.MinValue })
                {
                    string answer = Answer(method, comparer, new ushort[3], 1, start, 2);
                    foreach (char c in answer)
                    {
                        hash = (hash ^ c) * 1099511628211UL;
                    }
                    cases++;
                }
            }
            Console.WriteLine($"semantics cases={cases} mismatches={mismatches} hash={hash:x16}");
            return mismatches == 0;
        }

        private static string Answer(MethodInfo method, object comparer, ushort[] array, ushort value, int start, int count)
        {
            try
            {
                return ((int)method.Invoke(comparer, new object[] { array, value, start, count })).ToString();
            }
            catch (TargetInvocationException exception)
            {
                return exception.InnerException.GetType().Name;
            }
        }

        private static string Expected(ushort[] array, ushort value, int start, int count, bool forward)
        {
            if (forward)
            {
                for (int i = start; i < start + count; i++)
                {
                    if (array[i] == value)
                    {
                        return i.ToString();
                    }
                }
            }
            else
            {
                for (int i = start; i > start - count; i--)
                {
                    if (array[i] == value)
                    {
                        return i.ToString();
                    }
                }
            }
            return "-1";
        }

        // The window between the two mprotect calls allocates nothing: a
        // collection that reads the object after the array would fault too.
        private static int GuardPage()
        {
            List<GCHandle> handles = new List<GCHandle>();
            ushort[] array = null;
            IntPtr guard = IntPtr.Zero;
            for (int length = 4000; length < 6000 && array == null; length++)
            {
                ushort[] candidate = new ushort[length];
                GCHandle handle = GCHandle.Alloc(candidate, GCHandleType.Pinned);
                handles.Add(handle);
                long end = (long)handle.AddrOfPinnedObject() + (long)length * 2;
                if ((end & (PageSize - 1)) == 0 && mprotect((IntPtr)end, (UIntPtr)PageSize, ProtNone) == 0)
                {
                    array = candidate;
                    guard = (IntPtr)end;
                }
            }
            if (array == null)
            {
                Console.WriteLine("no ushort[] ends on a page boundary before a mapped page");
                return 3;
            }
            // Back to readable for now: the list below allocates.
            mprotect(guard, (UIntPtr)PageSize, ProtReadWrite);
            List<ushort> list = new List<ushort>();
            typeof(List<ushort>).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(list, array);
            typeof(List<ushort>).GetField("_size", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(list, array.Length);
            // Compile every generic instance first: the JIT allocates.
            ushort[] warm = new ushort[4];
            List<ushort> warmList = new List<ushort>(warm);
            Array.IndexOf(warm, Absent);
            Array.IndexOf(warm, Absent, 0);
            Array.LastIndexOf(warm, Absent);
            warmList.Contains(Absent);
            warmList.IndexOf(Absent);
            warmList.Remove(Absent);
            Console.WriteLine($"guard page after a ushort[{array.Length}]; searching");
            Console.Out.Flush();
            mprotect(guard, (UIntPtr)PageSize, ProtNone);
            int indexOf = Array.IndexOf(array, Absent);
            int indexOfFrom = Array.IndexOf(array, Absent, 0);
            int lastIndexOf = Array.LastIndexOf(array, Absent);
            bool contains = list.Contains(Absent);
            int listIndexOf = list.IndexOf(Absent);
            bool removed = list.Remove(Absent);
            mprotect(guard, (UIntPtr)PageSize, ProtReadWrite);
            foreach (GCHandle handle in handles)
            {
                handle.Free();
            }
            bool answers = indexOf == -1 && indexOfFrom == -1 && lastIndexOf == -1 && !contains && listIndexOf == -1 && !removed;
            Console.WriteLine(answers ? "survived" : "survived with wrong answers");
            return answers ? 0 : 1;
        }
    }
}
