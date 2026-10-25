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

    // Nullable<T> keeps its value in a private field; this checks it in place. Its has-value flag is the first byte,
    // and a writer only writes 0 or 1 there: lifted operators AND the flag in, so a 2 makes `x > max` false while
    // `x.Value` still returns the value. Without a value the value bytes are cleared, so equal nulls compare equal.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RunNullable<T>(ref T? value)
        where T : struct
    {
        var hasValue = Unsafe.As<T?, byte>(ref value);
        if (hasValue == 0)
        {
            value = default;
            return;
        }
        if (hasValue != 1)
        {
            MemoryPackSerializationException.ThrowNonCanonicalNullable(typeof(T));
        }
        if (UnmanagedValidator<T>.IsRegistered)
        {
            UnmanagedValidator<T>.Run(ref Unsafe.AsRef(in Nullable.GetValueRefOrDefaultRef(in value)));
        }
    }

    // The validator for every raw-copied T?, so arrays and spans of T? are checked too.
    public static void ValidateNullables<T>(Span<T?> values)
        where T : struct
    {
        foreach (ref var value in values)
        {
            RunNullable(ref value);
        }
    }

    // Built-in structs a raw copy can leave outside what their constructors allow. The generator registers these
    // for the types a generated type reaches.

    public static void ValidateDateTimes(Span<DateTime> values)
    {
        // Ticks are the low 62 bits; all four kinds are valid.
        foreach (var value in values)
        {
            if ((ulong)value.Ticks > (ulong)DateTime.MaxValue.Ticks) MemoryPackSerializationException.ThrowInvalidValue(typeof(DateTime));
        }
    }

    public static void ValidateDateTimeOffsets(Span<DateTimeOffset> values)
    {
        const long maxOffset = 14 * TimeSpan.TicksPerHour;
        foreach (var value in values)
        {
            var utc = value.UtcTicks;
            var offset = value.Offset.Ticks;
            if ((ulong)utc > (ulong)DateTime.MaxValue.Ticks
             || offset < -maxOffset || offset > maxOffset
             || (ulong)(utc + offset) > (ulong)DateTime.MaxValue.Ticks)
            {
                MemoryPackSerializationException.ThrowInvalidValue(typeof(DateTimeOffset));
            }
        }
    }

    public static void ValidateDateOnlys(Span<DateOnly> values)
    {
        foreach (var value in values)
        {
            if ((uint)value.DayNumber > (uint)DateOnly.MaxValue.DayNumber) MemoryPackSerializationException.ThrowInvalidValue(typeof(DateOnly));
        }
    }

    public static void ValidateTimeOnlys(Span<TimeOnly> values)
    {
        foreach (var value in values)
        {
            if ((ulong)value.Ticks > (ulong)TimeOnly.MaxValue.Ticks) MemoryPackSerializationException.ThrowInvalidValue(typeof(TimeOnly));
        }
    }

    public static void ValidateDecimals(Span<decimal> values)
    {
        // The flags word holds the sign in bit 31 and the scale, at most 28, in bits 16-23; the rest is zero.
        Span<int> bits = stackalloc int[4];
        foreach (var value in values)
        {
            decimal.GetBits(value, bits);
            var flags = bits[3];
            if ((flags & 0x7F00FFFF) != 0 || ((flags >> 16) & 0xFF) > 28) MemoryPackSerializationException.ThrowInvalidValue(typeof(decimal));
        }
    }

    public static void ValidateRunes(Span<System.Text.Rune> values)
    {
        foreach (var value in values)
        {
            if (!System.Text.Rune.IsValid(value.Value)) MemoryPackSerializationException.ThrowInvalidValue(typeof(System.Text.Rune));
        }
    }
}
