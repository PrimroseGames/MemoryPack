using MemoryPack.Internal;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack.Compression;

// serialize as (uncompressedLength, compressedLength, values...)

[Preserve]
public sealed class BrotliFormatter : MemoryPackFormatter<byte[]>
{
    internal const int DefaultDecompssionSizeLimit = 1024 * 1024 * 128; // 128MB

    public static readonly BrotliFormatter Default = new BrotliFormatter();

    readonly System.IO.Compression.CompressionLevel compressionLevel;
    readonly int window;
    readonly int decompressionSizeLimit;

    public BrotliFormatter()
        : this(System.IO.Compression.CompressionLevel.Fastest)
    {

    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel)
        : this(compressionLevel, BrotliUtils.WindowBits_Default)
    {
    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window)
        : this(compressionLevel, window, DefaultDecompssionSizeLimit)
    {
    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window, int decompressionSizeLimit)
    {
        this.compressionLevel = compressionLevel;
        this.window = window;
        this.decompressionSizeLimit = decompressionSizeLimit;
    }

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref byte[]? value)
    {
        if (value == null)
        {
            writer.WriteNullCollectionHeader();
            return;
        }

        if (value.Length == 0)
        {
            writer.WriteCollectionHeader(0);
            return;
        }

        var quality = BrotliUtils.GetQualityFromCompressionLevel(compressionLevel);

        using var encoder = new BrotliEncoder(quality, window);

        var maxLength = BrotliUtils.BrotliEncoderMaxCompressedSize(value.Length);

        ref var head = ref writer.GetSpanReference(maxLength + 8);

        var dest = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref head, 8), maxLength);
        var status = encoder.Compress(value.AsSpan(), dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            MemoryPackSerializationException.ThrowCompressionFailed(status);
        }

        if (bytesConsumed != value.Length)
        {
            MemoryPackSerializationException.ThrowCompressionFailed();
        }

        // write to buffer header (uncompressedLength, compressedLength, values...)
        Unsafe.WriteUnaligned(ref head, value.Length);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, 4), bytesWritten);

        writer.Advance(bytesWritten + 8);
    }

    [Preserve]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref byte[]? value)
    {
        var uncompressedLength = reader.ReadUnmanaged<int>();

        reader.DangerousReadUnmanagedSpanView<byte>(out var isNull, out var compressedBuffer);

        if (isNull)
        {
            value = null;
            return;
        }

        if (compressedBuffer.Length == 0)
        {
            value = Array.Empty<byte>();
            return;
        }

        // security, require to check length
        if (uncompressedLength < 0)
        {
            MemoryPackSerializationException.ThrowInvalidLength(uncompressedLength);
        }
        if (decompressionSizeLimit < uncompressedLength)
        {
            MemoryPackSerializationException.ThrowDecompressionSizeLimitExceeded(decompressionSizeLimit, uncompressedLength);
        }

        using var decoder = new ExactLengthBrotliDecoder(uncompressedLength, compressedBuffer.Length);
        var status = decoder.Decompress(compressedBuffer, out var bytesConsumed);
        if (status != OperationStatus.Done)
        {
            MemoryPackSerializationException.ThrowCompressionFailed(status);
        }
        if (bytesConsumed != compressedBuffer.Length)
        {
            MemoryPackSerializationException.ThrowCompressionFailed();
        }

        var output = decoder.GetOutput();
        if (value == null || value.Length != uncompressedLength)
        {
            value = output.ToArray();
        }
        else
        {
            output.CopyTo(value);
        }
    }
}

// Decompresses into a pooled buffer that grows as output arrives, so a declared length allocates only what the data
// really expands to, never more than that length. GetOutput throws unless the output is exactly that length.
internal sealed class ExactLengthBrotliDecoder : IDisposable
{
    BrotliDecoder decoder;
    byte[]? buffer;
    int written;
    readonly int expectedLength;

    public ExactLengthBrotliDecoder(int expectedLength, int compressedLength)
    {
        this.decoder = new BrotliDecoder();
        this.expectedLength = expectedLength;
        this.buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(expectedLength, Math.Max(4096L, compressedLength * 4L)));
    }

    // Feeds one chunk of compressed data.
    public OperationStatus Decompress(ReadOnlySpan<byte> source, out int consumed)
    {
        consumed = 0;
        var buffer = this.buffer!;
        while (true)
        {
            var capacity = Math.Min(buffer.Length, expectedLength);
            var status = decoder.Decompress(source, buffer.AsSpan(written, capacity - written), out var bytesConsumed, out var bytesWritten);
            consumed += bytesConsumed;
            written += bytesWritten;
            source = source.Slice(bytesConsumed);

            if (status != OperationStatus.DestinationTooSmall) return status;

            // More output than the declared length.
            if (capacity == expectedLength) MemoryPackSerializationException.ThrowCompressionFailed(status);

            var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(expectedLength, buffer.Length * 2L));
            buffer.AsSpan(0, written).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = this.buffer = larger;
        }
    }

    public ReadOnlySpan<byte> GetOutput()
    {
        if (written != expectedLength) MemoryPackSerializationException.ThrowCompressionFailed();
        return buffer.AsSpan(0, written);
    }

    public void Dispose()
    {
        decoder.Dispose();
        if (buffer is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null;
        }
    }
}


[Preserve]
public sealed class BrotliFormatter<T> : MemoryPackFormatter<T>
{
    internal const int DefaultDecompssionSizeLimit = 1024 * 1024 * 128; // 128MB

    public static readonly BrotliFormatter Default = new BrotliFormatter();

    readonly System.IO.Compression.CompressionLevel compressionLevel;
    readonly int window;
    readonly int decompressionSizeLimit;

    public BrotliFormatter()
        : this(System.IO.Compression.CompressionLevel.Fastest)
    {

    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel)
        : this(compressionLevel, BrotliUtils.WindowBits_Default)
    {
    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window)
        : this(compressionLevel, window, DefaultDecompssionSizeLimit)
    {
    }

    public BrotliFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window, int decompressionSizeLimit)
    {
        this.compressionLevel = compressionLevel;
        this.window = window;
        this.decompressionSizeLimit = decompressionSizeLimit;
    }

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref T? value)
    {
        var compressor = new BrotliCompressor(compressionLevel, window);
        try
        {
            var coWriter = new MemoryPackWriter<BrotliCompressor>(ref compressor, writer.OptionalState);

            coWriter.WriteValue(value);
            coWriter.Flush();

            compressor.CopyTo(ref writer);
        }
        finally
        {
            compressor.Dispose();
        }
    }

    [Preserve]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref T? value)
    {
        using var decompressor = new BrotliDecompressor(decompressionSizeLimit);

        reader.GetRemainingSource(out var singleSource, out var remainingSource);

        int consumed;
        ReadOnlySequence<byte> decompressedSource;
        if (singleSource.Length != 0)
        {
            decompressedSource = decompressor.Decompress(singleSource, out consumed);
        }
        else
        {
            decompressedSource = decompressor.Decompress(remainingSource, out consumed);
        }

        using var coReader = new MemoryPackReader(decompressedSource, reader.OptionalState);
        coReader.ReadValue(ref value);

        reader.Advance(consumed);
    }
}
