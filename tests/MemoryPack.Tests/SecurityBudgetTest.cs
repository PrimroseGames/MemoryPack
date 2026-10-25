using MemoryPack.Compression;
using MemoryPack.Streaming;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MemoryPack.Tests;

// Declared counts can't make a read allocate more than a small multiple of its length.
public class SecurityBudgetTest
{
    // Four nested headers, each claiming the whole tail behind it. Each passes the per-header check against what
    // remains, and once each preallocated a reference array the size of the payload.
    static byte[] NestedClaims(int length)
    {
        var payload = new byte[length];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(i * 4), length - (i + 1) * 4);
        }
        return payload;
    }

    [Fact]
    public void NestedHeadersCantEachClaimTheTail()
    {
        const int length = 1 << 20;
        var payload = NestedClaims(length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var act = () => MemoryPackSerializer.Deserialize<string[][][][]>(payload);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*declares*");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The outer array is the only one allocated: one reference per byte.
        allocated.Should().BeLessThan(10L * length);
    }

    [Fact]
    public void NestedHeadersOverASequence()
    {
        var payload = NestedClaims(1 << 16);
        var seq = ReadOnlySequenceBuilder.Create(payload.AsSpan(0, 7).ToArray(), payload.AsSpan(7).ToArray());
        var act = () => MemoryPackSerializer.Deserialize<List<List<List<string>>>>(seq);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*declares*");
    }

    [Fact]
    public void LegitimateNestingReadsBack()
    {
        var value = Enumerable.Range(0, 200).Select(i => Enumerable.Range(0, i).Select(j => new[] { $"{i}.{j}", "" }).ToList()).ToList();
        var read = MemoryPackSerializer.Deserialize<List<List<string[]>>>(MemoryPackSerializer.Serialize(value))!;
        read.Should().BeEquivalentTo(value);

        var bytes = Enumerable.Range(0, 5000).Select(i => new byte[i % 7]).ToArray();
        MemoryPackSerializer.Deserialize<byte[][]>(MemoryPackSerializer.Serialize(bytes)).Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public void ChargeElements()
    {
        var payload = new byte[16];
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);

        var reader = new MemoryPackReader(payload, state);
        reader.ChargeElements(10);
        reader.ChargeElements(6);
        var exhausted = false;
        try { reader.ChargeElements(1); }
        catch (MemoryPackSerializationException) { exhausted = true; }
        exhausted.Should().BeTrue();

        reader = new MemoryPackReader(payload, state);
        var negative = false;
        try { reader.ChargeElements(-1); }
        catch (MemoryPackSerializationException) { negative = true; }
        negative.Should().BeTrue();

        reader = new MemoryPackReader(payload, state);
        var pastEnd = false;
        try { reader.ChargeElements(17); }
        catch (MemoryPackSerializationException) { pastEnd = true; }
        pastEnd.Should().BeTrue();
    }

    [Fact]
    public void BrotliValueDecompressesWithinItsLimit()
    {
        var value = new BrotliLimited { Values = Enumerable.Range(0, 10_000).ToList() };
        var bytes = MemoryPackSerializer.Serialize(value);
        var act = () => MemoryPackSerializer.Deserialize<BrotliLimited>(bytes);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*limit*");

        var small = new BrotliLimited { Values = Enumerable.Range(0, 100).ToList() };
        MemoryPackSerializer.Deserialize<BrotliLimited>(MemoryPackSerializer.Serialize(small))!.Values.Should().Equal(small.Values);
    }

    [Fact]
    public void BrotliDecompressorLimit()
    {
        var act = () => new BrotliDecompressor(0);
        act.Should().Throw<ArgumentOutOfRangeException>();

        var data = new byte[100_000];
        var compressed = new byte[1024];
        System.IO.Compression.BrotliEncoder.TryCompress(data, compressed, out var written).Should().BeTrue();
        compressed = compressed.AsSpan(0, written).ToArray();

        var limited = new BrotliDecompressor(50_000);
        try
        {
            var decompress = () => { limited.Decompress(compressed); };
            decompress.Should().Throw<MemoryPackSerializationException>().WithMessage("*limit*");
        }
        finally
        {
            limited.Dispose();
        }

        using var unlimited = new BrotliDecompressor();
        unlimited.Decompress(compressed).Length.Should().Be(data.Length);
    }

    sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] data) : base(data, writable: false) { }
        public override bool TryGetBuffer(out ArraySegment<byte> buffer)
        {
            buffer = default;
            return false;
        }
    }

    [Fact]
    public async Task StreamLongerThanTheBufferLimit()
    {
        var bytes = MemoryPackSerializer.Serialize(new byte[2000]);
        var options = MemoryPackSerializerOptions.Default with { MaxBufferedStreamLength = 1000 };

        var act = async () => await MemoryPackSerializer.DeserializeAsync<byte[]>(new NonSeekableStream(bytes), options);
        await act.Should().ThrowAsync<MemoryPackSerializationException>().WithMessage("*MaxBufferedStreamLength*");

        var actNonGeneric = async () => await MemoryPackSerializer.DeserializeAsync(typeof(byte[]), new NonSeekableStream(bytes), options);
        await actNonGeneric.Should().ThrowAsync<MemoryPackSerializationException>();

        var fits = MemoryPackSerializerOptions.Default with { MaxBufferedStreamLength = bytes.Length };
        (await MemoryPackSerializer.DeserializeAsync<byte[]>(new NonSeekableStream(bytes), fits))!.Length.Should().Be(2000);

        var invalid = () => MemoryPackSerializerOptions.Default with { MaxBufferedStreamLength = 0 };
        invalid.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task StreamingNegativeItemCount()
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(payload, -5);
        var act = async () =>
        {
            await foreach (var _ in MemoryPackStreamingSerializer.DeserializeAsync<int>(new MemoryStream(payload), 4, 8)) { }
        };
        await act.Should().ThrowAsync<MemoryPackSerializationException>();
    }
}

[MemoryPackable]
public partial class BrotliLimited
{
    [BrotliFormatter<List<int>>(decompressionSizeLimit: 1024)]
    public List<int>? Values { get; set; }
}
