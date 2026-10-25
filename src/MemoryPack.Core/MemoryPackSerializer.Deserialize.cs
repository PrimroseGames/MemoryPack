using MemoryPack.Internal;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack;

public static partial class MemoryPackSerializer
{
    [ThreadStatic]
    static MemoryPackReaderOptionalState? threadStaticReaderOptionalState;

    // The thread's state, or a pooled one when a formatter reads again while the thread's state is still reading:
    // sharing it would clear the outer read's options and references when the inner one finished.
    static MemoryPackReaderOptionalState BeginReaderState(MemoryPackSerializerOptions? options)
    {
        var state = threadStaticReaderOptionalState ??= new MemoryPackReaderOptionalState();
        if (state.IsReading)
        {
            state = MemoryPackReaderOptionalStatePool.Rent(options);
        }
        else
        {
            state.Init(options);
        }
        state.BeginRead();
        return state;
    }

    static void EndReaderState(MemoryPackReaderOptionalState state)
    {
        state.EndRead();
        if (state.pooled)
        {
            MemoryPackReaderOptionalStatePool.Return(state);
        }
        else
        {
            state.Reset();
        }
    }

    public static T? Deserialize<T>(ReadOnlySpan<byte> buffer, MemoryPackSerializerOptions? options = default)
    {
        T? value = default;
        Deserialize(buffer, ref value, options);
        return value;
    }

    public static int Deserialize<T>(ReadOnlySpan<byte> buffer, ref T? value, MemoryPackSerializerOptions? options = default)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            if (buffer.Length < Unsafe.SizeOf<T>())
            {
                MemoryPackSerializationException.ThrowInvalidRange(Unsafe.SizeOf<T>(), buffer.Length);
            }
            value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buffer));
            UnmanagedValidator<T?>.Run(ref value);
            return Unsafe.SizeOf<T>();
        }

        var state = BeginReaderState(options);
        var reader = new MemoryPackReader(buffer, state);
        try
        {
            reader.ReadValue(ref value);
            return reader.Consumed;
        }
        finally
        {
            reader.Dispose();
            EndReaderState(state);
        }
    }

    public static T? Deserialize<T>(in ReadOnlySequence<byte> buffer, MemoryPackSerializerOptions? options = default)
    {
        T? value = default;
        Deserialize<T>(buffer, ref value, options);
        return value;
    }

    public static int Deserialize<T>(in ReadOnlySequence<byte> buffer, ref T? value, MemoryPackSerializerOptions? options = default)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            int sizeOfT = Unsafe.SizeOf<T>();
            if (buffer.Length < sizeOfT)
            {
                MemoryPackSerializationException.ThrowInvalidRange(Unsafe.SizeOf<T>(), (int)buffer.Length);
            }

            ReadOnlySequence<byte> sliced = buffer.Slice(0, sizeOfT);

            if (sliced.IsSingleSegment)
            {
                value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(sliced.FirstSpan));
                UnmanagedValidator<T?>.Run(ref value);
                return sizeOfT;
            }
            else
            {
                // We can't read directly from ReadOnlySequence<byte> to T, so we copy to a temp array.
                // if less than 512 bytes, use stackalloc, otherwise use MemoryPool<byte>
                byte[]? tempArray = null;

                Span<byte> tempSpan = sizeOfT <= 512 ? stackalloc byte[sizeOfT] : default;

                try
                {
                    if (sizeOfT > 512)
                    {
                        tempArray = ArrayPool<byte>.Shared.Rent(sizeOfT);
                        tempSpan = tempArray;
                    }

                    sliced.CopyTo(tempSpan);
                    value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(tempSpan));
                    UnmanagedValidator<T?>.Run(ref value);
                    return sizeOfT;
                }
                finally
                {
                    if (tempArray is not null)
                    {
                        ArrayPool<byte>.Shared.Return(tempArray);
                    }
                }
            }
        }

        var state = BeginReaderState(options);
        var reader = new MemoryPackReader(buffer, state);
        try
        {
            reader.ReadValue(ref value);
            return reader.Consumed;
        }
        finally
        {
            reader.Dispose();
            EndReaderState(state);
        }
    }

    public static async ValueTask<T?> DeserializeAsync<T>(Stream stream, MemoryPackSerializerOptions? options = default, CancellationToken cancellationToken = default)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> streamBuffer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? value = default;
            var bytesRead = Deserialize<T>(streamBuffer.AsSpan(checked((int)ms.Position)), ref value, options);

            // Emulate that we had actually "read" from the stream.
            ms.Seek(bytesRead, SeekOrigin.Current);

            return value;
        }

        var builder = ReusableReadOnlySequenceBuilderPool.Rent();
        try
        {
            var buffer = ArrayPool<byte>.Shared.Rent(65536); // initial 64K
            var offset = 0;
            var buffered = 0L;
            var maxBuffered = (options ?? MemoryPackSerializerOptions.Default).MaxBufferedStreamLength;
            do
            {
                if (offset == buffer.Length)
                {
                    builder.Add(buffer, returnToPool: true);
                    buffer = ArrayPool<byte>.Shared.Rent(MathEx.NewArrayCapacity(buffer.Length));
                    offset = 0;
                }

                int read = 0;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // buffer is not added in builder, so return here.
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                offset += read;
                buffered += read;
                if (buffered > maxBuffered)
                {
                    // buffer is not added in builder, so return here.
                    ArrayPool<byte>.Shared.Return(buffer);
                    MemoryPackSerializationException.ThrowStreamExceedsBufferLimit(maxBuffered);
                }

                if (read == 0)
                {
                    builder.Add(buffer.AsMemory(0, offset), returnToPool: true);
                    break;

                }
            } while (true);

            // If single buffer, we can avoid ReadOnlySequence build cost.
            if (builder.TryGetSingleMemory(out var memory))
            {
                return Deserialize<T>(memory.Span, options);
            }
            else
            {
                var seq = builder.Build();
                var result = Deserialize<T>(seq, options);
                return result;
            }
        }
        finally
        {
            builder.Reset();
        }
    }
}
