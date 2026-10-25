using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack.Internal;

internal static class CollectionsMarshalEx
{
    /// <summary>Sets the list's count to <paramref name="length"/> and returns its items as a span.</summary>
    public static Span<T?> CreateSpan<T>(List<T?> list, int length)
    {
        CollectionsMarshal.SetCount(list, length);
        return CollectionsMarshal.AsSpan(list);
    }

    public static Span<T?> AsSpan<T>(Stack<T?> stack)
    {
        return StackFields<T?>.Items(stack).AsSpan(0, StackFields<T?>.Size(stack));
    }

    /// <summary>Sets the stack's count to <paramref name="length"/> and returns its items, bottom first, as a span.</summary>
    public static Span<T?> CreateSpan<T>(Stack<T?> stack, int length)
    {
        stack.EnsureCapacity(length);
        StackFields<T?>.Size(stack) = length;
        return StackFields<T?>.Items(stack).AsSpan(0, length);
    }

    // Stack<T> has no public span access. These bind to its fields by name, so a runtime that renames them
    // throws MissingFieldException instead of the stack's memory being misread.
    static class StackFields<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_array")]
        public static extern ref T[] Items(Stack<T> stack);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
        public static extern ref int Size(Stack<T> stack);
    }
}
