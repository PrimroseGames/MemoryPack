using MemoryPack.Internal;
using System.Collections;
using System.Runtime.CompilerServices;

namespace MemoryPack.Formatters;

[Preserve]
public sealed class BitArrayFormatter : MemoryPackFormatter<BitArray>
{
    // serialize [m_length, m_array]

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref BitArray? value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }

        ref var view = ref Unsafe.As<BitArray, BitArrayView>(ref value);

        writer.WriteUnmanagedWithObjectHeader(2, view.m_length);
        writer.WriteUnmanagedArray(view.m_array);
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

        // The array is read before anything is sized by length, and has to hold length bits. It can
        // be longer: a BitArray keeps its array when shortened by less than its shrink threshold.
        int[]? array = null;
        reader.ReadUnmanagedArray(ref array);
        if (array == null || length < 0 || (length + 31L) / 32 > array.Length)
        {
            MemoryPackSerializationException.ThrowInvalidLength(length);
        }

        var bitArray = new BitArray(0);

        ref var view = ref Unsafe.As<BitArray, BitArrayView>(ref bitArray);
        view.m_array = array;
        view.m_length = length;

        value = bitArray;
    }
}

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
[Preserve]
internal class BitArrayView
{
    public int[] m_array;
    public int m_length;
    public int _version;
}
