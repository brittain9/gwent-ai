using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GwentGameplay;
using HarmonyLib;
using RedTools;
using KV = System.Collections.Generic.KeyValuePair<object, int>;

namespace GwentHost
{
    // The engine's event class, RedTools.BaseEvent, rewritten as plain code
    // with the same behaviour.
    //
    // - The class is in RedTools.dll, which the publisher's obfuscator
    //   flattened into switch loops: its methods are several times slower
    //   than the same logic written plainly. Every method of BaseEvent that
    //   touches a listener list is replaced by a plain implementation of
    //   its logic, step for step (the self-test compares it with the
    //   client's own method).
    // - The persistent listener list is created on the first persistent
    //   listener instead of in the constructor: most of a card's seven
    //   events never get one, and a game copy builds about 800 events. A
    //   missing list reads as an empty one everywhere below.
    //
    // - Typed engine events (Event<T0..>, the core event of every BattleEvent)
    //   keep an argument array that Invoke fills before it calls the base
    //   class. Invoke boxes the value-typed arguments into it, and an event
    //   nobody listens to does nothing with them. With the "invoke" part:
    //   the array is created when the event gets its first listener, and
    //   Invoke returns at once while it does not exist. See ArmArgs for the
    //   proof that an event without an array has never had a listener.
    //
    // The private fields are named for injection into the replacements
    // (Harmony: three underscores, then the field's own name). The client's
    // field names are fixed for the supported client version; a different
    // build fails at install, not by playing differently.
    internal static class EventPatches
    {
        // BaseEvent's fields, by the name the replacements inject them under
        // (three underscores, then the field's own name; each is one of
        // these five):
        //   _6Czmt...  "changed": listeners added or removed since the
        //              dispatch list was built
        //   _EgmY2...  "dispatching"
        //   _Yz92Q...  persistent listeners (null until the first)
        //   _cVbye...  the dispatch list: all listeners in call order
        //   _7ds2B...  single-call listeners (null until the first)

        private static readonly Action<BaseEvent, object, object[]> s_InvokeImpl = CreateInvokeImpl();

        public static void Install(Harmony harmony, bool invoke)
        {
            CheckFieldLayout();
            Patch(harmony, "HasListener", nameof(HasListenerPrefix));
            Patch(harmony, "AddListener", nameof(AddListenerPrefix));
            Patch(harmony, "RemoveListener", nameof(RemoveListenerPrefix));
            Patch(harmony, "Invoke", nameof(InvokePrefix));
            Patch(harmony, "RemoveAllListeners", nameof(RemoveAllListenersPrefix));
            Patch(harmony, "HasListeners", nameof(HasListenersPrefix));
            harmony.Patch(AccessTools.Constructor(typeof(BaseEvent), Type.EmptyTypes),
                transpiler: new HarmonyMethod(typeof(EventPatches), nameof(ConstructorTranspiler)));
            if (invoke)
            {
                InstallInvoke(harmony);
            }
        }

