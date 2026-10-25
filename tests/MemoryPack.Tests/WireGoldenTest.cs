using MemoryPack.Compression;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;

namespace MemoryPack.Tests;

// Pins the wire format. Each case is checked three ways against a stored .bin under Golden/:
// serializing the value gives the stored bytes, the stored bytes deserialize to the value, and
// re-serializing what was read gives the stored bytes again. A missing .bin is written on the
// first run and the case fails, so the capture is a deliberate step. Never regenerate a .bin to
// make a case pass: a mismatch means the format moved.
public class WireGoldenTest
{
    [Fact] public void Primitives() => Check(nameof(Primitives), GoldenData.Primitives());
    [Fact] public void Strings() => Check(nameof(Strings), GoldenData.Strings());
    [Fact] public void StringsUtf16() => Check(nameof(StringsUtf16), GoldenData.Strings(), MemoryPackSerializerOptions.Utf16);
    [Fact] public void Collections() => Check(nameof(Collections), GoldenData.Collections());
    [Fact] public void Union() => Check<IGoldenUnion[]>(nameof(Union), GoldenData.Union());
    [Fact] public void VersionTolerant() => Check(nameof(VersionTolerant), GoldenData.VersionTolerant());
    [Fact] public void CircularReference() => Check(nameof(CircularReference), GoldenData.Circular());
    [Fact] public void Unusual() => Check(nameof(Unusual), GoldenData.Unusual());

    // Top-level arrays of unmanaged elements, and an empty and a null one.
    [Fact] public void TopLevelIntArray() => Check(nameof(TopLevelIntArray), new[] { 3, -4, int.MaxValue });
    [Fact] public void TopLevelStructArray() => Check(nameof(TopLevelStructArray), new[] { new GoldenPoint { X = 1, Y = -2.5f, Z = true }, new GoldenPoint { X = 7, Y = 0, Z = false } });
    [Fact] public void TopLevelEmptyArray() => Check(nameof(TopLevelEmptyArray), Array.Empty<long>());
    [Fact] public void TopLevelNullArray() => Check<short[]?>(nameof(TopLevelNullArray), null);
    [Fact] public void TopLevelPackableArray() => Check(nameof(TopLevelPackableArray), new[] { new GoldenLeaf { Id = 9, Name = "nine" }, null });

    // Brotli output depends on the runtime's encoder, so only the read side is pinned.
    [Fact]
    public void BrotliRead()
    {
        var value = GoldenData.Brotli();
        var golden = LoadOrCapture(nameof(BrotliRead), () => MemoryPackSerializer.Serialize(value));
        MemoryPackSerializer.Deserialize<GoldenBrotli>(golden).Should().BeEquivalentTo(value);
    }

    static void Check<T>(string name, T value, MemoryPackSerializerOptions? options = null)
    {
        var golden = LoadOrCapture(name, () => MemoryPackSerializer.Serialize(value, options));

        MemoryPackSerializer.Serialize(value, options).Should().Equal(golden, "serializing {0} must give the stored bytes", name);

        var read = MemoryPackSerializer.Deserialize<T>(golden, options);
        read.Should().BeEquivalentTo(value, o => o.IgnoringCyclicReferences().WithStrictOrdering().RespectingRuntimeTypes());

        MemoryPackSerializer.Serialize(read, options).Should().Equal(golden, "re-serializing what {0} read must give the stored bytes", name);
    }

    static byte[] LoadOrCapture(string name, Func<byte[]> capture, [CallerFilePath] string thisFile = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", name + ".bin");
        if (File.Exists(path)) return File.ReadAllBytes(path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, capture());
        throw new InvalidOperationException($"Captured {path}. Check it in and run again.");
    }
}

