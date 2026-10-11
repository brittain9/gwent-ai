using System;
using GwentAI.Practice;
using GwentGameplay;
using MelonLoader;

[assembly: MelonInfo(typeof(GwentAI.GwentAIMod), "GwentAI", "0.1.0", "gwent-local", null)]
[assembly: MelonGame("CDProjektRED", "Gwent")]

namespace GwentAI
{
    // Entry point. Practice opponents play with the fair search
    // (GWENT_STOCK_AI=1 keeps the client's own AI), installed on the first
    // frame after the client has loaded its shared game data, before any
    // game. Code that drives Practice itself can turn the install off
    // (PracticeAI.Enabled) in its OnInitializeMelon.
    public class GwentAIMod : MelonMod
    {
        // Whether Practice was installed, and whether that was tried.
        private bool m_Practice;
        private bool m_PracticeTried;

        public override void OnInitializeMelon()
        {
            // Practice: the fair search, unless the stock AI is asked for.
            if (Environment.GetEnvironmentVariable("GWENT_STOCK_AI") == "1")
            {
                PracticeAI.Enabled = false;
            }
        }

        // Unity's Mono does not finish quitting while a thread is blocked
        // reading a socket: with search workers connected, the client hung
        // on quit until the workers exited.
        public override void OnApplicationQuit()
        {
            WorkerSearch.CloseAll();
        }

        public override void OnUpdate()
        {
            if (!m_PracticeTried && PracticeAI.Enabled && GameSharedData.Instance != null)
            {
                // Once: a failed install leaves its error in the log and the
                // client's own AI in Practice.
                m_PracticeTried = true;
                try
                {
                    PracticeAI.Install(HarmonyInstance, LoggerInstance.Msg);
                    m_Practice = true;
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error(ex);
                }
            }
            if (m_Practice)
            {
                PracticeAI.Update();
            }
        }
    }
}
