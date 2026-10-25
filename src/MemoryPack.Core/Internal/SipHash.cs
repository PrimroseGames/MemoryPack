/*
   SipHash-1-3, after the reference C implementation (https://github.com/veorq/SipHash).

   Copyright (c) 2012-2022 Jean-Philippe Aumasson <jeanphilippe.aumasson@gmail.com>
   Copyright (c) 2012-2014 Daniel J. Bernstein <djb@cr.yp.to>

   To the extent possible under law, the author(s) have dedicated all copyright
   and related and neighboring rights to this software to the public domain
   worldwide. This software is distributed without any warranty.
   <http://creativecommons.org/publicdomain/zero/1.0/>
 */

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack.Internal;

// One compression round and three finalization rounds: the variant hash tables in several language runtimes use,
// about half the cost of SipHash-2-4, which matters because this runs per key inserted while reading.
internal static class SipHash
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong ROTL(ulong x, int b) => (x << b) | (x >> (64 - b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void SIPROUND(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        unchecked
        {
            v0 += v1;
            v1 = ROTL(v1, 13);
            v1 ^= v0;
            v0 = ROTL(v0, 32);
            v2 += v3;
            v3 = ROTL(v3, 16);
            v3 ^= v2;
            v0 += v3;
            v3 = ROTL(v3, 21);
            v3 ^= v0;
            v2 += v1;
            v1 = ROTL(v1, 17);
            v1 ^= v2;
            v2 = ROTL(v2, 32);
        }
    }

    public static ulong Hash64(ReadOnlySpan<byte> data, ulong k0, ulong k1)
    {
        var v0 = 0x736f6d6570736575UL ^ k0;
        var v1 = 0x646f72616e646f6dUL ^ k1;
        var v2 = 0x6c7967656e657261UL ^ k0;
        var v3 = 0x7465646279746573UL ^ k1;

        var length = data.Length;
        var end = length & ~7;
        var b = (ulong)length << 56;

        ref var r0 = ref MemoryMarshal.GetReference(data);
        for (var i = 0; i < end; i += 8)
        {
            var m = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r0, i));
            if (!BitConverter.IsLittleEndian)
            {
                m = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(m);
            }
            v3 ^= m;
            SIPROUND(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        switch (length & 7)
        {
            case 7: b |= (ulong)Unsafe.Add(ref r0, end + 6) << 48; goto case 6;
            case 6: b |= (ulong)Unsafe.Add(ref r0, end + 5) << 40; goto case 5;
            case 5: b |= (ulong)Unsafe.Add(ref r0, end + 4) << 32; goto case 4;
            case 4: b |= (ulong)Unsafe.Add(ref r0, end + 3) << 24; goto case 3;
            case 3: b |= (ulong)Unsafe.Add(ref r0, end + 2) << 16; goto case 2;
            case 2: b |= (ulong)Unsafe.Add(ref r0, end + 1) << 8; goto case 1;
            case 1: b |= Unsafe.Add(ref r0, end); break;
            case 0: break;
        }

        return Finish(b, v0, v1, v2, v3);
    }

    // Hash64 over the little-endian bytes of a key of at most 8 bytes, without a span. A key under 8 bytes fits
    // the length-tagged final block (its unused upper bytes masked off); an 8-byte key is one block and an empty tail.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash64Fixed(ulong littleEndianBits, int length, ulong k0, ulong k1)
    {
        var v0 = 0x736f6d6570736575UL ^ k0;
        var v1 = 0x646f72616e646f6dUL ^ k1;
        var v2 = 0x6c7967656e657261UL ^ k0;
        var v3 = 0x7465646279746573UL ^ k1;

        ulong b;
        if (length == 8)
        {
            v3 ^= littleEndianBits;
            SIPROUND(ref v0, ref v1, ref v2, ref v3);
            v0 ^= littleEndianBits;
            b = 8UL << 56;
        }
        else
        {
            var payloadMask = (1UL << (length * 8)) - 1UL;
            b = ((ulong)length << 56) | (littleEndianBits & payloadMask);
        }

        return Finish(b, v0, v1, v2, v3);
    }

    // Hash64 over a 16-byte key: two blocks and an empty tail.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Hash64Fixed16(ulong bits0, ulong bits1, ulong k0, ulong k1)
    {
        var v0 = 0x736f6d6570736575UL ^ k0;
        var v1 = 0x646f72616e646f6dUL ^ k1;
        var v2 = 0x6c7967656e657261UL ^ k0;
        var v3 = 0x7465646279746573UL ^ k1;

        v3 ^= bits0;
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        v0 ^= bits0;

        v3 ^= bits1;
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        v0 ^= bits1;

        return Finish(16UL << 56, v0, v1, v2, v3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Finish(ulong b, ulong v0, ulong v1, ulong v2, ulong v3)
    {
        v3 ^= b;
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xff;
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        SIPROUND(ref v0, ref v1, ref v2, ref v3);
        return v0 ^ v1 ^ v2 ^ v3;
    }
}
