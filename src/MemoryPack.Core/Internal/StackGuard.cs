using System.Runtime.CompilerServices;

namespace MemoryPack.Internal;

// The stack half of the depth guard. MaxDepth counts levels, but a level's frames vary in size, so a payload within
// MaxDepth can still overflow a small thread stack, which ends the process. The runtime guarantees 128 KB (64-bit)
// past a successful probe, which covers the 16 levels between probes.
internal static class StackGuard
{
    public const int ProbeMask = 15;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Probe()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            MemoryPackSerializationException.ThrowInsufficientStack();
        }
    }
}
