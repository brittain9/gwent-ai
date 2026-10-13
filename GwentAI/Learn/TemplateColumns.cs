using System;

namespace GwentAI.Learn
{
    // The static columns of a learned net's template table: the card stats
    // the fitted files carry per template (PolicyModel for the policy net,
    // ValueModel for the value net). Both nets use the same 19 columns:
    // tier == 1, 2, 4, 8; type == 2, 4, 8; faction == 1, 2, 4, 8, 16, 32;
    // power / 10; armour / 10; token bit 8; availability == 1; 0; 0. Row 0
    // (a template outside the vocabulary) has 1 in the next-to-last column,
    // row 1 (a hidden card) in the last. A change to these columns needs a
    // new file version in both nets.
    //
    // Free of the engine, so a test can fill columns without a client: the
    // caller passes the card's numbers.
    public static class TemplateColumns
    {
        public const int Count = 19;
        private static readonly int[] Tiers = { 1, 2, 4, 8 };
        private static readonly int[] Types = { 2, 4, 8 };
        private static readonly int[] Factions = { 1, 2, 4, 8, 16, 32 };

        // The columns of vocabulary row 0 (unknown) or 1 (hidden).
        public static float[] Marker(int row)
        {
            if (row < 0 || row > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(row), "Only rows 0 and 1 are markers");
            }
            float[] columns = new float[Count];
            columns[Count - 2 + row] = 1f;
            return columns;
        }

        // The columns of one card template, as the fitted files have them
        // (doubles cast to float32). tier, type, faction, tokens and
        // availability are the client's enum values as integers.
        public static float[] Of(int tier, int type, int faction, double power, double armor, int tokens, int availability)
        {
            float[] columns = new float[Count];
            int c = 0;
            foreach (int x in Tiers)
            {
                columns[c++] = tier == x ? 1f : 0f;
            }
            foreach (int x in Types)
            {
                columns[c++] = type == x ? 1f : 0f;
            }
            foreach (int x in Factions)
            {
                columns[c++] = faction == x ? 1f : 0f;
            }
            columns[c++] = (float)(power / 10.0);
            columns[c++] = (float)(armor / 10.0);
            columns[c++] = (tokens & 8) != 0 ? 1f : 0f;
            columns[c++] = availability == 1 ? 1f : 0f;
            columns[c++] = 0f;
            columns[c] = 0f;
            return columns;
        }
    }
}
