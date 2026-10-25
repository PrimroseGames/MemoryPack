using MemoryPack.Internal;
using System.Buffers;
using System.Collections;

namespace MemoryPack.Formatters;

[Preserve]
public sealed class BitArrayFormatter : MemoryPackFormatter<BitArray>
{
    // Serialized as [length, int[] of the bits]. The array holds exactly the ints the length needs, with the bits
    // past the length clear. Only the public API is used: the runtime's own array is sized by its version (.NET 11
    // pads it), so writing it out made the bytes depend on the runtime.

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref BitArray? value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }

        var length = value.Length;
        var count = (int)((length + 31L) / 32);
        var ints = ArrayPool<int>.Shared.Rent(Math.Max(count, 1));
        try
        {
            value.CopyTo(ints, 0);
            if ((length & 31) != 0)
            {
                ints[count - 1] &= (1 << (length & 31)) - 1;
            }

            writer.WriteUnmanagedWithObjectHeader(2, length);
            writer.WriteUnmanagedSpan(ints.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(ints);
        }
    }

    [Preserve]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref BitArray? value)
    {
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = null;
            return;
        }

        if (count != 2) MemoryPackSerializationException.ThrowInvalidPropertyCount(2, count);

        reader.ReadUnmanaged(out int length);

        // The array is read before anything is sized by length, and has to hold length bits. It can be longer:
        // older writers wrote the runtime's own array, which may be padded or kept from a longer length.
        int[]? array = null;
        reader.ReadUnmanagedArray(ref array);
        if (array == null || length < 0 || (length + 31L) / 32 > array.Length)
        {
            MemoryPackSerializationException.ThrowInvalidLength(length);
        }

        var needed = (int)((length + 31L) / 32);
        if (needed <= int.MaxValue / 32)
        {
            value = new BitArray(needed == array.Length ? array : array.AsSpan(0, needed).ToArray()) { Length = length };
        }
        else
        {
            // Within 32 bits of int.MaxValue, more than BitArray(int[]) can hold, so bit by bit.
            value = new BitArray(length);
            for (int i = 0; i < length; i++)
            {
                if ((array[i >> 5] & (1 << (i & 31))) != 0) value[i] = true;
            }
        }
    }
}
