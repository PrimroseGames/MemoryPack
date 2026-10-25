using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack;

// Checks for values the reader copies as raw bytes, which no formatter sees field by field: an enum
// must hold a declared value or zero, a bool is normalized to 0 or 1, and a struct checks its fields.
// The generator registers them when MemoryPackGenerator_ValidateEnumsAndBools is on. With none
// registered for T, a raw read costs one static load.
public static class UnmanagedValidator<T>
{
    public delegate void Validate(Span<T> values);

    static Validate? validator;

    public static bool IsRegistered => validator != null;

    // The first registration wins.
    public static void Register(Validate validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        Interlocked.CompareExchange(ref validator, validate, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run(Span<T> values)
    {
        var validate = validator;
        if (validate != null && !values.IsEmpty) validate(values);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Run(ref T value)
    {
        var validate = validator;
        if (validate != null) validate(new Span<T>(ref value));
    }
}

public static class MemoryPackValidation
{
    public static void NormalizeBools(Span<bool> values)
    {
        foreach (ref var b in MemoryMarshal.AsBytes(values))
        {
            if (b > 1) b = 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void NormalizeBool(ref bool value)
    {
        ref var b = ref Unsafe.As<bool, byte>(ref value);
        if (b > 1) b = 1;
    }

    // For a bool that can only be read, such as a get-only property of a struct.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RejectNonCanonicalBool(bool value, string member)
    {
        if (Unsafe.As<bool, byte>(ref value) > 1)
        {
            MemoryPackSerializationException.ThrowNonCanonicalBool(member);
        }
    }

    // Nullable<T> keeps its value in a private field; this checks it in place.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunNullable<T>(ref T? value)
        where T : struct
    {
        if (value.HasValue && UnmanagedValidator<T>.IsRegistered)
        {
            UnmanagedValidator<T>.Run(ref Unsafe.AsRef(in Nullable.GetValueRefOrDefaultRef(in value)));
        }
    }

    // The validator for T? wherever T has one, so arrays and spans of T? are checked too.
    public static void ValidateNullables<T>(Span<T?> values)
        where T : struct
    {
        foreach (ref var value in values)
        {
            RunNullable(ref value);
        }
    }
}
