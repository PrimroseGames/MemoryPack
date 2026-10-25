using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MemoryPack.Tests;

// Hash collections read from data hash value-like keys with a keyed hash, so the data can't choose colliding keys.
public class SecurityHashFloodingTest
{
    static T RoundTrip<T>(T value, MemoryPackSerializerOptions? options = null)
        => MemoryPackSerializer.Deserialize<T>(MemoryPackSerializer.Serialize(value), options)!;

    static void IsResistant<T>(IEqualityComparer<T> comparer)
    {
        comparer.Should().NotBeSameAs(EqualityComparer<T>.Default);
        comparer.GetType().Namespace.Should().Be("MemoryPack.Internal");
    }

    [Fact]
    public void ConcreteCollectionsGetAResistantComparer()
    {
        var dict = RoundTrip(Enumerable.Range(0, 100).ToDictionary(x => x, x => x.ToString()));
        IsResistant(dict.Comparer);
        dict[42].Should().Be("42");

        var set = RoundTrip(new HashSet<long>(Enumerable.Range(0, 100).Select(x => (long)x << 32)));
        IsResistant(set.Comparer);
        set.Should().Contain(5L << 32);

        var concurrent = RoundTrip(new ConcurrentDictionary<Guid, int>(new[] { KeyValuePair.Create(Guid.NewGuid(), 1) }));
        IsResistant(concurrent.Comparer);
    }

    [Fact]
    public void InterfaceFrozenAndImmutableCollectionsGetAResistantComparer()
    {
        var map = RoundTrip<IDictionary<int, int>>(new Dictionary<int, int> { [1] = 2 });
        IsResistant(((Dictionary<int, int>)map).Comparer);

        var readOnly = RoundTrip<IReadOnlyDictionary<ushort, int>>(new Dictionary<ushort, int> { [1] = 2 });
        IsResistant(((Dictionary<ushort, int>)readOnly).Comparer);

        var iset = RoundTrip<ISet<DayOfWeek>>(new HashSet<DayOfWeek> { DayOfWeek.Friday });
        IsResistant(((HashSet<DayOfWeek>)iset).Comparer);

        var lookup = RoundTrip(Enumerable.Range(0, 10).ToLookup(x => x % 3));
        lookup[1].Should().Equal(1, 4, 7);

        var frozen = RoundTrip(new Dictionary<int, int> { [1] = 2, [3] = 4 }.ToFrozenDictionary());
        IsResistant(frozen.Comparer);
        frozen[3].Should().Be(4);

        var frozenSet = RoundTrip(new[] { 1u, 2u, 3u }.ToFrozenSet());
        IsResistant(frozenSet.Comparer);

        var immutable = RoundTrip(ImmutableDictionary.CreateRange(new[] { KeyValuePair.Create(7L, "a") }));
        IsResistant(immutable.KeyComparer);

        var immutableSet = RoundTrip(ImmutableHashSet.Create(1, 2, 3));
        IsResistant(immutableSet.KeyComparer);
    }

    [Fact]
    public void StringKeysKeepTheDefault()
    {
        // .NET already randomizes string hashing per process.
        var dict = RoundTrip(new Dictionary<string, int> { ["a"] = 1 });
        dict.Comparer.Should().BeSameAs(EqualityComparer<string>.Default);
    }

    [Fact]
    public void TheOptionTurnsItOff()
    {
        var options = MemoryPackSerializerOptions.Default with { HashFloodingResistant = false };
        var dict = RoundTrip(new Dictionary<int, int> { [1] = 2 }, options);
        dict.Comparer.Should().BeSameAs(EqualityComparer<int>.Default);
    }

    [Fact]
    public void AnExistingCollectionOnTheDefaultComparerIsReplaced()
    {
        var bytes = MemoryPackSerializer.Serialize(new Dictionary<int, int> { [1] = 2 });

        var existing = new Dictionary<int, int> { [9] = 9 };
        MemoryPackSerializer.Deserialize(bytes, ref existing);
        IsResistant(existing!.Comparer);
        existing.Keys.Should().Equal(1);

        // A comparer the caller chose is theirs to keep.
        var custom = new ReverseComparer();
        var chosen = new Dictionary<int, int>(custom) { [9] = 9 };
        var before = chosen;
        MemoryPackSerializer.Deserialize(bytes, ref chosen);
        chosen.Should().BeSameAs(before);
        chosen!.Comparer.Should().BeSameAs(custom);
        chosen.Keys.Should().Equal(1);
    }

    sealed class ReverseComparer : IEqualityComparer<int>
    {
        public bool Equals(int x, int y) => x == y;
        public int GetHashCode(int obj) => ~obj;
    }

    [Fact]
    public void EnumerationOrderIsUnchanged()
    {
        // Re-serializing writes what was read, since Dictionary and HashSet enumerate in insertion order.
        var original = new Dictionary<int, int>();
        foreach (var k in new[] { 50, 3, 99, 1, 77, 12 }) original[k] = k;
        var bytes = MemoryPackSerializer.Serialize(original);
        MemoryPackSerializer.Serialize(MemoryPackSerializer.Deserialize<Dictionary<int, int>>(bytes)).Should().Equal(bytes);
    }

    [Fact]
    public void EqualValuesHashEqual()
    {
        static void Same<T>(T a, T b)
        {
            var comparer = RoundTrip(new HashSet<T> { a }).Comparer;
            comparer.Equals(a, b).Should().BeTrue();
            comparer.GetHashCode(a!).Should().Be(comparer.GetHashCode(b!));
        }

        Same(0.0, -0.0);
        Same(double.NaN, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001)));
        Same(0f, -0f);
        Same((Half)0, (Half)(-0f));
        Same(1.0m, 1.00m);
        Same(0m, -0m);
        Same(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local));
        Same(new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 1, 1, 13, 0, 0, TimeSpan.FromHours(1)));
        Same<int?>(5, 5);
        Same<int?>(null, null);
    }

    [Fact]
    public void KeysThatCollideByDefaultSpreadOut()
    {
        // Multiples of a bucket count all land in one bucket under the default int hash.
        const int prime = 1_048_583;
        var keys = Enumerable.Range(0, 4096).Select(i => i * prime).ToHashSet();
        var comparer = RoundTrip(keys).Comparer;
        keys.Select(k => (uint)comparer.GetHashCode(k) % prime).Distinct().Count().Should().BeGreaterThan(4000);
    }
}
