using System;
using System.Collections.Generic;
using System.IO;

namespace GwentAI.Learn
{
    // The value net the AI embeds (eval/value.vnet) has the static columns
    // of its template table zeroed: they are the client's card stats, so the
    // AI fills them from the client's own card table when it loads the net
    // (ValueNet.Complete). The filled bytes equal the fitted file's, byte for
    // byte.
    //
    // A .vnet file (ValueNet's reader), little-endian. Version 1, one net:
    // 12 int32 (magic, file version, encoding, V, zones, F, hidden width,
    // dynamic features, globals, H1, H2, outputs), V int32 template ids (rows
    // 0 and 1: unknown and hidden; rows from 2: the client's templates by
    // id), then the [V][F] float32 template table whose last StaticColumns
    // columns per row are TemplateColumns'; the columns before them are the
    // learned embedding (F - StaticColumns of them). The header carries
    // their sum F only. Version 2, a full-state ensemble: 6 int32 (magic,
    // file version, encoding, mode, member count, pessimism), then per
    // member an int32 length and a version 1 file; each member is filled.
    //
    // Free of the engine: the card table arrives as a lookup from template
    // id to columns.
    public static class ValueModel
    {
        public const int Magic = 0x314E5647; // "GVN1"
        public const int FileVersion = 1;
        public const int FullStateVersion = 2;
        public const int StaticColumns = TemplateColumns.Count;
        private const int MemberHeaderInts = 12;
        private const int FullStateHeaderInts = 6;

        // A copy of the file's bytes with every row's static columns written
        // from the card table; every other byte is the input's. columnsOf
        // returns the TemplateColumns of a template id, or null when the card
        // table does not have it. Throws when the file is not a value net
        // this build reads, or names a card the card table does not have (the
        // model and the client differ).
        public static byte[] Complete(byte[] stripped, Func<int, float[]> columnsOf)
        {
            if (columnsOf == null)
            {
                throw new ArgumentNullException(nameof(columnsOf), "The value net's static columns come from the client's card table, which is not loaded yet");
            }
            Dictionary<int, float[]> known = new Dictionary<int, float[]>();
            return Rewrite(stripped, (row, id, vocab) =>
            {
                if (row < 2)
                {
                    return TemplateColumns.Marker(row);
                }
                float[] columns;
                if (!known.TryGetValue(id, out columns))
                {
                    columns = columnsOf(id);
                    if (columns == null)
                    {
                        throw new InvalidDataException($"Value net row {row} names card template {id}, which the client's card table " +
                                                       $"does not have: the model was trained on another card table ({vocab - 2} templates)");
                    }
                    known[id] = columns;
                }
                return columns;
            });
        }

        // A copy of the file's bytes with every row's static columns zeroed,
        // as scripts/vnet_strip.py writes it: the file the AI embeds.
        public static byte[] Strip(byte[] full)
        {
            float[] zero = new float[StaticColumns];
            return Rewrite(full, (row, id, vocab) => zero);
        }

        // A copy of the file's bytes with each row's static columns replaced
        // by columnsFor(row, template id, V) (float32 stores), in every net
        // of the file.
        private static byte[] Rewrite(byte[] source, Func<int, int, int, float[]> columnsFor)
        {
            byte[] copy = (byte[])source.Clone();
            List<int> starts, lengths;
            Members(source, out starts, out lengths);
            for (int m = 0; m < starts.Count; m++)
            {
                int vocab, inputs, ids, table;
                Layout(source, starts[m], lengths[m], out vocab, out inputs, out ids, out table);
                for (int v = 0; v < vocab; v++)
                {
                    float[] columns = columnsFor(v, BitConverter.ToInt32(source, ids + 4 * v), vocab);
                    int at = table + 4 * (v * inputs + inputs - StaticColumns);
                    for (int c = 0; c < StaticColumns; c++)
                    {
                        // Through a float32 store, whatever precision the
                        // runtime keeps in registers.
                        Buffer.BlockCopy(BitConverter.GetBytes(columns[c]), 0, copy, at + 4 * c, 4);
                    }
                }
            }
            return copy;
        }

        // The version 1 nets inside the file: the file itself, or a
        // full-state file's members. The checks ValueNet's reader makes on
        // the container.
        private static void Members(byte[] bytes, out List<int> starts, out List<int> lengths)
        {
            starts = new List<int>();
            lengths = new List<int>();
            if (bytes.Length < 8 || BitConverter.ToInt32(bytes, 0) != Magic)
            {
                throw new InvalidDataException("Not a value net file");
            }
            int version = BitConverter.ToInt32(bytes, 4);
            if (version == FileVersion)
            {
                starts.Add(0);
                lengths.Add(bytes.Length);
                return;
            }
            if (version != FullStateVersion)
            {
                throw new InvalidDataException($"Value net file version {version}; this build reads {FileVersion} and {FullStateVersion}");
            }
            int count = bytes.Length < 4 * FullStateHeaderInts ? 0 : BitConverter.ToInt32(bytes, 16);
            if (count < 1)
            {
                throw new InvalidDataException("Full-state value net: no members");
            }
            int at = 4 * FullStateHeaderInts;
            for (int i = 0; i < count; i++)
            {
                int length = at + 4 > bytes.Length ? -1 : BitConverter.ToInt32(bytes, at);
                at += 4;
                if (length < 0 || at + length > bytes.Length)
                {
                    throw new InvalidDataException("Full-state value net: a member is cut short");
                }
                starts.Add(at);
                lengths.Add(length);
                at += length;
            }
            if (at != bytes.Length)
            {
                throw new InvalidDataException("Value net file has trailing data");
            }
        }

        // V, F, the byte offsets of the template ids and of the template
        // table, for the version 1 net at `start`.
        private static void Layout(byte[] bytes, int start, int length, out int vocab, out int inputs, out int ids, out int table)
        {
            if (length < 4 * MemberHeaderInts || BitConverter.ToInt32(bytes, start) != Magic || BitConverter.ToInt32(bytes, start + 4) != FileVersion)
            {
                throw new InvalidDataException("Value net: a member is not a version 1 net");
            }
            vocab = BitConverter.ToInt32(bytes, start + 12);
            inputs = BitConverter.ToInt32(bytes, start + 20);
            ids = start + 4 * MemberHeaderInts;
            table = ids + 4 * vocab;
            if (vocab < 2 || inputs < StaticColumns || (long)table + 4L * vocab * inputs > start + length)
            {
                throw new InvalidDataException($"Value net shapes: V={vocab}, F={inputs}, {length} bytes");
            }
        }
    }
}
