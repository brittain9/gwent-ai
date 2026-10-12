// Loads platform/windows/session_net.c into the client on Windows before the
// game starts, so that the GOG names resolve to the local server, the Galaxy
// SDK finds its service, this install's certificate is accepted and the game's
// listeners stay on loopback, inside this process only (no hosts file, no
// Windows service, no certificate store, no firewall rule).
// scripts/windows_session.py sets the variables below; without
// GWENT_SESSION_NET_DLL the plugin does nothing, so a client started any other
// way is unchanged.
//
// With GWENT_SESSION_NET_DLL set, the plugin fails closed: if the library
// cannot be loaded or installed, it logs why and ends the game process (exit
// code 0x5E55), because a client that ran unrouted would talk to GOG's hosts
// instead of the local server.
//
//   GWENT_SESSION_NET_DLL       the library (dist\session_net.dll)
//   GWENT_HOSTS_FILE            the routed names (hosts-file syntax)
//   GWENT_SESSION_CERT_SHA256   this install's certificate (read by the library)
//   GWENT_SESSION_NET_LOG       a log file (optional)
using System;
using System.IO;
using System.Runtime.InteropServices;
using MelonLoader;

[assembly: MelonInfo(typeof(GwentSessionNet.SessionNetPlugin), "GwentSessionNet", "1.1.0", "gwent-ai")]
[assembly: MelonGame("CDProjektRED", "Gwent")]

namespace GwentSessionNet
{
    public class SessionNetPlugin : MelonPlugin
    {
        // scripts/windows_session.py reports this exit code as a routing failure.
        public const uint FailedExitCode = 0x5E55;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int Install(string hostsFile, string directory, string preload, string log);

        private static Install install;
        private static bool unset;
        private static string hosts, directory, preload, log;

        // Before the game's own code runs; again at each later stage, for any
        // client library loaded in between (a module already routed is skipped).
        public override void OnPreInitialization() => Run("pre-initialization");

        public override void OnApplicationEarlyStart() => Run("early start");

        public override void OnApplicationStarted() => Run("started");

        private void Run(string stage)
        {
            if (unset) return;
            string library = Environment.GetEnvironmentVariable("GWENT_SESSION_NET_DLL");
            if (string.IsNullOrEmpty(library))
            {
                LoggerInstance.Msg("GWENT_SESSION_NET_DLL unset; the client's names are not routed");
                unset = true;
                return;
            }
            try
            {
                if (install == null)
                    Load(library);
                int patched = install(hosts, directory, preload, log);
                if (patched < 0)
                    Fail($"session_net_install failed ({patched}); see {log}");
                LoggerInstance.Msg($"{stage}: routed {patched} client imports to the local server");
            }
            catch (Exception error)
            {
                Fail(error.ToString());
            }
        }

        // The session asked for routing and cannot have it: the game must not
        // run against GOG's hosts.
        private void Fail(string reason)
        {
            LoggerInstance.Error($"session_net: {reason}. Ending the game so that it does not run unrouted.");
            TerminateProcess(GetCurrentProcess(), FailedExitCode);
        }

        private void Load(string library)
        {
            hosts = Environment.GetEnvironmentVariable("GWENT_HOSTS_FILE");
            log = Environment.GetEnvironmentVariable("GWENT_SESSION_NET_LOG") ?? "";
            // The client folder: Gwent.exe's, which holds MelonLoader too.
            directory = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            // Galaxy64.dll would load only when the game first calls it.
            preload = Path.Combine(Path.Combine(Path.Combine(directory, "Gwent_Data"), "Plugins"), "Galaxy64.dll");
            IntPtr module = LoadLibraryW(library);
            if (module == IntPtr.Zero)
                Fail($"cannot load {library} (error {Marshal.GetLastWin32Error()})");
            IntPtr entry = GetProcAddress(module, "session_net_install");
            if (entry == IntPtr.Zero)
                Fail($"{library} has no session_net_install");
            install = (Install)Marshal.GetDelegateForFunctionPointer(entry, typeof(Install));
        }
    }
}
