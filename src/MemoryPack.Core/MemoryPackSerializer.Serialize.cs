using MemoryPack.Internal;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack;

using static MemoryMarshal;
using static GC;

public static partial class MemoryPackSerializer
{
    [ThreadStatic]
    static SerializerWriterThreadStaticState? threadStaticState;
    [ThreadStatic]
    static MemoryPackWriterOptionalState? threadStaticWriterOptionalState;

    public static byte[] Serialize<T>(in T? value, MemoryPackSerializerOptions? options = default)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            var array = AllocateUninitializedArray<byte>(Unsafe.SizeOf<T>());
            Unsafe.WriteUnaligned(ref GetArrayDataReference(array), value);
            return array;
        }

        var state = threadStaticState;
        if (state == null)
        {
            state = threadStaticState = new SerializerWriterThreadStaticState();
        }
        state.Init(options);

        try
        {
            var writer = new MemoryPackWriter<ReusableLinkedArrayBufferWriter>(ref state.BufferWriter, state.BufferWriter.DangerousGetFirstBuffer(), state.OptionalState);
            Serialize(ref writer, value);
            return state.BufferWriter.ToArrayAndReset();
        }
        finally
        {
            state.Reset();
        }
    }

    public static unsafe void Serialize<T, TBufferWriter>(in TBufferWriter bufferWriter, in T? value, MemoryPackSerializerOptions? options = default)
        where TBufferWriter : IBufferWriter<byte>
    {
        ref var bufferWriterRef = ref Unsafe.AsRef(in bufferWriter);
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            var buffer = bufferWriterRef.GetSpan(Unsafe.SizeOf<T>());
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buffer), value);
            bufferWriterRef.Advance(Unsafe.SizeOf<T>());
            return;
        }

        var state = threadStaticWriterOptionalState;
        if (state == null)
        {
            state = threadStaticWriterOptionalState = new MemoryPackWriterOptionalState();
        }
        state.Init(options);

        try
        {
            var writer = new MemoryPackWriter<TBufferWriter>(ref bufferWriterRef, state);
            Serialize(ref writer, value);
        }
        finally
        {
            state.Reset();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Serialize<T, TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, in T? value)
        where TBufferWriter : IBufferWriter<byte>
    {
        writer.WriteValue(value);
        writer.Flush();
    }

    public static async ValueTask SerializeAsync<T>(Stream stream, T? value, MemoryPackSerializerOptions? options = default, CancellationToken cancellationToken = default)
    {
        var tempWriter = ReusableLinkedArrayBufferWriterPool.Rent();
        try
        {
            Serialize(tempWriter, value, options);
            await tempWriter.WriteToAndResetAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReusableLinkedArrayBufferWriterPool.Return(tempWriter);
        }
    }

    sealed class SerializerWriterThreadStaticState
    {
        public ReusableLinkedArrayBufferWriter BufferWriter;
        public MemoryPackWriterOptionalState OptionalState;

        public SerializerWriterThreadStaticState()
        {
            BufferWriter = new ReusableLinkedArrayBufferWriter(useFirstBuffer: true, pinned: true);
            OptionalState = new MemoryPackWriterOptionalState();
        }

        public void Init(MemoryPackSerializerOptions? options)
        {
            OptionalState.Init(options);
        }

        public void Reset()
        {
            BufferWriter.Reset();
            OptionalState.Reset();
        }
    }
}
