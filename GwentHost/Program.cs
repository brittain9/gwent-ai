using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GwentAI.Sim;
using GwentGameplay;
using RedTools;

namespace GwentHost
{
    // The headless host: the client's own engine assemblies in a plain Mono
    // process, without Unity, Wine or rendering.
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --search-worker [--port P] [--memory-limit MB]
    //
    // serves searches for a game played elsewhere (the Practice client):
    // GwentAI.Practice.SearchWorker. --client is the game client's root,
    // whose engine and data the host uses; --loader is MelonLoader's managed
    // directory (Harmony and its dependencies). Port 0 (the default) picks a
    // free port, logged as "listening PORT". Over the memory limit the worker
    // exits with code 3.
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --validate-decks DECK.json...
    //
    // checks deck files (DeckSpec JSON) with the engine's own deck validator
    // and exits 1 if any is not legal.
    //
    //   mono -O=-float32 dist/host/GwentHost.exe --client DIR --loader DIR --self-test NAME
    //
    // runs one of the host's patch checks (SelfTest.cs: ushort-indexof,
    // engine-pack) and exits 0 if it passes.
    //
    // On .NET (GwentHost.Core):
    //
    //   dotnet dist/host-core/GwentHost.Core.dll --client DIR --loader DIR --search-worker [--port P] [--memory-limit MB]
    //
    // -O=-float32 is required. Unity's Mono evaluates float arithmetic in
    // double precision, and Mono 6 by default rounds every step to single
    // precision. The stock search's scores then differ in the last bits,
    // near-ties break the other way and the host plays different games.
    public static class Program
    {
        public static int Main(string[] args)
        {
            string client = null, loader = null, selfTest = null;
            System.Collections.Generic.List<string> decks = null;
            bool worker = false;
            int port = 0;
            long memoryLimitMB = 0;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--client": client = args[++i]; break;
                    case "--loader": loader = args[++i]; break;
                    case "--validate-decks": decks = new System.Collections.Generic.List<string>(); break;
                    case "--search-worker": worker = true; break;
                    case "--self-test": selfTest = args[++i]; break;
                    case "--port": port = int.Parse(args[++i]); break;
                    case "--memory-limit": memoryLimitMB = long.Parse(args[++i]); break;
                    default:
                        if (decks == null)
                        {
                            Console.Error.WriteLine($"GwentHost: unknown argument {args[i]}");
                            return 2;
                        }
                        decks.Add(args[i]);
                        break;
                }
            }
            if (client == null || loader == null || (decks == null && !worker && selfTest == null))
            {
                Console.Error.WriteLine("usage: mono -O=-float32 GwentHost.exe --client DIR --loader DIR (--search-worker | --validate-decks DECK.json... | --self-test NAME)");
                return 2;
            }
            int prepared = Host.Prepare(client, loader);
            if (prepared != 0)
            {
                return prepared;
            }
            if (selfTest != null)
            {
                return SelfTest.Run(selfTest, client);
            }
            if (worker)
            {
                return Host.RunWorker(client, port, memoryLimitMB);
            }
            return DeckCheck.Run(client, decks);
        }
    }

    // The host's start, its engine and its search worker.
    public static class Host
    {
#if !NETCOREAPP
        private static string[] s_AssemblyDirs;
#endif

        // Before any engine type is touched: checks the runtime and installs
        // the engine's assembly resolver (--client: the game client's root;
        // --loader: MelonLoader's managed directory, Harmony and its
        // dependencies). 0 when ready, else the exit code, with the reason on
        // standard error.
        public static int Prepare(string client, string loader)
        {
#if NETCOREAPP
            // Tiered PGO makes the JIT bypass Harmony patches (see
            // GwentHost.Core.csproj); the host's runtimeconfig turns it off.
            if (Environment.GetEnvironmentVariable("DOTNET_TieredPGO") == "1" || Environment.GetEnvironmentVariable("COMPlus_TieredPGO") == "1")
            {
                Console.Error.WriteLine("GwentHost: unset DOTNET_TieredPGO; tiered PGO lets the JIT bypass the host's patches");
                return 2;
            }
            // CoreCLR rounds every float step to single precision, as Mono
            // does without -O=-float32, and has no switch for it. 69 stock
            // games still matched Mono's action for action; a rare stock
            // difference stays possible. Harmony is the host's own
            // (HarmonyX); --loader is unused.
            EngineLoader.Install(Path.Combine(client, "Gwent_Data/Managed"));
            // SharpZipLib reads entry names in code page 437, which .NET
            // only has with the code pages provider.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
#else
            if (RoundsFloatSteps(16777216f, 1f))
            {
                Console.Error.WriteLine("GwentHost: run mono with -O=-float32 so float arithmetic matches the client");
                return 2;
            }
            // Engine assemblies load from the client and Harmony from the
            // loader. Mono's own class libraries always win: the client's
            // Unity mscorlib crashes a stock Mono runtime.
            s_AssemblyDirs = new[] { Path.Combine(client, "Gwent_Data/Managed"), loader };
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
#endif
            return 0;
        }

#if !NETCOREAPP
        // 2^24 + 1 is not a float: with single-precision steps the sum rounds
        // back to 2^24 and the difference is 0. Arguments keep it from being
        // folded at compile time.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool RoundsFloatSteps(float big, float one)
        {
            return big + one - big == 0f;
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            if (name == "mscorlib" || name == "System" || name.StartsWith("System."))
            {
                return null;
            }
            foreach (string directory in s_AssemblyDirs)
            {
                string path = Path.Combine(directory, name + ".dll");
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }
            return null;
        }
#endif

        // The engine as the host's games and workers run it: the client's
        // sort order, the virtual clock, the search's copy fixes, the host's
        // shortcuts, the engine speed patches
        // (on at level 2; GWENT_ENGINE_PATCHES=0 turns them off), and the
        // client's shared data. After Prepare.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static GameSharedData Engine(string client, out HarmonyLib.Harmony harmony, out RedLogger.IRedLogger logger)
        {
            GwentAI.Search.CardRoles.Initialize(client);
            harmony = new HarmonyLib.Harmony("gwent-host");
            GwentAI.Sim.UshortSafeIndexOf.Install(harmony);
            UnitySort.Install(harmony);
            StockAiPatches.Apply(harmony);
            ActionTrace.Install(harmony);
            MoveLog.Install(harmony);
            AnswerLog.Install(harmony);
            VirtualClock.Install(harmony);
            GwentAI.Search.SearchPatches.Install(harmony);
            HostPatches.Install(harmony);
            if (EnginePatches.Enabled)
            {
                EnginePatches.Install(harmony);
            }
            logger = new RedLogger.ConsoleLogger();
            if (Environment.GetEnvironmentVariable("GWENT_HOST_DEBUG_LOG") != "1")
            {
                logger = new QuietLogger(logger);
            }
            ClientAssets assets = new ClientAssets(client);
            // As the client does at startup: the stock search reads its
            // scoring weights from here.
            GwentSettings.Initialize(assets);
            return GameSharedData.Create(assets, logger);
        }

        // Serves searches for a game played elsewhere (GwentAI.Practice.SearchWorker)
        // until its connection closes; after Prepare. Port 0 picks a free port.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int RunWorker(string client, int port, long memoryLimitMB)
        {
            GameSharedData shared = Engine(client, out HarmonyLib.Harmony harmony, out RedLogger.IRedLogger logger);
            return GwentAI.Practice.SearchWorker.Run(shared, logger, port, memoryLimitMB,
                                                     message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}"));
        }
    }
}
