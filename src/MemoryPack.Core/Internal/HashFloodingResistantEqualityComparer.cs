using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace MemoryPack.Internal;

// Comparers for the hash collections a read builds, so data can't pick keys that all land in one bucket and make
// each insert linear. Keys hash with SipHash-1-3 under a random per-process key. A built-in key type whose default
// hash is its own value (integers, enums, Guid and the like) gets one; string keeps the default, which .NET already
// randomizes per process; anything else keeps its own hash.
internal static class HashFloodingResistantEqualityComparer
{
    internal static readonly ulong Key0;
    internal static readonly ulong Key1;

    static HashFloodingResistantEqualityComparer()
    {
        Span<byte> key = stackalloc byte[16];
        RandomNumberGenerator.Fill(key);
        Key0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        Key1 = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(8));
    }

    /// <summary>The resistant comparer for <typeparamref name="T"/>, or null when it keeps the default.</summary>
    public static IEqualityComparer<T>? Get<T>() => Cache<T>.Instance;

    /// <summary>
    /// The comparer a collection read from data gets: the formatter's own when it was given one, otherwise the
    /// resistant one when the options ask for it, otherwise null for the default.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEqualityComparer<T>? Resolve<T>(IEqualityComparer<T>? own, MemoryPackSerializerOptions options)
        => own ?? (options.HashFloodingResistant ? Cache<T>.Instance : null);

    /// <summary>
    /// Whether an existing collection being read into has to be replaced rather than refilled: it runs on the
    /// default comparer while the read should use a resistant one. A comparer the caller chose is kept.
    /// </summary>
    public static bool ReplacesDefault<T>(IEqualityComparer<T>? resolved, IEqualityComparer<T> existing)
        => resolved is not null && ReferenceEquals(existing, EqualityComparer<T>.Default);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Fold(ulong h) => (int)(h ^ (h >> 32));

    static class Cache<T>
    {
        internal static readonly IEqualityComparer<T>? Instance = Create();

        static IEqualityComparer<T>? Create()
        {
            var t = typeof(T);

            if (t == typeof(bool)
             || t == typeof(char)
             || t == typeof(sbyte)
             || t == typeof(byte)
             || t == typeof(short)
             || t == typeof(ushort)
             || t == typeof(int)
             || t == typeof(uint)
             || t == typeof(long)
             || t == typeof(ulong)
             || t == typeof(nint)
             || t == typeof(nuint)
             || t == typeof(TimeSpan)
             || t == typeof(Guid)
             || t == typeof(DateOnly)
             || t == typeof(TimeOnly)
             || t == typeof(Int128)
             || t == typeof(UInt128)
             || t.IsEnum)
            {
                // Equal values of these share one bit pattern, so hashing the bits agrees with Equals.
                return Unsafe.SizeOf<T>() switch
                {
                    1 => new BitwiseHashComparer1<T>(),
                    2 => new BitwiseHashComparer2<T>(),
                    4 => new BitwiseHashComparer4<T>(),
                    8 => new BitwiseHashComparer8<T>(),
                    16 => new BitwiseHashComparer16<T>(),
                    _ => throw new InvalidOperationException($"Unexpected key size for {typeof(T)}."),
                };
            }

            if (t == typeof(float)) return (IEqualityComparer<T>)(object)SingleHashComparer.Instance;
            if (t == typeof(double)) return (IEqualityComparer<T>)(object)DoubleHashComparer.Instance;
            if (t == typeof(Half)) return (IEqualityComparer<T>)(object)HalfHashComparer.Instance;
            if (t == typeof(decimal)) return (IEqualityComparer<T>)(object)DecimalHashComparer.Instance;
            if (t == typeof(DateTime)) return (IEqualityComparer<T>)(object)DateTimeHashComparer.Instance;
            if (t == typeof(DateTimeOffset)) return (IEqualityComparer<T>)(object)DateTimeOffsetHashComparer.Instance;

            // A nullable key hashes as its value does, so it floods as easily.
            if (Nullable.GetUnderlyingType(t) is { } underlying) return CreateNullable(underlying);

            return null;
        }

        // Spelled out so each NullableHashComparer<U> is visible to NativeAOT. A nullable enum would need
        // MakeGenericType to reach, so it keeps the default.
        static IEqualityComparer<T>? CreateNullable(Type underlying)
        {
            if (underlying == typeof(bool)) return Wrap<bool>();
            if (underlying == typeof(char)) return Wrap<char>();
            if (underlying == typeof(sbyte)) return Wrap<sbyte>();
            if (underlying == typeof(byte)) return Wrap<byte>();
            if (underlying == typeof(short)) return Wrap<short>();
            if (underlying == typeof(ushort)) return Wrap<ushort>();
            if (underlying == typeof(int)) return Wrap<int>();
            if (underlying == typeof(uint)) return Wrap<uint>();
            if (underlying == typeof(long)) return Wrap<long>();
            if (underlying == typeof(ulong)) return Wrap<ulong>();
            if (underlying == typeof(nint)) return Wrap<nint>();
            if (underlying == typeof(nuint)) return Wrap<nuint>();
            if (underlying == typeof(Guid)) return Wrap<Guid>();
            if (underlying == typeof(TimeSpan)) return Wrap<TimeSpan>();
            if (underlying == typeof(DateOnly)) return Wrap<DateOnly>();
            if (underlying == typeof(TimeOnly)) return Wrap<TimeOnly>();
            if (underlying == typeof(Int128)) return Wrap<Int128>();
            if (underlying == typeof(UInt128)) return Wrap<UInt128>();
            if (underlying == typeof(float)) return Wrap<float>();
            if (underlying == typeof(double)) return Wrap<double>();
            if (underlying == typeof(Half)) return Wrap<Half>();
            if (underlying == typeof(decimal)) return Wrap<decimal>();
            if (underlying == typeof(DateTime)) return Wrap<DateTime>();
            if (underlying == typeof(DateTimeOffset)) return Wrap<DateTimeOffset>();
            return null;

            static IEqualityComparer<T> Wrap<TUnderlying>()
                where TUnderlying : struct
                => (IEqualityComparer<T>)(object)new NullableHashComparer<TUnderlying>();
        }
    }
}

