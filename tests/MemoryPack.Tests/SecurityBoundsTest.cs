using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MemoryPack.Tests;

// Crafted payloads that once read outside the buffer, wrapped a size past a bounds check, or
// allocated from a length the data didn't hold. Each must fail with MemoryPackSerializationException.
public class SecurityBoundsTest
{
    static byte[] Bytes(params object[] parts)
    {
        var list = new List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case int i:
                    var b = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(b, i);
                    list.AddRange(b);
                    break;
                case byte one:
                    list.Add(one);
                    break;
                case byte[] many:
                    list.AddRange(many);
                    break;
                default:
                    throw new ArgumentException(part.GetType().Name);
            }
        }
        return list.ToArray();
    }

    delegate void WriteAction(ref MemoryPackWriter<ArrayBufferWriter<byte>> writer);

    static byte[] Write(WriteAction write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var state = MemoryPackWriterOptionalStatePool.Rent(null);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, state);
        write(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    static void Rejects<T>(byte[] payload)
    {
        var act = () => MemoryPackSerializer.Deserialize<T>(payload);
        act.Should().Throw<MemoryPackSerializationException>();

        // The same bytes split over a sequence take the reader's other paths.
        if (payload.Length > 1)
        {
            var seq = ReadOnlySequenceBuilder.Create(payload.AsSpan(0, payload.Length / 2).ToArray(), payload.AsSpan(payload.Length / 2).ToArray());
            var actSeq = () => MemoryPackSerializer.Deserialize<T>(seq);
            actSeq.Should().Throw<MemoryPackSerializationException>();
        }
    }

    [Fact]
    public void VersionTolerantNegativeDeltaDoesNotMoveBackwards()
    {
        // Three members declared, two known; the unknown third is skipped by a negative delta.
        var payload = Write((ref MemoryPackWriter<ArrayBufferWriter<byte>> w) =>
        {
            w.WriteObjectHeader(3);
            w.WriteVarInt(4);
            w.WriteVarInt(4);
            w.WriteVarInt(-100);
            w.WriteUnmanaged(1);
            w.WriteUnmanaged(2);
        });
        Rejects<VtBounds>(payload);
    }

    [Fact]
    public void VersionTolerantDeltaPastEnd()
    {
        var payload = Write((ref MemoryPackWriter<ArrayBufferWriter<byte>> w) =>
        {
            w.WriteObjectHeader(3);
            w.WriteVarInt(4);
            w.WriteVarInt(4);
            w.WriteVarInt(int.MaxValue);
            w.WriteUnmanaged(1);
            w.WriteUnmanaged(2);
        });
        Rejects<VtBounds>(payload);
    }

    [Fact]
    public void Utf8LengthThatOverflowsTheBoundsCheck()
    {
        // ~int.MinValue is int.MaxValue; adding the utf16 length's 4 bytes used to wrap negative.
        Rejects<string>(Bytes(int.MinValue, 5, Encoding.UTF8.GetBytes("hello")));
        Rejects<string>(Bytes(int.MinValue + 2, 1, (byte)'a'));
    }

    [Fact]
    public void Utf8LengthPastEnd()
    {
        Rejects<string>(Bytes(~100, 5, Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void Utf16LengthLargerThanUtf8Bytes()
    {
        Rejects<string>(Bytes(~5, 1000, Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void Utf16LengthLongerThanTheDecodedString()
    {
        // "aé" is three UTF-8 bytes and two UTF-16 units; a stated three would leave a NUL.
        Rejects<string>(Bytes(~3, 3, Encoding.UTF8.GetBytes("aé")));
    }

    [Fact]
    public void Utf16StringPastEnd()
    {
        Rejects<string>(Bytes(10, (byte)'a', (byte)0));
    }

    [Fact]
    public void NegativeCollectionLength()
    {
        Rejects<int[]>(Bytes(-5, 1, 2));
        Rejects<List<string>>(Bytes(-2, 0));
        Rejects<long[]>(Bytes(int.MinValue, 0));
    }

    [Fact]
    public void UnmanagedArrayByteCountThatWraps()
    {
        // 524288 elements of 4096 bytes is 2^31 bytes, which wrapped to int.MinValue and skipped
        // the bounds check. The header passes its element check against a 600 KB payload.
        var payload = new byte[600_000];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 524_288);
        Rejects<Big4K[]>(payload);
    }

    [Fact]
    public void UnmanagedArrayLongerThanTheData()
    {
        Rejects<Big4K[]>(Bytes(2, new byte[4096]));
        Rejects<long[]>(Bytes(3, new byte[16]));
    }

    [Fact]
    public void MultiDimensionalArrayDimensionsMustMatchTheCount()
    {
        // Huge dimensions with a count of zero used to allocate the dimensions.
        Rejects<int[,]>(Bytes((byte)3, 100_000, 100_000, 0));
        Rejects<string[,]>(Bytes((byte)3, 50_000, 50_000, 0));
        Rejects<int[,]>(Bytes((byte)3, -1, -1, 1, 0));
        Rejects<int[,,]>(Bytes((byte)3, 2, 2, 2, 7, new byte[28]));
        Rejects<int[,,,]>(Bytes((byte)3, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, 1, 0));
    }

    [Fact]
    public void MultiDimensionalArrayWithAZeroDimensionStillReads()
    {
        var empty = new int[1000, 0];
        var read = MemoryPackSerializer.Deserialize<int[,]>(MemoryPackSerializer.Serialize(empty))!;
        read.GetLength(0).Should().Be(1000);
        read.GetLength(1).Should().Be(0);
    }

    [Fact]
    public void BitArrayLengthLargerThanItsArray()
    {
        // A 9-byte prefix once allocated a 256 MB BitArray.
        Rejects<BitArray>(Bytes((byte)2, 0x7FFF_FFF0, 1, 0));
        Rejects<BitArray>(Bytes((byte)2, -1, 1, 0));
        Rejects<BitArray>(Bytes((byte)2, 5, -1));
    }

    [Fact]
    public void BitArrayRoundTrips()
    {
        var bits = new BitArray(70);
        bits[3] = true;
        bits[69] = true;
        var read = MemoryPackSerializer.Deserialize<BitArray>(MemoryPackSerializer.Serialize(bits))!;
        read.Length.Should().Be(70);
        read[3].Should().BeTrue();
        read[69].Should().BeTrue();
        read[4].Should().BeFalse();
    }

    [Fact]
    public void BigIntegerNegativeLength()
    {
        Rejects<BigInteger>(Bytes(-3, 1, 2));
    }

    [Fact]
    public void BrotliStringLengthThatWrapsUnderTheLimit()
    {
        // 0x40000001 chars is 2^31 + 2 bytes, which wrapped negative and skipped the size limit.
        Rejects<BrotliStringProbe>(Bytes((byte)1, 0x4000_0001, new byte[8]));
        Rejects<BrotliStringProbe>(Bytes((byte)1, -7, new byte[8]));
    }

    [Fact]
    public void BrotliNegativeUncompressedLength()
    {
        Rejects<BrotliBytesProbe>(Bytes((byte)1, -10, 4, new byte[4]));
    }

    [Fact]
    public void BitPackNegativeLength()
    {
        Rejects<BitPackProbe>(Bytes((byte)1, -9, 0));
    }

    [Fact]
    public void VersionWithInvalidComponents()
    {
        Rejects<Version>(Bytes((byte)4, -1, 0, -1, -1));
        Rejects<Version>(Bytes((byte)4, 1, 2, -1, 5));
        Rejects<Version>(Bytes((byte)4, 1, 2, -2, -1));
    }

    [Fact]
    public void StringBuilderLongerThanTheData()
    {
        Rejects<StringBuilder>(Bytes(10, new byte[12]));
    }
}

[StructLayout(LayoutKind.Sequential, Size = 4096)]
public struct Big4K
{
    public int A;
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class VtBounds
{
    [MemoryPackOrder(0)] public int A { get; set; }
    [MemoryPackOrder(1)] public int B { get; set; }
}

[MemoryPackable]
public partial class BrotliStringProbe
{
    [BrotliStringFormatter] public string? Text { get; set; }
}

[MemoryPackable]
public partial class BrotliBytesProbe
{
    [BrotliFormatter] public byte[]? Data { get; set; }
}

[MemoryPackable]
public partial class BitPackProbe
{
    [BitPackFormatter] public bool[]? Bits { get; set; }
}
