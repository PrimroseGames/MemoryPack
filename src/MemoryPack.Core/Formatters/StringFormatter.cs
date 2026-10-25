using MemoryPack.Compression;
using MemoryPack.Internal;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack.Formatters;

[Preserve]
public sealed class StringFormatter : MemoryPackFormatter<string>
{
    public static readonly StringFormatter Default = new StringFormatter();

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref string? value)
    {
        writer.WriteString(value);
    }

    [Preserve]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value)
    {
        value = reader.ReadString();
    }
}

[Preserve]
public sealed class Utf8StringFormatter : MemoryPackFormatter<string>
{
    public static readonly Utf8StringFormatter Default = new Utf8StringFormatter();

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref string? value)
    {
        writer.WriteUtf8(value);
    }

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value)
    {
        value = reader.ReadString();
    }
}

[Preserve]
public sealed class Utf16StringFormatter : MemoryPackFormatter<string>
{
    public static readonly Utf16StringFormatter Default = new Utf16StringFormatter();

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref string? value)
    {
        writer.WriteUtf16(value);
    }

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value)
    {
        value = reader.ReadString();
    }
}

[Preserve]
public sealed class InternStringFormatter : MemoryPackFormatter<string>
{
    public static readonly InternStringFormatter Default = new InternStringFormatter();

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref string? value)
    {
        writer.WriteString(value);
    }

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value)
    {
        var str = reader.ReadString();
        if (str == null)
        {
            value = null;
            return;
        }

        value = Intern(str);
    }

    // The intern pool lives for the process, so data could grow it without end. Strings already in it are shared
    // as before; new ones are added only while short and while this formatter has added fewer than the cap.
    internal const int MaxInternedLength = 256;
    internal const int MaxInternedCount = 16384;
    static int internedCount;

    static string Intern(string str)
    {
        if (string.IsInterned(str) is { } interned) return interned;
        if (str.Length > MaxInternedLength || Volatile.Read(ref internedCount) >= MaxInternedCount) return str;

        Interlocked.Increment(ref internedCount);
        return string.Intern(str);
    }
}

[Preserve]
public sealed class BrotliStringFormatter : MemoryPackFormatter<string>
{
    internal const int DefaultDecompssionSizeLimit = 1024 * 1024 * 128; // 128MB

    public static readonly BrotliStringFormatter Default = new BrotliStringFormatter();

    readonly System.IO.Compression.CompressionLevel compressionLevel;
    readonly int window;
    readonly int decompressionSizeLimit;

    public BrotliStringFormatter()
        : this(System.IO.Compression.CompressionLevel.Fastest)
    {

    }

    public BrotliStringFormatter(System.IO.Compression.CompressionLevel compressionLevel)
        : this(compressionLevel, BrotliUtils.WindowBits_Default)
    {
        this.compressionLevel = compressionLevel;
    }

    public BrotliStringFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window)
        : this(compressionLevel, window, DefaultDecompssionSizeLimit)
    {
    }

    public BrotliStringFormatter(System.IO.Compression.CompressionLevel compressionLevel, int window, int decompressionSizeLimit)
    {
        this.compressionLevel = compressionLevel;
        this.window = window;
        this.decompressionSizeLimit = decompressionSizeLimit;
    }

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref string? value)
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

        var srcLength = value.Length * 2;
        var maxLength = BrotliUtils.BrotliEncoderMaxCompressedSize(srcLength);

        ref var spanRef = ref writer.GetSpanReference(maxLength + 4);
        var dest = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref spanRef, 4), maxLength);

        var status = encoder.Compress(MemoryMarshal.AsBytes(value.AsSpan()), dest, out var bytesConsumed, out var bytesWritten, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            MemoryPackSerializationException.ThrowCompressionFailed(status);
        }

        if (bytesConsumed != srcLength)
        {
            MemoryPackSerializationException.ThrowCompressionFailed();
        }

        Unsafe.WriteUnaligned(ref spanRef, value.Length);
        writer.Advance(bytesWritten + 4);
    }

    [Preserve]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref string? value)
    {
        if (!reader.DangerousTryReadCollectionHeader(out var length))
        {
            value = null;
            return;
        }

        if (length == 0)
        {
            value = "";
            return;
        }

        if (length < 0)
        {
            MemoryPackSerializationException.ThrowInvalidLength(length);
        }

        // security, require to check length. In 64 bits, so a huge length can't wrap under the limit.
        if (decompressionSizeLimit < (long)length * 2)
        {
            MemoryPackSerializationException.ThrowDecompressionSizeLimitExceeded(decompressionSizeLimit, length);
        }
        var byteLength = length * 2;

        reader.GetRemainingSource(out var singleSource, out var remainingSource);

        // The string is made once the output is in, so a stated length allocates only what the data expands to.
        using var decoder = new ExactLengthBrotliDecoder(byteLength, singleSource.Length != 0 ? singleSource.Length : (int)Math.Min(remainingSource.Length, int.MaxValue));

        int consumed;
        OperationStatus status;
        if (singleSource.Length != 0)
        {
            status = decoder.Decompress(singleSource, out consumed);
        }
        else
        {
            consumed = 0;
            status = OperationStatus.NeedMoreData;
            foreach (var item in remainingSource)
            {
                status = decoder.Decompress(item.Span, out var bytesConsumed);
                consumed += bytesConsumed;
                if (status != OperationStatus.NeedMoreData) break;
            }
        }
        if (status != OperationStatus.Done)
        {
            MemoryPackSerializationException.ThrowCompressionFailed(status);
        }

        // The stated length must be the decompressed length, or the string ends in NULs.
        value = new string(MemoryMarshal.Cast<byte, char>(decoder.GetOutput()));
        reader.Advance(consumed);
    }
}