sealed class BitwiseHashComparer1<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

    public int GetHashCode(T value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(Unsafe.As<T, byte>(ref value), 1, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}

sealed class BitwiseHashComparer2<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

    public int GetHashCode(T value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(Unsafe.As<T, ushort>(ref value), 2, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}

sealed class BitwiseHashComparer4<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

    public int GetHashCode(T value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(Unsafe.As<T, uint>(ref value), 4, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}

sealed class BitwiseHashComparer8<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

    public int GetHashCode(T value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(Unsafe.As<T, ulong>(ref value), 8, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}

sealed class BitwiseHashComparer16<T> : IEqualityComparer<T>
{
    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

    public int GetHashCode(T value)
    {
        ref var first = ref Unsafe.As<T, byte>(ref value);
        var bits0 = Unsafe.ReadUnaligned<ulong>(ref first);
        var bits1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, 8));
        return HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed16(bits0, bits1, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
    }
}

// Null hashes to 0 and equals only null.
sealed class NullableHashComparer<TUnderlying> : IEqualityComparer<TUnderlying?>
    where TUnderlying : struct
{
    readonly IEqualityComparer<TUnderlying> inner = HashFloodingResistantEqualityComparer.Get<TUnderlying>()!;

    public bool Equals(TUnderlying? x, TUnderlying? y)
        => x.HasValue ? y.HasValue && inner.Equals(x.GetValueOrDefault(), y.GetValueOrDefault()) : !y.HasValue;

    public int GetHashCode(TUnderlying? value) => value.HasValue ? inner.GetHashCode(value.GetValueOrDefault()) : 0;
}

// Equals treats -0 and +0 as equal, and every NaN as equal to every NaN, so both are made one pattern first.
sealed class SingleHashComparer : IEqualityComparer<float>
{
    internal static readonly SingleHashComparer Instance = new();

    public bool Equals(float x, float y) => x.Equals(y);

    public int GetHashCode(float value)
    {
        if (value == 0f) value = 0f;
        else if (float.IsNaN(value)) value = float.NaN;
        return HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(BitConverter.SingleToUInt32Bits(value), 4, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
    }
}

sealed class DoubleHashComparer : IEqualityComparer<double>
{
    internal static readonly DoubleHashComparer Instance = new();

    public bool Equals(double x, double y) => x.Equals(y);

    public int GetHashCode(double value)
    {
        if (value == 0d) value = 0d;
        else if (double.IsNaN(value)) value = double.NaN;
        return HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(BitConverter.DoubleToUInt64Bits(value), 8, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
    }
}

sealed class HalfHashComparer : IEqualityComparer<Half>
{
    internal static readonly HalfHashComparer Instance = new();

    public bool Equals(Half x, Half y) => x.Equals(y);

    public int GetHashCode(Half value)
    {
        if (value == Half.Zero) value = Half.Zero;
        else if (Half.IsNaN(value)) value = Half.NaN;
        return HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(BitConverter.HalfToUInt16Bits(value), 2, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
    }
}

// Equal decimals can differ in scale (1.0m and 1.00m) and in the sign of zero; dividing by a 1 of the largest
// scale strips trailing zeros, so equal values share one pattern.
sealed class DecimalHashComparer : IEqualityComparer<decimal>
{
    internal static readonly DecimalHashComparer Instance = new();

    public bool Equals(decimal x, decimal y) => x == y;

    public int GetHashCode(decimal value)
    {
        value = value == 0m ? 0m : value / 1.0000000000000000000000000000m;
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var bits0 = (ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        var bits1 = (ulong)(uint)bits[2] | ((ulong)(uint)bits[3] << 32);
        return HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed16(bits0, bits1, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
    }
}

// Equals compares ticks and ignores Kind, so only ticks are hashed.
sealed class DateTimeHashComparer : IEqualityComparer<DateTime>
{
    internal static readonly DateTimeHashComparer Instance = new();

    public bool Equals(DateTime x, DateTime y) => x == y;

    public int GetHashCode(DateTime value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(unchecked((ulong)value.Ticks), 8, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}

// Equals compares the UTC instant and ignores the offset, so only UTC ticks are hashed.
sealed class DateTimeOffsetHashComparer : IEqualityComparer<DateTimeOffset>
{
    internal static readonly DateTimeOffsetHashComparer Instance = new();

    public bool Equals(DateTimeOffset x, DateTimeOffset y) => x == y;

    public int GetHashCode(DateTimeOffset value)
        => HashFloodingResistantEqualityComparer.Fold(SipHash.Hash64Fixed(unchecked((ulong)value.UtcTicks), 8, HashFloodingResistantEqualityComparer.Key0, HashFloodingResistantEqualityComparer.Key1));
}
