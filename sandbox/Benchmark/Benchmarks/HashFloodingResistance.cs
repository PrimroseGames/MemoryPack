using BenchmarkDotNet.Attributes;
using MemoryPack;
using System.Collections.Generic;
using System.Linq;

namespace Benchmark.Benchmarks;

// What the keyed key hash costs reading hash collections, against the default comparers. The shapes mirror
// replication: small and mid-size maps and sets keyed by integer ids.
[MemoryDiagnoser]
public class HashFloodingResistance
{
    static readonly MemoryPackSerializerOptions Resistant = MemoryPackSerializerOptions.Default;
    static readonly MemoryPackSerializerOptions Plain = MemoryPackSerializerOptions.Default with { HashFloodingResistant = false };

    [Params(8, 256, 16384)]
    public int Count;

    byte[] intMap = default!;
    byte[] uintSet = default!;
    byte[] longMap = default!;
    byte[] stringMap = default!;

    [GlobalSetup]
    public void Setup()
    {
        intMap = MemoryPackSerializer.Serialize(Enumerable.Range(0, Count).ToDictionary(i => i * 7919, i => i));
        uintSet = MemoryPackSerializer.Serialize(new HashSet<uint>(Enumerable.Range(0, Count).Select(i => (uint)i * 2654435761u)));
        longMap = MemoryPackSerializer.Serialize(Enumerable.Range(0, Count).ToDictionary(i => (long)i << 20, i => i.ToString()));
        stringMap = MemoryPackSerializer.Serialize(Enumerable.Range(0, Count).ToDictionary(i => "key" + i, i => i));
    }

    [Benchmark(Baseline = true)] public Dictionary<int, int>? IntMapPlain() => MemoryPackSerializer.Deserialize<Dictionary<int, int>>(intMap, Plain);
    [Benchmark] public Dictionary<int, int>? IntMapResistant() => MemoryPackSerializer.Deserialize<Dictionary<int, int>>(intMap, Resistant);

    [Benchmark] public HashSet<uint>? UIntSetPlain() => MemoryPackSerializer.Deserialize<HashSet<uint>>(uintSet, Plain);
    [Benchmark] public HashSet<uint>? UIntSetResistant() => MemoryPackSerializer.Deserialize<HashSet<uint>>(uintSet, Resistant);

    [Benchmark] public Dictionary<long, string>? LongMapPlain() => MemoryPackSerializer.Deserialize<Dictionary<long, string>>(longMap, Plain);
    [Benchmark] public Dictionary<long, string>? LongMapResistant() => MemoryPackSerializer.Deserialize<Dictionary<long, string>>(longMap, Resistant);

    // String keys take the default either way; this row shows the option costs them nothing.
    [Benchmark] public Dictionary<string, int>? StringMapResistant() => MemoryPackSerializer.Deserialize<Dictionary<string, int>>(stringMap, Resistant);
}
