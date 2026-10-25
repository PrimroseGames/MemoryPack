using MemoryPack.Internal;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;

namespace MemoryPack.Compression;

public struct BrotliDecompressor : IDisposable
{
    ReusableReadOnlySequenceBuilder? sequenceBuilder;
    readonly int decompressionSizeLimit;
    int decompressedLength;

    /// <summary>Creates a decompressor that throws once the data decompresses past <paramref name="decompressionSizeLimit"/> bytes.</summary>
    public BrotliDecompressor(int decompressionSizeLimit)
    {
        if (decompressionSizeLimit <= 0) throw new ArgumentOutOfRangeException(nameof(decompressionSizeLimit), decompressionSizeLimit, "The limit must be positive.");
        this.sequenceBuilder = null;
        this.decompressionSizeLimit = decompressionSizeLimit;
        this.decompressedLength = 0;
    }

    public ReadOnlySequence<byte> Decompress(ReadOnlySpan<byte> compressedSpan)
    {
        return Decompress(compressedSpan, out _);
    }

    public ReadOnlySequence<byte> Decompress(ReadOnlySpan<byte> compressedSpan, out int consumed)
    {
        if (sequenceBuilder != null)
        {
            MemoryPackSerializationException.ThrowAlreadyDecompressed();
        }

        sequenceBuilder = ReusableReadOnlySequenceBuilderPool.Rent();
        var decoder = new BrotliDecoder();
        try
        {
            var status = OperationStatus.DestinationTooSmall;
            DecompressCore(ref status, ref decoder, compressedSpan, out consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                MemoryPackSerializationException.ThrowCompressionFailed(status);
            }
        }
        finally
        {
            decoder.Dispose();
        }

        return sequenceBuilder.Build();
    }

    public ReadOnlySequence<byte> Decompress(ReadOnlySequence<byte> compressedSequence)
    {
        return Decompress(compressedSequence, out _);
    }

    public ReadOnlySequence<byte> Decompress(ReadOnlySequence<byte> compressedSequence, out int consumed)
    {
        if (sequenceBuilder != null)
        {
            MemoryPackSerializationException.ThrowAlreadyDecompressed();
        }

        sequenceBuilder = ReusableReadOnlySequenceBuilderPool.Rent();
        var decoder = new BrotliDecoder();
        try
        {
            var status = OperationStatus.DestinationTooSmall;
            consumed = 0;
            foreach (var item in compressedSequence)
            {
                DecompressCore(ref status, ref decoder, item.Span, out var bytesConsumed);
                consumed += bytesConsumed;
            }

            if (status == OperationStatus.NeedMoreData)
            {
                MemoryPackSerializationException.ThrowCompressionFailed(status);
            }
        }
        finally
        {
            decoder.Dispose();
        }

        return sequenceBuilder.Build();
    }

    void DecompressCore(ref OperationStatus status, ref BrotliDecoder decoder, ReadOnlySpan<byte> source, out int consumed)
    {
        Debug.Assert(sequenceBuilder != null);
        consumed = 0;

        byte[]? buffer = null;
        status = OperationStatus.DestinationTooSmall;
        var nextCapacity = source.Length;
        while (status == OperationStatus.DestinationTooSmall)
        {
            if (buffer == null)
            {
                nextCapacity = GetDoubleCapacity(nextCapacity);
                buffer = ArrayPool<byte>.Shared.Rent(nextCapacity);
            }

            status = decoder.Decompress(source, buffer, out var bytesConsumed, out var bytesWritten);
            consumed += bytesConsumed;

            if (status == OperationStatus.InvalidData)
            {
                MemoryPackSerializationException.ThrowCompressionFailed(status);
            }

            // A default decompressor has no limit.
            if (decompressionSizeLimit != 0 && bytesWritten > decompressionSizeLimit - decompressedLength)
            {
                MemoryPackSerializationException.ThrowDecompressionSizeLimitExceeded(decompressionSizeLimit, (int)Math.Min((long)decompressedLength + bytesWritten, int.MaxValue));
            }
            decompressedLength += bytesWritten;

            if (status == OperationStatus.NeedMoreData)
            {
                if (bytesWritten > 0)
                {
                    sequenceBuilder.Add(buffer.AsMemory(0, bytesWritten), true);
                }
                if (bytesConsumed > 0)
                {
                    source = source.Slice(bytesConsumed);
                }
                if (source.Length != 0)
                {
                    // not consumed source fully
                    MemoryPackSerializationException.ThrowCompressionFailed();
                }

                // continue for next sequence. 
                return;
            }

            if (bytesConsumed > 0)
            {
                source = source.Slice(bytesConsumed);
            }
            if (bytesWritten > 0)
            {
                sequenceBuilder.Add(buffer.AsMemory(0, bytesWritten), true);
                buffer = null;
            }
        }
    }

    public void Dispose()
    {
        if (sequenceBuilder != null)
        {
            ReusableReadOnlySequenceBuilderPool.Return(sequenceBuilder);
            sequenceBuilder = null;
        }
    }

    int GetDoubleCapacity(int length)
    {
        var newCapacity = unchecked(length * 2);
        if ((uint)newCapacity > int.MaxValue) newCapacity = int.MaxValue;
        return Math.Max(newCapacity, 4096);
    }
}