static class GoldenData
{
    public static GoldenPrimitives Primitives() => new()
    {
        B = true, U8 = 0xAB, I16 = -12345, U16 = 54321, I32 = int.MinValue + 7, U32 = 0xDEADBEEF,
        I64 = long.MaxValue - 3, U64 = 0x0123456789ABCDEF, F32 = 3.25f, F64 = -1e-300, Dec = 79228162514264337593543950.335m,
        Ch = 'Ω', Guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), Time = new DateTime(2026, 10, 25, 12, 34, 56, DateTimeKind.Utc),
        Offset = new DateTimeOffset(2026, 10, 25, 12, 34, 56, TimeSpan.FromHours(9)), Span = TimeSpan.FromTicks(123456789),
        Kind = GoldenKind.Second, NullableSet = 42, NullableUnset = null, Point = new GoldenPoint { X = -5, Y = 1.5f, Z = true },
        NullablePoint = new GoldenPoint { X = 9, Y = -0.25f, Z = false },
    };

    public static GoldenStrings Strings() => new()
    {
        Ascii = "hello, world",
        Multibyte = "日本語 ✓ émoji 🌸",
        Empty = "",
        Null = null,
        Long = new string('x', 300) + "ü",
    };

    public static GoldenCollections Collections() => new()
    {
        Ints = new[] { 1, -2, 3, int.MaxValue },
        EmptyInts = Array.Empty<int>(),
        NullInts = null,
        Points = new[] { new GoldenPoint { X = 1, Y = 2, Z = true }, new GoldenPoint { X = -3, Y = 4, Z = false } },
        Bytes = new byte[] { 0, 1, 2, 254, 255 },
        Strings = new List<string?> { "a", null, "ccc" },
        Map = new Dictionary<int, string> { [3] = "three", [1] = "one", [2] = "two" },
        Set = new HashSet<int> { 5, 1, 9 },
        Nested = new List<List<int>> { new() { 1, 2 }, new(), new() { 3 } },
        Sorted = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 },
        ImmutableList = ImmutableList.Create(7, 8, 9),
        ImmutableMap = ImmutableDictionary<int, int>.Empty.Add(10, 100).Add(20, 200).Add(30, 300),
        ImmutableSet = ImmutableHashSet.Create(11, 22, 33),
        Packables = new List<GoldenLeaf> { new() { Id = 1, Name = "one" }, new() { Id = 2, Name = null } },
    };

    public static IGoldenUnion[] Union() => new IGoldenUnion[]
    {
        new GoldenUnionA { Value = 77 },
        new GoldenUnionB { Text = "bee", Inner = new GoldenUnionA { Value = -1 } },
        new GoldenUnionB { Text = null, Inner = null },
    };

    public static GoldenVersionTolerant VersionTolerant() => new()
    {
        First = 1, Second = "two", Fourth = new[] { 4L, 44L }, Leaf = new GoldenLeaf { Id = 5, Name = "five" },
    };

    public static GoldenNode Circular()
    {
        var root = new GoldenNode { Id = 1 };
        var child = new GoldenNode { Id = 2, Parent = root };
        var other = new GoldenNode { Id = 3, Parent = root };
        root.Children = new[] { child, other, child };
        return root;
    }

    public static GoldenUnusual Unusual()
    {
        var grid = new int[2, 3];
        for (var i = 0; i < 2; i++)
            for (var j = 0; j < 3; j++)
                grid[i, j] = i * 10 + j;

        return new GoldenUnusual
        {
            Grid = grid,
            Bits = new BitArray(new[] { true, false, true, true, false, false, false, true, true }),
            Tuple = (1, "x", 2.5),
            Pair = new KeyValuePair<string, int>("k", 3),
            Uri = new Uri("https://example.com/a?b=c"),
            Version = new Version(1, 2, 3, 4),
            Utf16 = "wide ✓",
            Utf8 = "narrow ✓",
            Packed = new[] { true, true, false, true, false, false, true, false, true, true },
            IgnoreCase = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Key"] = "v", ["other"] = null },
        };
    }

    public static GoldenBrotli Brotli() => new()
    {
        Payload = System.Text.Encoding.UTF8.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("compressible ", 200))),
        Text = string.Concat(System.Linq.Enumerable.Repeat("brotli string ", 100)),
    };
}

public enum GoldenKind : short { First = 1, Second = 2, Third = 300 }

public struct GoldenPoint
{
    public int X;
    public float Y;
    public bool Z;
}

