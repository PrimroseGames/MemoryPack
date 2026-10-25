using MemoryPack.Internal;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MemoryPack;

public static class MemoryPackReaderOptionalStatePool
{
    static readonly ConcurrentQueue<MemoryPackReaderOptionalState> queue = new ConcurrentQueue<MemoryPackReaderOptionalState>();

    public static MemoryPackReaderOptionalState Rent(MemoryPackSerializerOptions? options)
    {
        if (!queue.TryDequeue(out var state))
        {
            state = new MemoryPackReaderOptionalState { pooled = true };
        }

        state.Init(options);
        return state;
    }

    internal static void Return(MemoryPackReaderOptionalState state)
    {
        state.Reset();
        queue.Enqueue(state);
    }
}

public sealed class MemoryPackReaderOptionalState : IDisposable
{
    // The innermost state a top-level read on this thread is reading with. A state opened inside that read (a
    // formatter calling back into MemoryPackSerializer) is nested in it, so it carries on from its depth.
    [ThreadStatic]
    static MemoryPackReaderOptionalState? reading;

    readonly Dictionary<uint, object> refToObject;
    MemoryPackReaderOptionalState? outer;
    internal bool pooled;
    int depth;
    int maxDepth;

    public MemoryPackSerializerOptions Options { get; private set; }

    internal bool IsReading { get; private set; }

    internal MemoryPackReaderOptionalState()
    {
        refToObject = new Dictionary<uint, object>();
        Options = null!;
    }

    internal void Init(MemoryPackSerializerOptions? options)
    {
        Options = options ?? MemoryPackSerializerOptions.Default;
        maxDepth = Options.MaxDepth == 0 ? int.MaxValue : Options.MaxDepth;
        depth = reading?.depth ?? 0;

        // A read can start anywhere on the stack, and one nested through formatters may never reach a probing level.
        StackGuard.Probe();
    }

    internal void BeginRead()
    {
        outer = reading;
        reading = this;
        IsReading = true;
    }

    internal void EndRead()
    {
        reading = outer;
        outer = null;
        IsReading = false;
    }

    /// <summary>
    /// Enters one level of nesting. A formatter that reads a value which can hold further values of its own kind
    /// calls this before reading them and <see cref="ExitDepth"/> after, so that deep data throws rather than
    /// exhausting the stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterDepth()
    {
        if (++depth > maxDepth)
        {
            MemoryPackSerializationException.ThrowDeserializeDepthExceeded(maxDepth);
        }
        if ((depth & StackGuard.ProbeMask) == 0)
        {
            StackGuard.Probe();
        }
    }

    /// <summary>Leaves the level entered by <see cref="EnterDepth"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExitDepth() => depth--;

    public object GetObjectReference(uint id)
    {
        if (refToObject.TryGetValue(id, out var value))
        {
            return value;
        }
        MemoryPackSerializationException.ThrowMessage("Object is not found in this reference id:" + id);
        return null!;
    }

    public void AddObjectReference(uint id, object value)
    {
        if (!refToObject.TryAdd(id, value))
        {
            MemoryPackSerializationException.ThrowMessage("Object is already added, id:" + id);
        }
    }

    public void Reset()
    {
        refToObject.Clear();
        Options = null!;
        depth = 0;
    }

    void IDisposable.Dispose()
    {
        MemoryPackReaderOptionalStatePool.Return(this);
    }
}
