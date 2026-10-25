using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    static int ReadVarInt(byte[] payload, string type)
    {
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var reader = new MemoryPackReader(payload, state);
        _ = type switch
        {
            "byte" => (object)reader.ReadVarIntByte(),
            "sbyte" => reader.ReadVarIntSByte(),
            "ushort" => reader.ReadVarIntUInt16(),
            "short" => reader.ReadVarIntInt16(),
            "uint" => reader.ReadVarIntUInt32(),
            "int" => reader.ReadVarIntInt32(),
            "ulong" => reader.ReadVarIntUInt64(),
            "long" => reader.ReadVarIntInt64(),
            _ => throw new ArgumentException(type),
        };
        return reader.Consumed;
    }

    // The first byte is the value itself or a code naming the width that follows: 0x87 byte,
    // 0x86 sbyte, 0x85 ushort, 0x84 short, 0x83 uint, 0x82 int, 0x81 ulong, 0x80 long.
    [Theory]
    [InlineData(new byte[] { 0xFF }, "byte")]
    [InlineData(new byte[] { 0x85, 0x00, 0x01 }, "byte")]
    [InlineData(new byte[] { 0x87, 0xC8 }, "sbyte")]
    [InlineData(new byte[] { 0x82, 0xFF, 0xFF, 0xFF, 0xFF }, "ushort")]
    [InlineData(new byte[] { 0x85, 0x40, 0x9C }, "short")]
    [InlineData(new byte[] { 0x80, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "uint")]
    [InlineData(new byte[] { 0x83, 0x00, 0x00, 0x00, 0x80 }, "int")]
    [InlineData(new byte[] { 0x80, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00 }, "int")]
    [InlineData(new byte[] { 0x86, 0xFB }, "ulong")]
    [InlineData(new byte[] { 0x81, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80 }, "long")]
    public void VarIntOutsideTheTargetType(byte[] payload, string type)
    {
        var act = () => ReadVarInt(payload, type);
        act.Should().Throw<MemoryPackSerializationException>();
    }

    [Fact]
    public void VarIntByteReadsTheWholeUInt16()
    {
        ReadVarInt(new byte[] { 0x85, 0xC8, 0x00, 0x7F }, "byte").Should().Be(3);
        ReadVarInt(new byte[] { 0x86, 0xFB }, "long").Should().Be(2);
    }

    [Fact]
    public void DictionaryWithARepeatedKey()
    {
        var payload = Bytes(2, 1, 10, 1, 20);
        Rejects<Dictionary<int, int>>(payload);
        Rejects<SortedDictionary<int, int>>(payload);
        Rejects<SortedList<int, int>>(payload);
        Rejects<System.Collections.Concurrent.ConcurrentDictionary<int, int>>(payload);
        Rejects<System.Collections.Frozen.FrozenDictionary<int, int>>(payload);
        Rejects<System.Collections.Immutable.ImmutableDictionary<int, int>>(payload);
        Rejects<System.Collections.Immutable.ImmutableSortedDictionary<int, int>>(payload);
        Rejects<System.Collections.Immutable.IImmutableDictionary<int, int>>(payload);
        Rejects<IDictionary<int, int>>(payload);
        Rejects<IReadOnlyDictionary<int, int>>(payload);
    }

    [Fact]
    public void DictionaryWithANullKey()
    {
        // One entry whose string key is the null string.
        var payload = Bytes(1, -1, 5);
        Rejects<Dictionary<string, int>>(payload);
        Rejects<SortedDictionary<string, int>>(payload);
        Rejects<System.Collections.Frozen.FrozenDictionary<string, int>>(payload);
        Rejects<System.Collections.Immutable.ImmutableDictionary<string, int>>(payload);
        Rejects<IReadOnlyDictionary<string, int>>(payload);
    }

    static byte[] PatchInt(byte[] bytes, int offset, int value)
    {
        var copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    static byte[] PatchLong(byte[] bytes, int offset, long value)
    {
        var copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    [Fact]
    public void NullableHasValueFlagAboveOne()
    {
        var bytes = MemoryPackSerializer.Serialize(new NullableFlagHolder { Count = 5, Scale = 2f });
        bytes.Length.Should().Be(17); // header, then two 8-byte nullables

        Rejects<NullableFlagHolder>(Patched(bytes, 1, 2));
        Rejects<NullableFlagHolder>(Patched(bytes, 9, 2));

        var read = MemoryPackSerializer.Deserialize<NullableFlagHolder>(bytes)!;
        read.Count.Should().Be(5);
        read.Scale.Should().Be(2f);

        // Without a value the value bytes are cleared, so the null compares equal to any other.
        var empty = MemoryPackSerializer.Serialize(new NullableFlagHolder());
        var garbage = MemoryPackSerializer.Deserialize<NullableFlagHolder>(Patched(empty, 13, 7))!;
        garbage.Scale.Should().BeNull();
        garbage.Scale.GetValueOrDefault().Should().Be(0f);

        // Arrays are raw-copied; dictionary values go through the nullable formatter.
        var array = MemoryPackSerializer.Serialize(new int?[] { 1, null });
        Rejects<int?[]>(Patched(array, 4, 2));
        var map = MemoryPackSerializer.Serialize(new Dictionary<int, int?> { [1] = 5 });
        Rejects<Dictionary<int, int?>>(Patched(map, 8, 2));
        MemoryPackSerializer.Deserialize<Dictionary<int, int?>>(map)![1].Should().Be(5);
    }

    static byte[] Patched(byte[] bytes, int offset, byte value)
    {
        var copy = (byte[])bytes.Clone();
        copy[offset] = value;
        return copy;
    }

    static long AllocatedWhileRejecting<T>(byte[] payload)
    {
        // Formatters and their statics are created before measuring.
        try { MemoryPackSerializer.Deserialize<T>(payload); } catch (MemoryPackSerializationException) { }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => MemoryPackSerializer.Deserialize<T>(payload);
        act.Should().Throw<MemoryPackSerializationException>();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void DeclaredCountsDontPreallocateLargeElements()
    {
        const int n = 1 << 18;

        // Every element claims one byte, which is no element at all: 254 is no member count.
        var invalid = new byte[4 + n];
        BinaryPrimitives.WriteInt32LittleEndian(invalid, n);
        invalid.AsSpan(4).Fill(254);
        AllocatedWhileRejecting<LargeManagedElement[]>(invalid).Should().BeLessThan(24L * n);
        AllocatedWhileRejecting<List<LargeManagedElement>>(invalid).Should().BeLessThan(24L * n);

        // Raw elements take their size, so n of them can't fit in n bytes.
        var zeros = new byte[4 + n];
        BinaryPrimitives.WriteInt32LittleEndian(zeros, n);
        AllocatedWhileRejecting<List<Matrix4x4>>(zeros).Should().BeLessThan(4L * n);
        AllocatedWhileRejecting<Stack<Matrix4x4>>(zeros).Should().BeLessThan(4L * n);
        AllocatedWhileRejecting<System.Collections.Immutable.ImmutableQueue<Matrix4x4>>(zeros).Should().BeLessThan(4L * n);

        // The second zero key repeats the first.
        AllocatedWhileRejecting<Dictionary<long, long>>(zeros).Should().BeLessThan(4L * n);
        AllocatedWhileRejecting<HashSet<Matrix4x4>>(zeros).Should().BeLessThan(4L * n);
    }

    [Fact]
    public void LargeElementsStillReadBack()
    {
        var items = Enumerable.Range(0, 300).Select(i => new LargeManagedElement { Name = i.ToString(), Transform = Matrix4x4.CreateScale(i), A = i, B = -i }).ToArray();

        MemoryPackSerializer.Deserialize<LargeManagedElement[]>(MemoryPackSerializer.Serialize(items)).Should().Equal(items);
        MemoryPackSerializer.Deserialize<List<LargeManagedElement>>(MemoryPackSerializer.Serialize(items.ToList())).Should().Equal(items);

        var stack = new Stack<LargeManagedElement>(items);
        MemoryPackSerializer.Deserialize<Stack<LargeManagedElement>>(MemoryPackSerializer.Serialize(stack)).Should().Equal(stack);

        var existing = new List<LargeManagedElement> { items[0] };
        MemoryPackSerializer.Deserialize(MemoryPackSerializer.Serialize(items.ToList()), ref existing);
        existing.Should().Equal(items);
    }

    [Fact]
    public void SortedListKeysOutOfOrderReadSorted()
    {
        // A list sorted by another comparer arrives out of order, which used to cost a shift per key.
        var descending = new SortedList<int, int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        for (int i = 0; i < 20000; i++) descending.Add(i, -i);
        var read = MemoryPackSerializer.Deserialize<SortedList<int, int>>(MemoryPackSerializer.Serialize(descending))!;
        read.Keys.Should().Equal(Enumerable.Range(0, 20000));
        read[7].Should().Be(-7);

        // A key repeated after the order breaks is still caught.
        Rejects<SortedList<int, int>>(Bytes(3, 5, 0, 3, 0, 5, 0));
        Rejects<SortedList<int, int>>(Bytes(2, 5, 0, 5, 0));
    }

    [Fact]
    public void NullableEnumKeysGetAResistantComparer()
    {
        var set = MemoryPackSerializer.Deserialize<HashSet<ValidatedLevel?>>(MemoryPackSerializer.Serialize(new HashSet<ValidatedLevel?> { ValidatedLevel.Low, null, ValidatedLevel.High }))!;
        set.Comparer.GetType().Namespace.Should().Be("MemoryPack.Internal");
        set.Should().Contain(new ValidatedLevel?[] { ValidatedLevel.Low, null, ValidatedLevel.High });
        set.Comparer.GetHashCode(null!).Should().Be(0);
        set.Comparer.GetHashCode(ValidatedLevel.Low).Should().Be(set.Comparer.GetHashCode((ValidatedLevel?)ValidatedLevel.Low));
        set.Comparer.Equals(ValidatedLevel.Low, ValidatedLevel.High).Should().BeFalse();
    }

    [Fact]
    public void BrotliDeclaredLengthAllocatesOnlyTheOutput()
    {
        var bytes = MemoryPackSerializer.Serialize(new BrotliBytesProbe { Data = new byte[100] });
        var claimed = PatchInt(bytes, 1, 100_000_000); // uncompressedLength, under the 128 MB limit
        AllocatedWhileRejecting<BrotliBytesProbe>(claimed).Should().BeLessThan(1 << 20);

        var text = MemoryPackSerializer.Serialize(new BrotliStringProbe { Text = "hello" });
        AllocatedWhileRejecting<BrotliStringProbe>(PatchInt(text, 1, 50_000_000)).Should().BeLessThan(1 << 20);

        // Output that grows the buffer many times still reads back whole.
        var large = Enumerable.Range(0, 1 << 20).Select(i => (byte)(i % 7)).ToArray();
        MemoryPackSerializer.Deserialize<BrotliBytesProbe>(MemoryPackSerializer.Serialize(new BrotliBytesProbe { Data = large }))!.Data.Should().Equal(large);
        var longText = string.Concat(Enumerable.Repeat("abcdefg", 100_000));
        MemoryPackSerializer.Deserialize<BrotliStringProbe>(MemoryPackSerializer.Serialize(new BrotliStringProbe { Text = longText }))!.Text.Should().Be(longText);

        // More output than declared.
        Rejects<BrotliBytesProbe>(PatchInt(bytes, 1, 99));
    }

    [Fact]
    public void RepeatedKeyIsNotFormatted()
    {
        var key = MemoryPackSerializer.Serialize(new ThrowingKey { Id = 7 });
        var act = () => MemoryPackSerializer.Deserialize<Dictionary<ThrowingKey, int>>(Bytes(2, key, 1, key, 2));
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*ThrowingKey*");
    }

    [Fact]
    public void InvalidUriIsAMemoryPackException()
    {
        var candidates = new[] { "http://[", "http://host:99999999999/", "http://a b/", "http://\u0001/", new string('a', 70000) };
        var invalid = candidates.Where(s =>
        {
            try { _ = new Uri(s, UriKind.RelativeOrAbsolute); return false; }
            catch (UriFormatException) { return true; }
        }).ToArray();

        invalid.Should().NotBeEmpty();
        foreach (var s in invalid) Rejects<Uri>(MemoryPackSerializer.Serialize(s));
    }

    [Fact]
    public void BackReferenceToAnotherTypeIsRejected()
    {
        var shared = new CircularLeaf { X = 3 };
        var bytes = MemoryPackSerializer.Serialize(new CircularRoot { First = shared, Second = shared });

        // The second member ends as a reference to id 1, the leaf; id 0 is the root.
        bytes[^2].Should().Be(MemoryPackCode.ReferenceId);
        bytes[^1].Should().Be(1);
        Rejects<CircularRoot>(Patched(bytes, bytes.Length - 1, 0));

        // A back-reference doesn't run OnDeserialized again.
        var read = MemoryPackSerializer.Deserialize<CircularRoot>(bytes)!;
        read.Second.Should().BeSameAs(read.First);
        read.First!.DeserializedCount.Should().Be(1);
    }

    [Fact]
    public void DynamicUnionReadsPastAnExistingValueOfAnotherType()
    {
        // Registers both members' formatters.
        MemoryPackSerializer.Serialize(new DynamicProbeA());
        MemoryPackSerializer.Serialize(new DynamicProbeB());

        var formatter = new global::MemoryPack.Formatters.DynamicUnionFormatter<IDynamicProbe>((0, typeof(DynamicProbeA)), (1, typeof(DynamicProbeB)));
        IDynamicProbe? source = new DynamicProbeB { B = 9 };
        var bytes = Write((ref MemoryPackWriter<ArrayBufferWriter<byte>> w) => formatter.Serialize(ref w, ref source));

        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        var reader = new MemoryPackReader(bytes, state);
        IDynamicProbe? existing = new DynamicProbeA { A = 1 };
        formatter.Deserialize(ref reader, ref existing);
        existing.Should().BeOfType<DynamicProbeB>().Which.B.Should().Be(9);
    }

    [Fact]
    public void BuiltinStructsOutsideTheirRange()
    {
        var bytes = MemoryPackSerializer.Serialize(new BuiltinValueHolder { When = DateTime.UnixEpoch, Amount = 1.5m, Day = DateOnly.MinValue, Time = TimeOnly.MinValue, Letter = new Rune('a') });
        bytes.Length.Should().Be(41); // header, 8, 16, 4, 8, 4

        Rejects<BuiltinValueHolder>(PatchLong(bytes, 1, DateTime.MaxValue.Ticks + 1));
        Rejects<BuiltinValueHolder>(PatchInt(bytes, 9, 29 << 16)); // decimal scale
        Rejects<BuiltinValueHolder>(PatchInt(bytes, 9, 1)); // decimal reserved bits
        Rejects<BuiltinValueHolder>(PatchInt(bytes, 25, int.MaxValue));
        Rejects<BuiltinValueHolder>(PatchLong(bytes, 29, long.MaxValue));
        Rejects<BuiltinValueHolder>(PatchInt(bytes, 37, 0xD800));
        Rejects<BuiltinValueHolder>(PatchInt(bytes, 37, 0x110000));

        var dates = MemoryPackSerializer.Serialize(new[] { DateTime.UnixEpoch });
        Rejects<DateTime[]>(PatchLong(dates, 4, long.MaxValue));

        // Every valid value reads back, the local-ambiguous kind bits included.
        var value = new BuiltinValueHolder
        {
            When = new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Local),
            Amount = -0.0000000000000000000000000001m,
            Day = DateOnly.MaxValue,
            Time = TimeOnly.MaxValue,
            Letter = new Rune(0x10FFFF),
        };
        var legit = MemoryPackSerializer.Serialize(value);
        MemoryPackSerializer.Serialize(MemoryPackSerializer.Deserialize<BuiltinValueHolder>(legit)).Should().Equal(legit);
        var ambiguous = PatchLong(legit, 1, unchecked((long)(0xC000000000000000UL | 5UL)));
        MemoryPackSerializer.Deserialize<BuiltinValueHolder>(ambiguous)!.When.Ticks.Should().Be(5);
    }

    [Fact]
    public void RentedArraysAreNotReadInto()
    {
        var pool = ArrayPool<PoolProbe?>.Shared;
        var stale = pool.Rent(16);
        for (int i = 0; i < stale.Length; i++) stale[i] = new PoolProbe { X = -1 };
        var kept = stale.ToArray();
        pool.Return(stale, clearArray: false);

        var items = new[] { new PoolProbe { X = 1 }, new PoolProbe { X = 2 }, new PoolProbe { X = 3 } };
        var queue = MemoryPackSerializer.Deserialize<System.Collections.Immutable.ImmutableQueue<PoolProbe>>(MemoryPackSerializer.Serialize(System.Collections.Immutable.ImmutableQueue.Create(items)))!;
        queue.Select(x => x.X).Should().Equal(1, 2, 3);
        var immutableStack = MemoryPackSerializer.Deserialize<System.Collections.Immutable.ImmutableStack<PoolProbe>>(MemoryPackSerializer.Serialize(System.Collections.Immutable.ImmutableStack.Create(items)))!;
        immutableStack.Select(x => x.X).Should().Equal(3, 2, 1);

        var formatter = new global::MemoryPack.Formatters.MemoryPoolFormatter<PoolProbe>();
        var arrayBytes = MemoryPackSerializer.Serialize(items);
        using (var state = MemoryPackReaderOptionalStatePool.Rent(null))
        {
            var reader = new MemoryPackReader(arrayBytes, state);
            Memory<PoolProbe?> memory = default;
            formatter.Deserialize(ref reader, ref memory);
            memory.ToArray().Select(x => x!.X).Should().Equal(1, 2, 3);
        }

        kept.Should().OnlyContain(x => x!.X == -1);
    }

    static Exception? RunOnThread(Action action, int stackSize)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { caught = e; }
        }, stackSize);
        thread.Start();
        thread.Join();
        return caught;
    }

    [Fact]
    public void DeepVersionTolerantDataStopsBeforeTheStackRunsOut()
    {
        // Each level is [1 member][delta: the child's size][child]; the innermost child is null.
        const int depth = 20000;
        var sizes = new int[depth + 1];
        sizes[0] = 1;
        for (int i = 1; i <= depth; i++)
        {
            var inner = sizes[i - 1];
            sizes[i] = 1 + Write((ref MemoryPackWriter<ArrayBufferWriter<byte>> w) => w.WriteVarInt(inner)).Length + inner;
        }
        var payload = Write((ref MemoryPackWriter<ArrayBufferWriter<byte>> w) =>
        {
            for (int i = depth; i >= 1; i--)
            {
                w.WriteObjectHeader(1);
                w.WriteVarInt(sizes[i - 1]);
            }
            w.WriteNullObjectHeader();
        });
        payload.Length.Should().Be(sizes[depth]);

        var options = MemoryPackSerializerOptions.Default with { MaxDepth = 0 };
        var error = RunOnThread(() => MemoryPackSerializer.Deserialize<VtDepthNode>(payload, options), 512 * 1024);
        error.Should().BeOfType<MemoryPackSerializationException>().Which.Message.Should().Contain("stack");
    }

    [Fact]
    public void ReservedUnionHeadersAreRejected()
    {
        for (byte header = 251; header < 255; header++)
        {
            Rejects<IDepthUnion>(new[] { header });
        }
        MemoryPackSerializer.Deserialize<IDepthUnion>(new byte[] { 255 }).Should().BeNull();
    }

    [Fact]
    public void InterningStopsAtLongStrings()
    {
        static string? Read(string text)
        {
            var bytes = MemoryPackSerializer.Serialize(text);
            using var state = MemoryPackReaderOptionalStatePool.Rent(null);
            var reader = new MemoryPackReader(bytes, state);
            string? read = null;
            global::MemoryPack.Formatters.InternStringFormatter.Default.Deserialize(ref reader, ref read);
            return read;
        }

        var longText = new string('x', 300) + Guid.NewGuid();
        var readLong = Read(longText)!;
        readLong.Should().Be(longText);
        string.IsInterned(readLong).Should().BeNull();

        var shortText = Guid.NewGuid().ToString();
        string.IsInterned(Read(shortText)!).Should().NotBeNull();
    }

    sealed class OneByteStream : Stream
    {
        readonly MemoryStream inner;
        public OneByteStream(byte[] data) => inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => inner.Read(buffer.Slice(0, Math.Min(buffer.Length, 1)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void StreamingReadWaitsForMoreWhenAnItemDoesntFit()
    {
        // With the minimum read equal to bufferAtLeast, a full buffer reads no item; asking for the same amount
        // again returned the same bytes at once, forever.
        var bytes = MemoryPackSerializer.Serialize(Enumerable.Range(0, 10).ToArray());
        var task = Task.Run(async () =>
        {
            var items = new List<int>();
            var reader = System.IO.Pipelines.PipeReader.Create(new OneByteStream(bytes));
            await foreach (var item in global::MemoryPack.Streaming.MemoryPackStreamingSerializer.DeserializeAsync<int>(reader, bufferAtLeast: 16, readMinimumSize: 16))
            {
                items.Add(item);
            }
            return items;
        });

        task.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        task.Result.Should().Equal(Enumerable.Range(0, 10));
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

[MemoryPackable]
public partial class NullableFlagHolder
{
    public int? Count;
    public float? Scale;
}

// Holds references and is larger than a preallocated slot may be.
[MemoryPackable]
public partial struct LargeManagedElement
{
    public string? Name;
    public Matrix4x4 Transform;
    public long A;
    public long B;
}

[MemoryPackable]
public partial class ThrowingKey
{
    public int Id;

    public override bool Equals(object? obj) => obj is ThrowingKey other && other.Id == Id;
    public override int GetHashCode() => Id;
    public override string ToString() => throw new InvalidOperationException("The key was formatted.");
}

[MemoryPackable(GenerateType.CircularReference)]
public partial class CircularRoot
{
    [MemoryPackOrder(0)] public CircularLeaf? First { get; set; }
    [MemoryPackOrder(1)] public CircularLeaf? Second { get; set; }
}

[MemoryPackable(GenerateType.CircularReference)]
public partial class CircularLeaf
{
    [MemoryPackOrder(0)] public int X { get; set; }

    [MemoryPackIgnore] public int DeserializedCount { get; set; }

    [MemoryPackOnDeserialized]
    void OnDeserialized() => DeserializedCount++;
}

public interface IDynamicProbe
{
}

[MemoryPackable]
public partial class DynamicProbeA : IDynamicProbe
{
    public int A { get; set; }
}

[MemoryPackable]
public partial class DynamicProbeB : IDynamicProbe
{
    public int B { get; set; }
}

[MemoryPackable]
public partial class BuiltinValueHolder
{
    public DateTime When;
    public decimal Amount;
    public DateOnly Day;
    public TimeOnly Time;
    public Rune Letter;
}

[MemoryPackable]
public partial class PoolProbe
{
    public int X { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class VtDepthNode
{
    [MemoryPackOrder(0)] public VtDepthNode? Child { get; set; }
}
