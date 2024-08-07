using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace MeshWeaver.Collections
{
    public class XxHashBuilder
    {
        private struct HashState
        {
            public uint Seed;
            public uint V1;
            public uint V2;
            public uint V3;
            public uint V4;
            public int TailLength;
            public int TotalLength;
            public uint[] Tail;
        };

        private const uint Prime1 = 2654435761U;
        private const uint Prime2 = 2246822519U;
        private const uint Prime3 = 3266489917U;
        private const uint Prime4 = 668265263U;
        private const uint Prime5 = 374761393U;

        private HashState state;

        public XxHashBuilder(uint seed = 0)
        {
            state = new HashState
                    {
                        V1 = seed + Prime1 + Prime2,
                        V2 = seed + Prime2,
                        V3 = seed + 0,
                        V4 = seed - Prime1,
                        Seed = seed,
                        Tail = new uint[3]
                    };
        }

        public XxHashBuilder Add(int input)
        {
            ++state.TotalLength;

            if (state.TailLength < 3)
            {
                state.Tail[state.TailLength] = (uint)input;
                ++state.TailLength;
            }
            else
            {
                state.TailLength = 0;

                state.V1 += state.Tail[0]*Prime2;
                state.V2 += state.Tail[1]*Prime2;
                state.V3 += state.Tail[2]*Prime2;
                state.V4 += (uint)input*Prime2;

                state.V1 = state.V1.RotateLeft(13);
                state.V2 = state.V2.RotateLeft(13);
                state.V3 = state.V3.RotateLeft(13);
                state.V4 = state.V4.RotateLeft(13);

                state.V1 *= Prime1;
                state.V2 *= Prime1;
                state.V3 *= Prime1;
                state.V4 *= Prime1;
            }

            return this;
        }

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode")]
        public override int GetHashCode()
        {
            uint h32;

            if (state.TotalLength >= 4)
            {
                h32 = state.V1.RotateLeft(1)
                      + state.V2.RotateLeft(7)
                      + state.V3.RotateLeft(12)
                      + state.V4.RotateLeft(18);
            }
            else
            {
                h32 = state.Seed + Prime5;
            }

            h32 += (uint)state.TotalLength*sizeof(uint);

            int index = 0;
            while (index < state.TailLength)
            {
                h32 += state.Tail[index]*Prime3;
                h32 = h32.RotateLeft(17)*Prime4;
                index += 1;
            }

            h32 ^= h32 >> 15;
            h32 *= Prime2;
            h32 ^= h32 >> 13;
            h32 *= Prime3;
            h32 ^= h32 >> 16;

            return (int)h32;
        }
    }

    internal static class XxHashExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint RotateLeft(this uint x, byte r)
        {
            return (x << r) | (x >> (32 - r));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int RotateLeft(this int x, byte r)
        {
            return (x << r) | (x >> (32 - r));
        }
    }
}