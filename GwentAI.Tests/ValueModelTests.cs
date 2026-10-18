using System;
using System.Collections.Generic;
using System.IO;
using GwentAI.Learn;
using Xunit;

namespace GwentAI.Tests
{
    // The value net the AI embeds has its static template columns zeroed and
    // filled from the card table at load (ValueModel). The client is not
    // here, so the card table is read back from the fitted file's own static
    // columns through the layout TemplateColumns documents: the fill must
    // reproduce the file byte for byte, and a card the table lacks must be
    // refused. The file's bytes decide the outputs of the net, so equal bytes
    // are equal outputs.
    //
    // The fitted nets (data/eval) are optional fixtures: the tests that need
    // one skip when it is absent, and the ones on the AI's own net
    // (data/model/fta.vnet) always run.
    public class ValueModelTests
    {
        private const int HeaderInts = 12;

        // A test that needs a fitted net (data/eval); it skips without one.
        private sealed class FittedNetFactAttribute : FactAttribute
        {
            public FittedNetFactAttribute(string name = "fta.vnet")
            {
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "model-fixtures", name)))
                {
                    Skip = $"{name}, an optional fitted net, is absent";
                }
            }
        }

        private static byte[] Fixture(string name)
        {
            return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "model-fixtures", name));
        }

        private static int Int(byte[] bytes, int at)
        {
            return BitConverter.ToInt32(bytes, at);
        }

        // V, F, the offset of the template ids and of the template table of
        // the version 1 file.
        private static void Layout(byte[] bytes, out int vocab, out int inputs, out int ids, out int table)
        {
            Assert.Equal(ValueModel.Magic, Int(bytes, 0));
            Assert.Equal(ValueModel.FileVersion, Int(bytes, 4));
            vocab = Int(bytes, 12);
            inputs = Int(bytes, 20);
            ids = 4 * HeaderInts;
            table = ids + 4 * vocab;
        }

        private static float[] Row(byte[] bytes, int table, int inputs, int row)
        {
            float[] columns = new float[ValueModel.StaticColumns];
            for (int c = 0; c < columns.Length; c++)
            {
                columns[c] = BitConverter.ToSingle(bytes, table + 4 * (row * inputs + inputs - columns.Length + c));
            }
            return columns;
        }

        private static int Which(float[] columns, int from, int[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (columns[from + i] == 1f)
                {
                    return values[i];
                }
            }
            return 0;
        }

        // The card table the fitted file implies: per template id, the stats
        // its static columns spell (TemplateColumns' order).
        private static Func<int, float[]> CardTable(byte[] full, out HashSet<int> templates)
        {
            int vocab, inputs, ids, table;
            Layout(full, out vocab, out inputs, out ids, out table);
            Dictionary<int, float[]> byId = new Dictionary<int, float[]>();
            for (int v = 2; v < vocab; v++)
            {
                float[] columns = Row(full, table, inputs, v);
                float[] spelled = TemplateColumns.Of(
                    Which(columns, 0, new[] { 1, 2, 4, 8 }),
                    Which(columns, 4, new[] { 2, 4, 8 }),
                    Which(columns, 7, new[] { 1, 2, 4, 8, 16, 32 }),
                    Math.Round(columns[13] * 10.0),
                    Math.Round(columns[14] * 10.0),
                    columns[15] == 1f ? 8 : 0,
                    columns[16] == 1f ? 1 : 0);
                Assert.Equal(columns, spelled);
                byId[Int(full, ids + 4 * v)] = spelled;
            }
            templates = new HashSet<int>(byId.Keys);
            return id => byId.TryGetValue(id, out float[] columns) ? columns : null;
        }

        [FittedNetFact]
        public void TheStaticColumnsAreTheLastNineteenOfEveryRowAndNothingElseChanges()
        {
            byte[] full = Fixture("fta.vnet");
            byte[] stripped = ValueModel.Strip(full);
            int vocab, inputs, ids, table;
            Layout(full, out vocab, out inputs, out ids, out table);
            Assert.Equal(full.Length, stripped.Length);
            int changed = 0;
            for (int i = 0; i < full.Length; i++)
            {
                if (full[i] == stripped[i])
                {
                    continue;
                }
                changed++;
                Assert.True(i >= table && i < table + 4 * vocab * inputs, $"byte {i} is outside the template table");
                int column = (i - table) / 4 % inputs;
                Assert.True(column >= inputs - ValueModel.StaticColumns, $"byte {i} is in embedding column {column}");
            }
            Assert.True(changed > 0);
            for (int v = 0; v < vocab; v++)
            {
                Assert.All(Row(stripped, table, inputs, v), x => Assert.Equal(0f, x));
            }
        }

        [FittedNetFact]
        public void TheStrippedCopyTheBuildToolWritesIsTheOneTheLoaderReads()
        {
            // data/model/fta.vnet is data/eval/fta.vnet with its static columns zeroed.
            Assert.Equal(Fixture("shipped-fta.vnet"), ValueModel.Strip(Fixture("fta.vnet")));
        }

        [FittedNetFact]
        public void FillingAStrippedNetGivesBackTheFittedFile()
        {
            byte[] full = Fixture("fta.vnet");
            HashSet<int> templates;
            Func<int, float[]> table = CardTable(full, out templates);
            byte[] filled = ValueModel.Complete(ValueModel.Strip(full), table);
            Assert.Equal(full, filled);
            Assert.Equal(full, ValueModel.Complete(Fixture("shipped-fta.vnet"), table));
        }

        [FittedNetFact]
        public void FillingAFileThatHasItsColumnsChangesNothing()
        {
            byte[] full = Fixture("fta.vnet");
            HashSet<int> templates;
            Assert.Equal(full, ValueModel.Complete(full, CardTable(full, out templates)));
        }

        [FittedNetFact("fs2.vnet")]
        public void AFullStateFileIsFilledMemberByMember()
        {
            byte[] full = Fixture("fs2.vnet");
            Assert.Equal(ValueModel.FullStateVersion, Int(full, 4));
            int members = Int(full, 16);
            Assert.True(members > 1);
            // A member is a version 1 file behind an int32 length.
            int length = Int(full, 24);
            byte[] first = new byte[length];
            Array.Copy(full, 28, first, 0, length);
            HashSet<int> templates;
            Func<int, float[]> table = CardTable(first, out templates);
            byte[] stripped = ValueModel.Strip(full);
            Assert.NotEqual(full, stripped);
            Assert.Equal(full, ValueModel.Complete(stripped, table));
        }

        [Fact]
        public void TheShippedNetHasNoStaticColumns()
        {
            byte[] shipped = Fixture("shipped-fta.vnet");
            int vocab, inputs, ids, table;
            Layout(shipped, out vocab, out inputs, out ids, out table);
            Assert.True(inputs > ValueModel.StaticColumns);
            for (int v = 0; v < vocab; v++)
            {
                Assert.All(Row(shipped, table, inputs, v), x => Assert.Equal(0f, x));
            }
            Assert.Equal(shipped, ValueModel.Strip(shipped));
        }

        [Fact]
        public void TheShippedNetIsFilledFromAnyCardTableThatHasItsTemplates()
        {
            byte[] shipped = Fixture("shipped-fta.vnet");
            int vocab, inputs, ids, table;
            Layout(shipped, out vocab, out inputs, out ids, out table);
            float[] card = TemplateColumns.Of(tier: 1, type: 2, faction: 4, power: 60, armor: 0, tokens: 0, availability: 1);
            byte[] filled = ValueModel.Complete(shipped, id => card);
            for (int v = 0; v < vocab; v++)
            {
                Assert.Equal(v < 2 ? TemplateColumns.Marker(v) : card, Row(filled, table, inputs, v));
            }
            // Nothing outside the static columns moved.
            Assert.Equal(shipped, ValueModel.Strip(filled));
        }

        [Fact]
        public void ACardTheTableLacksIsRefused()
        {
            byte[] shipped = Fixture("shipped-fta.vnet");
            int vocab, inputs, ids, table;
            Layout(shipped, out vocab, out inputs, out ids, out table);
            int dropped = Int(shipped, ids + 4 * 5);
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => ValueModel.Complete(shipped, id => id == dropped ? null : new float[ValueModel.StaticColumns]));
            Assert.Contains(dropped.ToString(), error.Message);
        }

        [Fact]
        public void AFileThatIsNotAValueNetIsRefused()
        {
            Func<int, float[]> any = id => new float[ValueModel.StaticColumns];
            Assert.Throws<InvalidDataException>(() => ValueModel.Complete(new byte[64], any));
            byte[] truncated = Fixture("shipped-fta.vnet");
            Array.Resize(ref truncated, 4000);
            Assert.Throws<InvalidDataException>(() => ValueModel.Complete(truncated, any));
            byte[] newer = Fixture("shipped-fta.vnet");
            newer[4] = 9;
            Assert.Throws<InvalidDataException>(() => ValueModel.Complete(newer, any));
        }

        [Fact]
        public void ACardsColumnsFollowTheDocumentedOrder()
        {
            // Tier 2, type 4, faction 8, power 75, armour 5, token bit 8, available.
            float[] columns = TemplateColumns.Of(tier: 2, type: 4, faction: 8, power: 75, armor: 5, tokens: 8 | 1, availability: 1);
            float[] expected =
            {
                0, 1, 0, 0,
                0, 1, 0,
                0, 0, 0, 1, 0, 0,
                7.5f, 0.5f, 1, 1, 0, 0,
            };
            Assert.Equal(expected, columns);
            Assert.Equal(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0 }, TemplateColumns.Marker(0));
            Assert.Equal(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, TemplateColumns.Marker(1));
        }
    }
}
