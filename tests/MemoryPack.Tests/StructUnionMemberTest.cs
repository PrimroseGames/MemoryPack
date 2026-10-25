using System;

namespace MemoryPack.Tests;

// A NoGenerate union skips the union checks, so it can name struct members; the generated formatter
// boxes and unboxes them rather than reinterpreting the reference.
public class StructUnionMemberTest
{
    static StructUnionMessage Sample() => new() { Id = 0x11223344, Name = "seven", Payload = new byte[] { 1, 2, 3 } };

    static byte[] Expected(StructUnionMessage value) => [3, .. MemoryPackSerializer.Serialize(value)];

    static void IsSample(object? value)
    {
        var message = value.Should().BeOfType<StructUnionMessage>().Subject;
        message.Id.Should().Be(0x11223344);
        message.Name.Should().Be("seven");
        message.Payload.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void WritesTheMemberThroughTheUnion()
    {
        var value = Sample();
        MemoryPackSerializer.Serialize<IStructUnion>(value).Should().Equal(Expected(value));
        MemoryPackSerializer.Serialize(typeof(IStructUnion), value).Should().Equal(Expected(value));
    }

    [Fact]
    public void ReadsTheMemberBoxed()
    {
        var read = MemoryPackSerializer.Deserialize<IStructUnion>(Expected(Sample()));
        IsSample(read);
    }

    [Fact]
    public void ReadsOverAnExistingBoxedMember()
    {
        IStructUnion? existing = new StructUnionMessage { Id = 1, Name = "old" };
        var other = new object[] { "kept" };

        MemoryPackSerializer.Deserialize(Expected(Sample()), ref existing);

        IsSample(existing);
        other[0].Should().Be("kept");
    }

    [Fact]
    public void ArrayOfTheUnionRoundTrips()
    {
        var values = new IStructUnion?[] { Sample(), new StructUnionClass { Text = "c" }, null, new StructUnionOther { Value = -9 } };
        var read = MemoryPackSerializer.Deserialize<IStructUnion?[]>(MemoryPackSerializer.Serialize(values))!;
        IsSample(read[0]);
        read[1].Should().BeOfType<StructUnionClass>().Which.Text.Should().Be("c");
        read[2].Should().BeNull();
        read[3].Should().BeOfType<StructUnionOther>().Which.Value.Should().Be(-9);
    }
}

[MemoryPackable(GenerateType.NoGenerate)]
[MemoryPackUnion(3, typeof(StructUnionMessage))]
[MemoryPackUnion(4, typeof(StructUnionClass))]
[MemoryPackUnion(5, typeof(StructUnionOther))]
public partial interface IStructUnion
{
}

[MemoryPackable]
public partial struct StructUnionOther : IStructUnion
{
    public long Value { get; set; }
}

[MemoryPackable]
public partial struct StructUnionMessage : IStructUnion
{
    public uint Id { get; set; }
    public string? Name { get; set; }
    public byte[]? Payload { get; set; }
}

[MemoryPackable]
public partial class StructUnionClass : IStructUnion
{
    public string? Text { get; set; }
}