        private static void Patch(Harmony harmony, string method, string prefix)
        {
            MethodInfo original = typeof(BaseEvent).GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (original == null)
            {
                throw new InvalidOperationException($"BaseEvent has no {method}");
            }
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(EventPatches), prefix));
        }

        private static Action<BaseEvent, object, object[]> CreateInvokeImpl()
        {
            MethodInfo method = typeof(BaseEvent).GetMethod("InvokeImpl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (method == null || !method.IsAbstract)
            {
                throw new InvalidOperationException("BaseEvent.InvokeImpl is not the abstract method the event patches replace around");
            }
            return (Action<BaseEvent, object, object[]>)Delegate.CreateDelegate(typeof(Action<BaseEvent, object, object[]>), method);
        }

        // Five fields: bool, bool, List<KV> (readonly), List<KV>, List<KV>,
        // by these names and in this order.
        private static void CheckFieldLayout()
        {
            string[] expected =
            {
                "_6CzmtScCPnxf7hBi9vAQzWn6whg:System.Boolean",
                "_EgmY2ogceGEKw8ydHdYCWmEVVn7:System.Boolean",
                "_Yz92Q4bSbjvA7oB6yKLfLxuCIlO:List",
                "_cVbyef9ahgzdUdHJZZiimPn1k5aA:List",
                "_7ds2Bi2h23cD0GU7HO0XhENLwFP:List",
            };
            FieldInfo[] fields = typeof(BaseEvent).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            bool ok = fields.Length == expected.Length;
            for (int i = 0; ok && i < expected.Length; i++)
            {
                string kind = fields[i].FieldType == typeof(List<KV>) ? "List" : fields[i].FieldType.FullName;
                ok = expected[i] == fields[i].Name + ":" + kind;
            }
            if (!ok)
            {
                throw new InvalidOperationException("BaseEvent's fields are not the ones the event patches were written for");
            }
        }

        // The constructor sized the persistent list for four listeners.
        // It leaves the list null instead (the field starts null).
        private static IEnumerable<HarmonyLib.CodeInstruction> ConstructorTranspiler(IEnumerable<HarmonyLib.CodeInstruction> instructions)
        {
            List<HarmonyLib.CodeInstruction> code = new List<HarmonyLib.CodeInstruction>(instructions);
            ConstructorInfo listConstructor = typeof(List<KV>).GetConstructor(new[] { typeof(int) });
            for (int i = 0; i + 3 < code.Count; i++)
            {
                if (code[i].opcode == System.Reflection.Emit.OpCodes.Ldarg_0 &&
                    code[i + 2].opcode == System.Reflection.Emit.OpCodes.Newobj && Equals(code[i + 2].operand, listConstructor) &&
                    code[i + 3].opcode == System.Reflection.Emit.OpCodes.Stfld)
                {
                    // ldarg.0, ldc.i4 N, newobj List(int), stfld field  ->  nothing
                    code.RemoveRange(i, 4);
                    return code;
                }
            }
            throw new InvalidOperationException("BaseEvent's constructor does not size a listener list the way the event patches expect");
        }

        private static bool KeyEquals(object a, object b)
        {
            return a == null ? b == null : a.Equals(b);
        }

        // List<KV>.Remove(item): the first entry equal to the item (the
        // key by its own Equals, the priority by value), found by the
        // struct's default equality, which is this.
        private static void RemoveFirstEqual(List<KV> list, KV item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                KV entry = list[i];
                if (KeyEquals(entry.Key, item.Key) && entry.Value == item.Value)
                {
                    list.RemoveAt(i);
                    return;
                }
            }
        }

        private static bool Contains(List<KV> persistent, object listener)
        {
            if (persistent == null)
            {
                return false;
            }
            int i = 0;
            for (int count = persistent.Count; i < count; i++)
            {
                if (persistent[i].Key.Equals(listener))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasListenerPrefix(object listener, ref bool __result, List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO)
        {
            __result = Contains(____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, listener);
            return false;
        }

        private static bool AddListenerPrefix(BaseEvent __instance, object listener, bool singleCall, int priority,
            ref bool ____6CzmtScCPnxf7hBi9vAQzWn6whg, ref List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, ref List<KV> ____7ds2Bi2h23cD0GU7HO0XhENLwFP)
        {
            if (s_ArmArgs && ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO == null && ____7ds2Bi2h23cD0GU7HO0XhENLwFP == null)
            {
                ArmArgs(__instance);
            }
            if (singleCall)
            {
                if (____7ds2Bi2h23cD0GU7HO0XhENLwFP == null)
                {
                    ____7ds2Bi2h23cD0GU7HO0XhENLwFP = new List<KV>(2);
                }
                ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Add(new KV(listener, priority));
            }
            else
            {
                if (Contains(____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, listener))
                {
                    return false;
                }
                if (____Yz92Q4bSbjvA7oB6yKLfLxuCIlO == null)
                {
                    ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO = new List<KV>(4);
                }
                ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO.Add(new KV(listener, priority));
            }
            ____6CzmtScCPnxf7hBi9vAQzWn6whg = true;
            return false;
        }

        private static bool RemoveListenerPrefix(object listener,
            ref bool ____6CzmtScCPnxf7hBi9vAQzWn6whg, bool ____EgmY2ogceGEKw8ydHdYCWmEVVn7,
            List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, List<KV> ____cVbyef9ahgzdUdHJZZiimPn1k5aA, List<KV> ____7ds2Bi2h23cD0GU7HO0XhENLwFP)
        {
            if (____7ds2Bi2h23cD0GU7HO0XhENLwFP != null)
            {
                int i = 0;
                for (int count = ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Count; i < count; i++)
                {
                    KV item = ____7ds2Bi2h23cD0GU7HO0XhENLwFP[i];
                    if (item.Key == listener)
                    {
                        if (!____EgmY2ogceGEKw8ydHdYCWmEVVn7 && ____cVbyef9ahgzdUdHJZZiimPn1k5aA != null)
                        {
                            RemoveFirstEqual(____cVbyef9ahgzdUdHJZZiimPn1k5aA, item);
                        }
                        ____7ds2Bi2h23cD0GU7HO0XhENLwFP.RemoveAt(i);
                        break;
                    }
                }
            }
            if (____Yz92Q4bSbjvA7oB6yKLfLxuCIlO != null)
            {
                int j = 0;
                for (int count2 = ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO.Count; j < count2; j++)
                {
                    KV item2 = ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO[j];
                    if (item2.Key.Equals(listener))
                    {
                        if (!____EgmY2ogceGEKw8ydHdYCWmEVVn7 && ____cVbyef9ahgzdUdHJZZiimPn1k5aA != null)
                        {
                            RemoveFirstEqual(____cVbyef9ahgzdUdHJZZiimPn1k5aA, item2);
                        }
                        ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO.RemoveAt(j);
                        break;
                    }
                }
            }
            ____6CzmtScCPnxf7hBi9vAQzWn6whg = true;
            return false;
        }

        // The lists are read through the fields at each use: a listener may
        // create the persistent or the single-call list during the dispatch.
        private static bool InvokePrefix(BaseEvent __instance, object[] args,
            ref bool ____6CzmtScCPnxf7hBi9vAQzWn6whg, ref bool ____EgmY2ogceGEKw8ydHdYCWmEVVn7,
            ref List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, ref List<KV> ____cVbyef9ahgzdUdHJZZiimPn1k5aA, ref List<KV> ____7ds2Bi2h23cD0GU7HO0XhENLwFP)
        {
            if (____6CzmtScCPnxf7hBi9vAQzWn6whg)
            {
                Rebuild(ref ____6CzmtScCPnxf7hBi9vAQzWn6whg, ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, ref ____cVbyef9ahgzdUdHJZZiimPn1k5aA, ____7ds2Bi2h23cD0GU7HO0XhENLwFP);
            }
            List<KV> dispatch = ____cVbyef9ahgzdUdHJZZiimPn1k5aA;
            if (dispatch != null)
            {
                ____EgmY2ogceGEKw8ydHdYCWmEVVn7 = true;
                int i = 0;
                for (int count = dispatch.Count; i < count; i++)
                {
                    s_InvokeImpl(__instance, dispatch[i].Key, args);
                }
                if (____7ds2Bi2h23cD0GU7HO0XhENLwFP != null && ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Count > 0)
                {
                    ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Clear();
                    ____6CzmtScCPnxf7hBi9vAQzWn6whg = true;
                }
                ____EgmY2ogceGEKw8ydHdYCWmEVVn7 = false;
            }
            if (____6CzmtScCPnxf7hBi9vAQzWn6whg)
            {
                Rebuild(ref ____6CzmtScCPnxf7hBi9vAQzWn6whg, ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, ref ____cVbyef9ahgzdUdHJZZiimPn1k5aA, ____7ds2Bi2h23cD0GU7HO0XhENLwFP);
            }
            return false;
        }

        // The dispatch list: persistent listeners, then single-call ones,
        // each inserted after the last entry of lower or equal priority.
        private static void Rebuild(ref bool changed, List<KV> persistent, ref List<KV> dispatch, List<KV> single)
        {
            if (!changed)
            {
                return;
            }
            if (dispatch == null)
            {
                dispatch = new List<KV>(persistent == null ? 0 : persistent.Count);
            }
            else
            {
                dispatch.Clear();
            }
            if (persistent != null)
            {
                int i = 0;
                for (int count = persistent.Count; i < count; i++)
                {
                    Insert(dispatch, persistent[i]);
                }
            }
            if (single != null)
            {
                int j = 0;
                for (int count2 = single.Count; j < count2; j++)
                {
                    Insert(dispatch, single[j]);
                }
            }
            changed = false;
        }

        private static void Insert(List<KV> dispatch, KV entry)
        {
            if (dispatch.Count != 0 && dispatch[dispatch.Count - 1].Value > entry.Value)
            {
                bool inserted = false;
                int index = dispatch.Count - 2;
                while (index >= 0)
                {
                    if (dispatch[index].Value > entry.Value)
                    {
                        index--;
                        continue;
                    }
                    dispatch.Insert(index + 1, entry);
                    inserted = true;
                    break;
                }
                if (!inserted)
                {
                    dispatch.Insert(0, entry);
                }
            }
            else
            {
                dispatch.Add(entry);
            }
        }

        private static bool RemoveAllListenersPrefix(ref bool ____6CzmtScCPnxf7hBi9vAQzWn6whg,
            List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, List<KV> ____cVbyef9ahgzdUdHJZZiimPn1k5aA, List<KV> ____7ds2Bi2h23cD0GU7HO0XhENLwFP)
        {
            if (____Yz92Q4bSbjvA7oB6yKLfLxuCIlO != null)
            {
                ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO.Clear();
            }
            if (____cVbyef9ahgzdUdHJZZiimPn1k5aA != null)
            {
                ____cVbyef9ahgzdUdHJZZiimPn1k5aA.Clear();
            }
            if (____7ds2Bi2h23cD0GU7HO0XhENLwFP != null)
            {
                ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Clear();
            }
            ____6CzmtScCPnxf7hBi9vAQzWn6whg = false;
            return false;
        }

        private static bool HasListenersPrefix(ref bool __result, List<KV> ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO, List<KV> ____7ds2Bi2h23cD0GU7HO0XhENLwFP)
        {
            if (____Yz92Q4bSbjvA7oB6yKLfLxuCIlO != null && ____Yz92Q4bSbjvA7oB6yKLfLxuCIlO.Count > 0)
            {
                __result = true;
            }
            else
            {
                __result = ____7ds2Bi2h23cD0GU7HO0XhENLwFP != null && ____7ds2Bi2h23cD0GU7HO0XhENLwFP.Count > 0;
            }
            return false;
        }

        // ---- typed events: lazy argument array, no-listener Invoke ---------

        private static bool s_ArmArgs;
        private static readonly object s_ArmLock = new object();
        private static readonly Dictionary<Type, Action<BaseEvent>> s_Arm = new Dictionary<Type, Action<BaseEvent>>();

        // The closed Event<T0..> types of the engine's battle events: the
        // core event of every BattleEvent field or property declared in the
        // engine's assembly, with at least one type argument.
        public static List<Type> EngineEventTypes()
        {
            SortedDictionary<string, Type> found = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            foreach (Type type in LoadableTypes(typeof(Card).Assembly))
            {
                const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (FieldInfo field in type.GetFields(All))
                {
                    Consider(field.FieldType, found);
                }
                foreach (PropertyInfo property in type.GetProperties(All))
                {
                    Consider(property.PropertyType, found);
                }
            }
            return new List<Type>(found.Values);
        }

        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                List<Type> loaded = new List<Type>();
                foreach (Type type in e.Types)
                {
                    if (type != null)
                    {
                        loaded.Add(type);
                    }
                }
                return loaded;
            }
        }

        private static void Consider(Type type, SortedDictionary<string, Type> found)
        {
            if (type.IsArray)
            {
                type = type.GetElementType();
            }
            for (Type b = type; b != null; b = b.BaseType)
            {
                if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(ABattleEvent<>))
                {
                    Type core = b.GetGenericArguments()[0];
                    if (core.IsGenericType && !core.ContainsGenericParameters)
                    {
                        found[core.FullName] = core;
                    }
                    return;
                }
            }
        }

        private static void InstallInvoke(Harmony harmony)
        {
            s_ArmArgs = true;
            foreach (Type type in EngineEventTypes())
            {
                ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                MethodInfo invoke = type.GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (constructor == null || invoke == null)
                {
                    throw new InvalidOperationException($"{type} has no constructor or Invoke the event patches can rewrite");
                }
                harmony.Patch(constructor, transpiler: new HarmonyMethod(typeof(EventPatches), nameof(ArgsConstructorTranspiler)));
                harmony.Patch(invoke, transpiler: new HarmonyMethod(typeof(EventPatches), nameof(InvokeTranspiler)));
            }
        }

        private static bool IsLoadInt(CodeInstruction instruction)
        {
            OpCode code = instruction.opcode;
            return code == OpCodes.Ldc_I4 || code == OpCodes.Ldc_I4_S || code == OpCodes.Ldc_I4_0 || code == OpCodes.Ldc_I4_1 ||
                   code == OpCodes.Ldc_I4_2 || code == OpCodes.Ldc_I4_3 || code == OpCodes.Ldc_I4_4 || code == OpCodes.Ldc_I4_5 ||
                   code == OpCodes.Ldc_I4_6 || code == OpCodes.Ldc_I4_7 || code == OpCodes.Ldc_I4_8;
        }

        // The constructor creates the argument array (ldc N, newarr object,
        // stfld): the field starts null instead.
        private static IEnumerable<CodeInstruction> ArgsConstructorTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int found = 0;
            for (int i = 1; i + 1 < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Newarr && Equals(code[i].operand, typeof(object)) &&
                    IsLoadInt(code[i - 1]) && code[i + 1].opcode == OpCodes.Stfld)
                {
                    code.RemoveRange(i - 1, 2);
                    code.Insert(i - 1, new CodeInstruction(OpCodes.Ldnull));
                    found++;
                    break;
                }
            }
            if (found != 1)
            {
                throw new InvalidOperationException("an Event constructor does not create its argument array the way the event patches expect");
            }
            return code;
        }

        // Invoke(args...) stores its arguments in the array, then calls the
        // base class. With no array the event has no listener: return.
        private static IEnumerable<CodeInstruction> InvokeTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            FieldInfo array = null;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field && field.FieldType == typeof(object[]))
                {
                    array = field;
                    break;
                }
            }
            if (array == null || code.Count == 0)
            {
                throw new InvalidOperationException("an Event's Invoke does not use its argument array the way the event patches expect");
            }
            Label proceed = generator.DefineLabel();
            code[0].labels.Add(proceed);
            code.InsertRange(0, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, array),
                new CodeInstruction(OpCodes.Brtrue_S, proceed),
                new CodeInstruction(OpCodes.Ret),
            });
            return code;
        }

        // An event's first listener: create its argument array. The array
        // is missing only on an event built after the constructor patch
        // that has never had a listener, because this runs before the first
        // listener of any kind is stored and nothing else creates it, so
        // an Invoke that finds none skips a call that would have found no
        // listener. The same method serves events of types the patch did not
        // rewrite: their array exists and stays.
        private static void ArmArgs(BaseEvent instance)
        {
            Type type = instance.GetType();
            Action<BaseEvent> arm;
            lock (s_ArmLock)
            {
                if (!s_Arm.TryGetValue(type, out arm))
                {
                    arm = BuildArm(type);
                    s_Arm.Add(type, arm);
                }
            }
            if (arm != null)
            {
                arm(instance);
            }
        }

        // For the event class that declares the array: if (e.array == null)
        // e.array = new object[arity]. Null for events without one.
        private static Action<BaseEvent> BuildArm(Type type)
        {
            for (Type b = type; b != null && b != typeof(BaseEvent); b = b.BaseType)
            {
                foreach (FieldInfo field in b.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(object[]))
                    {
                        continue;
                    }
                    int arity = b.IsGenericType ? b.GetGenericArguments().Length : 0;
                    DynamicMethod method = new DynamicMethod("ArmArgs", typeof(void), new[] { typeof(BaseEvent) }, typeof(EventPatches).Module, true);
                    ILGenerator il = method.GetILGenerator();
                    Label done = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, b);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Brtrue_S, done);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, b);
                    il.Emit(OpCodes.Ldc_I4, arity);
                    il.Emit(OpCodes.Newarr, typeof(object));
                    il.Emit(OpCodes.Stfld, field);
                    il.MarkLabel(done);
                    il.Emit(OpCodes.Ret);
                    return (Action<BaseEvent>)method.CreateDelegate(typeof(Action<BaseEvent>));
                }
            }
            return null;
        }
    }
}
