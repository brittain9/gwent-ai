using System;
using System.IO;
using GwentGameplay;

namespace GwentAI.Learn
{
    // The policy net the AI embeds (eval/policy.pnet) has the card columns of
    // its template-input table zeroed: they are the client's card stats, so
    // the AI fills them from the client's own card table when it loads the
    // net. The filled bytes equal the fitted file's, byte for byte.
    //
    // A .pnet file (PolicyNet's reader): HeaderInts int32 (magic, file
    // version, encoding, options, V, zones, F, ...), V int32 template ids
    // (rows 0 and 1: unknown and hidden; rows from 2: the client's templates
    // by id), then the [V][F] float32 table whose last CardColumns columns
    // per row are: tier == 1, 2, 4, 8; type == 2, 4, 8;
    // faction == 1, 2, 4, 8, 16, 32; power / 10; armour / 10; token bit 8;
    // availability == 1; 0; 0 (TemplateColumns). Row 0 has 1 in the
    // next-to-last column, row 1 in the last. A change to those columns
    // needs a new file version here and in PolicyNet.
    public static class PolicyModel
    {
        public const int CardColumns = TemplateColumns.Count;
        private const int HeaderInts = 14;

        // A copy of the file's bytes with every row's card columns written
        // from the client's card table; every other byte is the input's.
        // Throws when the file is not a policy net this build reads, or names
        // a card the client does not have (the model and the client differ).
        public static byte[] Complete(byte[] stripped, GameSharedData shared)
        {
            if (shared == null)
            {
                throw new ArgumentNullException(nameof(shared), "The policy net's card columns come from the client's shared game data, which is not loaded yet");
            }
            int vocab, inputs, table;
            Layout(stripped, out vocab, out inputs, out table);
            byte[] full = (byte[])stripped.Clone();
            for (int v = 0; v < vocab; v++)
            {
                float[] columns;
                if (v < 2)
                {
                    columns = TemplateColumns.Marker(v);
                }
                else
                {
                    int id = BitConverter.ToInt32(stripped, 4 * (HeaderInts + v));
                    RuntimeCardTemplate runtime;
                    if (!shared.SharedRuntimeTemplates.TryGetValue(id, out runtime))
                    {
                        throw new InvalidDataException($"Policy net row {v} names card template {id}, which the client's card table " +
                                                       $"({shared.SharedRuntimeTemplates.Count} templates) does not have: the model was trained " +
                                                       $"on another card table ({vocab - 2} templates)");
                    }
                    columns = Columns(runtime.Template);
                }
                int at = table + 4 * (v * inputs + inputs - CardColumns);
                for (int c = 0; c < CardColumns; c++)
                {
                    // Through a float32 store, whatever precision the
                    // runtime keeps in registers.
                    Buffer.BlockCopy(BitConverter.GetBytes(columns[c]), 0, full, at + 4 * c, 4);
                }
            }
            return full;
        }

        // The card columns of one template, as the fitted files have them
        // (doubles cast to float32); the value net's static columns are the
        // same (ValueNet.Complete).
        internal static float[] Columns(CardTemplate t)
        {
            return TemplateColumns.Of((int)t.Tier, (int)t.Type, (int)t.FactionId, t.Power, t.Armor, (int)t.Tokens, (int)t.Availability);
        }

        // V, F and the template-input table's byte offset, after the checks
        // PolicyNet's reader makes on the header.
        private static void Layout(byte[] bytes, out int vocab, out int inputs, out int table)
        {
            if (bytes.Length < 4 * HeaderInts || BitConverter.ToInt32(bytes, 0) != PolicyNet.Magic)
            {
                throw new InvalidDataException("Not a policy net file");
            }
            int fileVersion = BitConverter.ToInt32(bytes, 4);
            int encoding = BitConverter.ToInt32(bytes, 8);
            int options = BitConverter.ToInt32(bytes, 12);
            if (fileVersion != PolicyNet.FileVersion || encoding != StateEncoder.Version || options != OptionEncoder.Version)
            {
                throw new InvalidDataException($"Policy net file version {fileVersion}, encoding {encoding}, options {options}; " +
                                               $"this build reads {PolicyNet.FileVersion}, encoding {StateEncoder.Version}, options {OptionEncoder.Version}");
            }
            vocab = BitConverter.ToInt32(bytes, 16);
            inputs = BitConverter.ToInt32(bytes, 24);
            table = 4 * (HeaderInts + vocab);
            if (vocab < 2 || inputs < CardColumns || (long)table + 4L * vocab * inputs > bytes.Length)
            {
                throw new InvalidDataException($"Policy net shapes: V={vocab}, F={inputs}, {bytes.Length} bytes");
            }
        }
    }
}
