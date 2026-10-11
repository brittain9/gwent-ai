namespace GwentAI.Search
{
    // SplitMix64 (Steele, Lea and Flood 2014): uniform draws for chances the
    // search must realize as fitted (OpponentStyle). The engine's
    // MersenneTwisterRandom is strongly autocorrelated: NextDouble's
    // successive values sit close together (lag-1 correlation about 0.9, a
    // seed's mean of 4,000 draws 0.38-0.65) and Next(2) repeats its last
    // value 13% of the time, not 50%.
    public sealed class SplitMix64
    {
        private ulong m_State;

        public SplitMix64(int seed)
        {
            m_State = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL);
        }

        // The stream that starts from this state.
        public SplitMix64(ulong state)
        {
            m_State = state;
        }

        public ulong NextULong()
        {
            unchecked
            {
                ulong z = m_State += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        // In [0, 1), 53 bits.
        public double NextDouble()
        {
            return (NextULong() >> 11) * (1.0 / (1UL << 53));
        }

        // In [0, n), n > 0.
        public int Next(int n)
        {
            return (int)((NextULong() >> 1) % (ulong)n);
        }
    }

    // The search's generator behind SearchSettings.SearchRandom: a
    // System.Random in place of MersenneTwisterRandom, which the engine seeds
    // and draws badly (see SplitMix64). Bounded draws use Lemire's method, so
    // they carry no modulo bias. The seed is hashed first: SplitMix64 streams
    // from adjacent seeds are one another shifted by a draw, and the search
    // seeds are adjacent (they differ by an offset added to the match seed).
    public sealed class SplitMixRandom : System.Random
    {
        private readonly SplitMix64 m_Source;

        public SplitMixRandom(int seed)
        {
            m_Source = new SplitMix64(new SplitMix64(seed).NextULong());
        }

        protected override double Sample()
        {
            return NextDouble();
        }

        // In [0, int.MaxValue), as System.Random's.
        public override int Next()
        {
            return Next(int.MaxValue);
        }

        public override int Next(int maxValue)
        {
            if (maxValue < 0)
            {
                throw new System.ArgumentOutOfRangeException("maxValue");
            }
            return (int)Bounded((uint)maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue)
            {
                throw new System.ArgumentOutOfRangeException("minValue");
            }
            return (int)(minValue + (long)Bounded((uint)((long)maxValue - minValue)));
        }

        public override double NextDouble()
        {
            return m_Source.NextDouble();
        }

        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null)
            {
                throw new System.ArgumentNullException("buffer");
            }
            for (int index = 0; index < buffer.Length; index += 8)
            {
                ulong bits = m_Source.NextULong();
                for (int offset = 0; offset < 8 && index + offset < buffer.Length; offset++)
                {
                    buffer[index + offset] = (byte)(bits >> (8 * offset));
                }
            }
        }

        // Uniform in [0, bound) (Lemire 2019): the high half of a 32 x 32
        // bit product, redrawn only for the few low parts that would favour
        // some results. 0 for bound 0.
        private uint Bounded(uint bound)
        {
            ulong product = (m_Source.NextULong() >> 32) * bound;
            if ((uint)product < bound)
            {
                uint threshold = unchecked(0u - bound) % bound;
                while ((uint)product < threshold)
                {
                    product = (m_Source.NextULong() >> 32) * bound;
                }
            }
            return (uint)(product >> 32);
        }
    }
}
