using System;
using System.Collections.Generic;
using GwentGameplay;
using HarmonyLib;

namespace GwentHost
{
    // Which parts of the pack (GWENT_ENGINE_PATCHES=2) are installed:
    // GWENT_ENGINE_PACK=filter,authority,events,invoke,rng,presize, default all.
    public sealed class Pack
    {
        public static readonly string[] Names = { "filter", "authority", "events", "invoke", "rng", "presize" };
        public static readonly Pack None = new Pack(new HashSet<string>());

        private readonly HashSet<string> m_Parts;

        private Pack(HashSet<string> parts)
        {
            m_Parts = parts;
        }

        // Every part the runtime supports. "invoke" rewrites the constructor
        // and Invoke of the typed events together: if the JIT inlined one of
        // them unpatched (CoreCLR can: GwentHost.Core.csproj), an event
        // without an argument array would meet an Invoke that expects one.
        // The Mono host's JIT does not (the self-test checks that each
        // patched type behaves), so the part is Mono's only.
        public static Pack All
        {
            get
            {
                HashSet<string> parts = new HashSet<string>(Names);
#if NETCOREAPP
                parts.Remove("invoke");
#endif
                return new Pack(parts);
            }
        }

        public static Pack FromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(EnginePatches.PackVariable);
            return string.IsNullOrEmpty(value) ? All : Parse(value);
        }

        private static Pack Parse(string list)
        {
            HashSet<string> parts = new HashSet<string>();
            foreach (string name in list.Split(','))
            {
                if (Array.IndexOf(Names, name) < 0)
                {
                    throw new InvalidOperationException($"{EnginePatches.PackVariable}: no part {name}; known: {string.Join(", ", Names)}");
                }
#if NETCOREAPP
                if (name == "invoke")
                {
                    throw new InvalidOperationException($"{EnginePatches.PackVariable}: part invoke is for the Mono host (see Pack.All)");
                }
#endif
                parts.Add(name);
            }
            return new Pack(parts);
        }

        public bool Filter => m_Parts.Contains("filter");
        public bool Authority => m_Parts.Contains("authority");
        public bool Events => m_Parts.Contains("events");
        public bool Invoke => m_Parts.Contains("invoke");
        public bool Rng => m_Parts.Contains("rng");
        public bool Presize => m_Parts.Contains("presize");
    }

    // The pack: byte-identical patches for the hot spots of a search
    // profile, each behind its own name so each can be turned off alone.
    public static class EnginePack
    {
        public static void Install(Harmony harmony, Pack pack)
        {
            if (pack.Filter)
            {
                FilterMemo.Install(harmony);
            }
            if (pack.Authority)
            {
                harmony.Patch(AccessTools.Method(typeof(AAction), nameof(AAction.CheckAuthority)),
                    prefix: new HarmonyMethod(typeof(EnginePack), nameof(CheckAuthorityPrefix)));
            }
            if (pack.Invoke && !pack.Events)
            {
                throw new InvalidOperationException("pack part invoke needs events");
            }
            if (pack.Events)
            {
                EventPatches.Install(harmony, pack.Invoke);
            }
            if (pack.Rng)
            {
                RngPatches.Install(harmony);
            }
            if (pack.Presize)
            {
                harmony.Patch(AccessTools.Method(typeof(AbilityVariables), nameof(AbilityVariables.Copy), new[] { typeof(AbilityVariables), typeof(GameController) }),
                    prefix: new HarmonyMethod(typeof(EnginePack), nameof(AbilityVariablesCopyPrefix)));
            }
        }

        // CheckAuthority throws when an authority action runs on a controller
        // without authority. GameController.HasAuthority is
        // GameInstance.HasAuthority || !IsInValidNetworkState, so a
        // controller whose GameInstance has authority (single player or
        // server mode: the host's games and their copies, which share the
        // game instance) always passes and the check does nothing. It
        // skips the interface test and the second property chain. Any other
        // case, a null controller or game instance included, runs the
        // original, and with it the original's throw.
        private static bool CheckAuthorityPrefix(AAction __instance)
        {
            GameController controller = __instance.GameController;
            if (controller == null)
            {
                return true;
            }
            GameInstance instance = controller.GameInstance;
            return instance == null || !instance.HasAuthority;
        }

        // AbilityVariables.Copy appends a clone of every variable of the
        // source to an empty list. The list grows to the final count at
        // once instead of by doubling; nothing reads a list's capacity.
        private static void AbilityVariablesCopyPrefix(AbilityVariables __instance, AbilityVariables other)
        {
            int count = other.Variables.Count;
            if (__instance.Variables.Capacity < count)
            {
                __instance.Variables.Capacity = count;
            }
        }
    }
}
