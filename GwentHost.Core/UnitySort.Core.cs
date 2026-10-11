using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using GwentGameplay;
using HarmonyLib;

namespace GwentHost
{
    // UnitySort on .NET: the same sorts get Unity's quicksort, but the
    // engine's calls are redirected instead of the BCL's sort.
    //
    // Harmony cannot replace CoreCLR's ArraySortHelper<T> for reference
    // types: a detour of IntroSort never ran (their shared code is
    // precompiled), and one of IntrospectiveSort stopped running once its
    // callers were recompiled with it inlined. So every method of
    // Assembly-CSharp that calls List<T>.Sort or Array.Sort<T> gets a
    // transpiler that calls the methods below. They sort as Mono with
    // UnitySort does: Unity's quicksort wherever Mono's sort reached
    // ArraySortHelper<T>.IntroSort for a reference type or a listed value
    // type, the BCL's sort elsewhere. All gameplay sorts are in
    // Assembly-CSharp; the other client assemblies sort only in UI code.
    public static partial class UnitySort
    {
        public static void Install(Harmony harmony)
        {
            Assembly engine = typeof(GameController).Assembly;
            HarmonyMethod transpiler = new HarmonyMethod(typeof(UnitySort), nameof(SortCallTranspiler));
            foreach (int method in SortCallers(EngineLoader.Image(engine)))
            {
                harmony.Patch(engine.ManifestModule.ResolveMethod(method), transpiler: transpiler);
            }
        }