[MemoryPackable]
public partial class GoldenPrimitives
{
    public bool B { get; set; }
    public byte U8 { get; set; }
    public short I16 { get; set; }
    public ushort U16 { get; set; }
    public int I32 { get; set; }
    public uint U32 { get; set; }
    public long I64 { get; set; }
    public ulong U64 { get; set; }
    public float F32 { get; set; }
    public double F64 { get; set; }
    public decimal Dec { get; set; }
    public char Ch { get; set; }
    public Guid Guid { get; set; }
    public DateTime Time { get; set; }
    public DateTimeOffset Offset { get; set; }
    public TimeSpan Span { get; set; }
    public GoldenKind Kind { get; set; }
    public int? NullableSet { get; set; }
    public int? NullableUnset { get; set; }
    public GoldenPoint Point { get; set; }
    public GoldenPoint? NullablePoint { get; set; }
}

[MemoryPackable]
public partial class GoldenStrings
{
    public string? Ascii { get; set; }
    public string? Multibyte { get; set; }
    public string? Empty { get; set; }
    public string? Null { get; set; }
    public string? Long { get; set; }
}

[MemoryPackable]
public partial class GoldenLeaf
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

[MemoryPackable]
public partial class GoldenCollections
{
    public int[]? Ints { get; set; }
    public int[]? EmptyInts { get; set; }
    public int[]? NullInts { get; set; }
    public GoldenPoint[]? Points { get; set; }
    public byte[]? Bytes { get; set; }
    public List<string?>? Strings { get; set; }
    public Dictionary<int, string>? Map { get; set; }
    public HashSet<int>? Set { get; set; }
    public List<List<int>>? Nested { get; set; }
    public SortedDictionary<string, int>? Sorted { get; set; }
    public ImmutableList<int>? ImmutableList { get; set; }
    // Hash-ordered: re-serializing these depends on the comparer the reader builds them with.
    public ImmutableDictionary<int, int>? ImmutableMap { get; set; }
    public ImmutableHashSet<int>? ImmutableSet { get; set; }
    public List<GoldenLeaf>? Packables { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(GoldenUnionA))]
[MemoryPackUnion(5, typeof(GoldenUnionB))]
public partial interface IGoldenUnion
{
}

[MemoryPackable]
public partial class GoldenUnionA : IGoldenUnion
{
    public int Value { get; set; }
}

[MemoryPackable]
public partial class GoldenUnionB : IGoldenUnion
{
    public string? Text { get; set; }
    public IGoldenUnion? Inner { get; set; }
}

[MemoryPackable(GenerateType.VersionTolerant)]
public partial class GoldenVersionTolerant
{
    [MemoryPackOrder(0)] public int First { get; set; }
    [MemoryPackOrder(1)] public string? Second { get; set; }
    [MemoryPackOrder(3)] public long[]? Fourth { get; set; }
    [MemoryPackOrder(4)] public GoldenLeaf? Leaf { get; set; }
}

[MemoryPackable(GenerateType.CircularReference)]
public partial class GoldenNode
{
    [MemoryPackOrder(0)] public int Id { get; set; }
    [MemoryPackOrder(1)] public GoldenNode? Parent { get; set; }
    [MemoryPackOrder(2)] public GoldenNode[]? Children { get; set; }
}

[MemoryPackable]
public partial class GoldenUnusual
{
    public int[,]? Grid { get; set; }
    public BitArray? Bits { get; set; }
    // No BigInteger: its writer emits the unused tail of its scratch buffer, so every value reads back as 0.
    public (int, string, double) Tuple { get; set; }
    public KeyValuePair<string, int> Pair { get; set; }
    public Uri? Uri { get; set; }
    public Version? Version { get; set; }
    [Utf16StringFormatter] public string? Utf16 { get; set; }
    [Utf8StringFormatter] public string? Utf8 { get; set; }
    [BitPackFormatter] public bool[]? Packed { get; set; }
    [OrdinalIgnoreCaseStringDictionaryFormatter<string>] public Dictionary<string, string?>? IgnoreCase { get; set; }
}

[MemoryPackable]
public partial class GoldenBrotli
{
    [BrotliFormatter] public byte[]? Payload { get; set; }
    [BrotliStringFormatter] public string? Text { get; set; }
}
