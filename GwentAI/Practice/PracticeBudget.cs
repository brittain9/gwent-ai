using System;
using System.IO;
using GwentAI.Learn;
using GwentAI.Search;
using GwentGameplay;

namespace GwentAI.Practice
{
    // Practice's time budget on a search, and the policy prior it plays with.
    public static class PracticeBudget
    {
        // thinkMs per decision, settled decisions stopping at half of it and
        // open ones extending to DefaultPlay.ThinkExtend times it (extend
        // replaces that when above 1), a mulligan
        // DefaultPlay.ThinkMulliganScale plays' (mulliganScale replaces
        // that when above 0).
        public static void Apply(SearchSettings settings, int thinkMs, double extend, double mulliganScale)
        {
            settings.TimeBudgetMs = thinkMs;
            Extension(settings, extend, mulliganScale);
        }

        // The extension and the mulligan scale of Apply alone, for a budget
        // set some other way.
        public static void Extension(SearchSettings settings, double extend, double mulliganScale)
        {
            settings.Extend = extend > 1 ? extend : DefaultPlay.ThinkExtend;
            settings.MulliganScale = mulliganScale > 0 ? mulliganScale : DefaultPlay.ThinkMulliganScale;
        }

        // The policy prior the AI plays with when none is named
        // (DefaultPlay.PolicyNet): the net embedded in the mod as
        // eval/policy.pnet, with its card columns filled from the client's
        // card table (PolicyModel), so after the client's shared game data is
        // loaded. A fresh net per call: a net keeps scratch state, so a caller
        // shares one only where it shares a seat.
        public static PolicyNet LoadDefaultPrior()
        {
            if (GameSharedData.Instance == null)
            {
                throw new InvalidOperationException("The default policy prior is read after the client's shared game data is loaded (GameSharedData.Instance)");
            }
            return PolicyNet.FromBytes(PolicyModel.Complete(DefaultPriorBytes(), GameSharedData.Instance), DefaultPlay.PolicyNet);
        }

        // The embedded net's bytes as the mod carries them (card columns zeroed).
        public static byte[] DefaultPriorBytes()
        {
            return EmbeddedBytes("eval/policy.pnet");
        }

        // The embedded value net's bytes as the mod carries them (static
        // columns zeroed).
        public static byte[] DefaultValueNetBytes()
        {
            return EmbeddedBytes("eval/value.vnet");
        }

        private static byte[] EmbeddedBytes(string name)
        {
            using (Stream stream = typeof(PracticeBudget).Assembly.GetManifestResourceStream(name))
            using (MemoryStream copy = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copy.Write(buffer, 0, read);
                }
                return copy.ToArray();
            }
        }

        // The evaluator with the embedded value net DefaultPlay.ValueNet (eval/value.vnet) from
        // round DefaultPlay.NetFromRound on (0: at every cut) and `linear` before it. A fresh net
        // per call: a net keeps scratch state, so a caller shares one only where it shares a
        // seat. loaded: told of the net. The net's static columns are filled
        // from the client's card table (ValueNet.Complete), so after the
        // client's shared game data is loaded.
        public static IRoundEvaluator WithDefaultValueNet(IRoundEvaluator linear, System.Action<ValueNet> loaded)
        {
            if (GameSharedData.Instance == null)
            {
                throw new InvalidOperationException("The default value net is read after the client's shared game data is loaded (GameSharedData.Instance)");
            }
            ValueNet net = ValueNet.FromBytes(ValueNet.Complete(DefaultValueNetBytes(), GameSharedData.Instance), DefaultPlay.ValueNet);
            loaded(net);
            return DefaultPlay.NetFromRound > 0 ? (IRoundEvaluator)new RoundGateEvaluator(linear, net, DefaultPlay.NetFromRound) : net;
        }
    }
}