        private static IEnumerable<CodeInstruction> SortCallTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> result = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction instruction in result)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                    method.Name == "Sort" && Replacement(method, __originalMethod) is MethodInfo replacement)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
            }
            return result;
        }

        // The method below that stands in for a List<T>.Sort or Array.Sort<T>
        // overload, or null for sorts Mono's ArraySortHelper<T> never ran
        // (Array.Sort<TKey, TValue> and the non-generic Array.Sort).
        private static MethodInfo Replacement(MethodInfo sort, MethodBase caller)
        {
            Type[] parameters;
            Type element;
            string name;
            if (sort.DeclaringType.IsGenericType && sort.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
            {
                element = sort.DeclaringType.GetGenericArguments()[0];
                parameters = new[] { sort.DeclaringType }.Concat(sort.GetParameters().Select(p => p.ParameterType)).ToArray();
                name = nameof(ListSort);
            }
            else if (sort.DeclaringType == typeof(Array) && sort.IsGenericMethod && sort.GetGenericArguments().Length == 1)
            {
                element = sort.GetGenericArguments()[0];
                parameters = sort.GetParameters().Select(p => p.ParameterType).ToArray();
                name = nameof(ArraySort);
            }
            else
            {
                return null;
            }
            foreach (MethodInfo candidate in typeof(UnitySort).GetMethods(BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (candidate.Name == name)
                {
                    MethodInfo closed = candidate.MakeGenericMethod(element);
                    if (closed.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters))
                    {
                        return closed;
                    }
                }
            }
            throw new NotSupportedException($"{caller.DeclaringType}.{caller.Name} calls {sort.DeclaringType}.Sort({string.Join(", ", parameters.Select(p => p.Name))}), which UnitySort does not cover");
        }

        // Whether Mono with UnitySort sorted T with this comparer by Unity's
        // quicksort: every sort that reached ArraySortHelper<T>.IntroSort,
        // which is every comparer sort except a default-comparer sort of an
        // IComparable<T> type (GenericArraySortHelper<T> has its own), and
        // only for the element types UnitySort patched.
        private static bool Replaced<T>(IComparer<T> comparer)
        {
            if (typeof(T).IsValueType && Array.IndexOf(s_ElementTypes, typeof(T)) < 0)
            {
                return false;
            }
            return !(typeof(IComparable<T>).IsAssignableFrom(typeof(T)) && (comparer == null || comparer == Comparer<T>.Default));
        }

        private static class ListItems<T>
        {
            public static readonly AccessTools.FieldRef<List<T>, T[]> Get = AccessTools.FieldRefAccess<List<T>, T[]>("_items");
        }

        private static void ListSort<T>(List<T> list, Comparison<T> comparison)
        {
            if (comparison == null || !Replaced<T>(null))
            {
                list.Sort(comparison);
                return;
            }
            Sorted(list, 0, list.Count, comparison);
        }

        private static void ListSort<T>(List<T> list)
        {
            ListSort(list, 0, list.Count, null);
        }

        private static void ListSort<T>(List<T> list, IComparer<T> comparer)
        {
            ListSort(list, 0, list.Count, comparer);
        }

        private static void ListSort<T>(List<T> list, int index, int count, IComparer<T> comparer)
        {
            if (index < 0 || count < 0 || list.Count - index < count || !Replaced(comparer))
            {
                list.Sort(index, count, comparer);
                return;
            }
            Sorted(list, index, count, (comparer ?? Comparer<T>.Default).Compare);
        }

        // As List<T>.Sort: sorts the range and invalidates enumerators.
        private static void Sorted<T>(List<T> list, int index, int count, Comparison<T> comparison)
        {
            QuickSort(ListItems<T>.Get(list), index, index + count - 1, comparison);
            CollectionsMarshal.SetCount(list, list.Count);
        }

        private static void ArraySort<T>(T[] array)
        {
            ArraySort(array, 0, array?.Length ?? 0, null);
        }

        private static void ArraySort<T>(T[] array, IComparer<T> comparer)
        {
            ArraySort(array, 0, array?.Length ?? 0, comparer);
        }

        private static void ArraySort<T>(T[] array, Comparison<T> comparison)
        {
            if (array == null || comparison == null || !Replaced<T>(null))
            {
                Array.Sort(array, comparison);
                return;
            }
            QuickSort(array, 0, array.Length - 1, comparison);
        }

        private static void ArraySort<T>(T[] array, int index, int length, IComparer<T> comparer)
        {
            if (array == null || index < 0 || length < 0 || array.Length - index < length || !Replaced(comparer))
            {
                Array.Sort(array, index, length, comparer);
                return;
            }
            QuickSort(array, index, index + length - 1, (comparer ?? Comparer<T>.Default).Compare);
        }

        // Methods (MethodDef tokens) whose IL calls a Sort method of List<T>
        // or Array.
        private static IEnumerable<int> SortCallers(byte[] image)
        {
            using PEReader pe = new PEReader(new System.IO.MemoryStream(image));
            MetadataReader metadata = pe.GetMetadataReader();
            HashSet<int> sorts = new HashSet<int>();
            foreach (MemberReferenceHandle handle in metadata.MemberReferences)
            {
                MemberReference member = metadata.GetMemberReference(handle);
                if (metadata.StringComparer.Equals(member.Name, "Sort") && IsListOrArray(metadata, member.Parent))
                {
                    sorts.Add(MetadataTokens.GetToken(handle));
                }
            }
            foreach (MethodSpecificationHandle handle in Enumerable.Range(1, metadata.GetTableRowCount(TableIndex.MethodSpec)).Select(MetadataTokens.MethodSpecificationHandle))
            {
                if (sorts.Contains(MetadataTokens.GetToken(metadata.GetMethodSpecification(handle).Method)))
                {
                    sorts.Add(MetadataTokens.GetToken(handle));
                }
            }
            List<int> callers = new List<int>();
            foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
            {
                int body = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
                if (body != 0 && CallsAny(pe.GetMethodBody(body).GetILBytes(), sorts))
                {
                    callers.Add(MetadataTokens.GetToken(handle));
                }
            }
            return callers;
        }

        private static bool IsListOrArray(MetadataReader metadata, EntityHandle type)
        {
            if (type.Kind == HandleKind.TypeSpecification)
            {
                BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)type).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                {
                    return false;
                }
                signature.ReadSignatureTypeCode();
                type = signature.ReadTypeHandle();
            }
            if (type.Kind != HandleKind.TypeReference)
            {
                return false;
            }
            TypeReference reference = metadata.GetTypeReference((TypeReferenceHandle)type);
            string name = metadata.GetString(reference.Namespace) + "." + metadata.GetString(reference.Name);
            return name == "System.Collections.Generic.List`1" || name == "System.Array";
        }

        private static readonly Dictionary<short, OpCode> s_OpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)).ToDictionary(code => code.Value);

        private static bool CallsAny(byte[] il, HashSet<int> methods)
        {
            for (int at = 0; at < il.Length;)
            {
                short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
                OpCode code = s_OpCodes[value];
                at += code.Size;
                if ((code == OpCodes.Call || code == OpCodes.Callvirt) && methods.Contains(BitConverter.ToInt32(il, at)))
                {
                    return true;
                }
                at += OperandSize(code.OperandType, il, at);
            }
            return false;
        }

        private static int OperandSize(OperandType type, byte[] il, int at)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, at);
                default: return 4;
            }
        }
    }
}
