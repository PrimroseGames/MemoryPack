using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;

namespace MemoryPack;

public static class MemoryPackWriterOptionalStatePool
{
    static readonly ConcurrentQueue<MemoryPackWriterOptionalState> queue = new ConcurrentQueue<MemoryPackWriterOptionalState>();

    public static MemoryPackWriterOptionalState Rent(MemoryPackSerializerOptions? options)
    {
        if (!queue.TryDequeue(out var state))
        {
            state = new MemoryPackWriterOptionalState();
        }

        state.Init(options);
        return state;
    }

    internal static void Return(MemoryPackWriterOptionalState state)
    {
        state.Reset();
        queue.Enqueue(state);
    }
}

public sealed class MemoryPackWriterOptionalState : IDisposable
{
    internal static readonly MemoryPackWriterOptionalState NullState = new MemoryPackWriterOptionalState(true);

    uint nextId;
    readonly Dictionary<object, uint> objectToRef;

    public MemoryPackSerializerOptions Options { get; private set; }

    internal MemoryPackWriterOptionalState()
    {
        objectToRef = new Dictionary<object, uint>(ReferenceEqualityComparer.Instance);
        Options = null!;
        nextId = 0;
    }

    MemoryPackWriterOptionalState(bool _)
    {
        objectToRef = null!;
        Options = MemoryPackSerializerOptions.Default;
        nextId = 0;
    }

    internal void Init(MemoryPackSerializerOptions? options)
    {
        Options = options ?? MemoryPackSerializerOptions.Default;
    }

    public void Reset()
    {
        objectToRef.Clear();
        Options = null!;
        nextId = 0;
    }

    public (bool existsReference, uint id) GetOrAddReference(object value)
    {
        ref var id = ref CollectionsMarshal.GetValueRefOrAddDefault(objectToRef, value, out var exists);
        if (exists)
        {
            return (true, id);
        }
        else
        {
            id = nextId++;
            return (false, id);
        }
    }

    void IDisposable.Dispose()
    {
        MemoryPackWriterOptionalStatePool.Return(this);
    }
}
