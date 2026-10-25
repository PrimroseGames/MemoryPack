using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;

namespace MemoryPack.Tests;

// Deep payloads must throw MemoryPackSerializationException, never exhaust the stack.
public class SecurityDepthTest
{
    const int Deep = 100_000;

    static SecurityDepthTest()
    {
        MemoryPackFormatterProvider.Register(new ReentrantBoxFormatter());
    }

    static Exception? RunOnThread(Action action, int stackSize)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { caught = e; }
        }, stackSize);
        thread.Start();
        thread.Join();
        return caught;
    }

    static byte[] Repeat(byte[] level, int count, byte[] end)
    {
        var payload = new byte[level.Length * count + end.Length];
        for (int i = 0; i < count; i++) level.CopyTo(payload, i * level.Length);
        end.CopyTo(payload, level.Length * count);
        return payload;
    }

    // One member per level, then a null child.
    static byte[] DeepObjects(int depth) => Repeat(new byte[] { 1 }, depth, new byte[] { 255 });

    [Fact]
    public void DeepObjectChainThrows()
    {
        var act = () => MemoryPackSerializer.Deserialize<DepthNode>(DeepObjects(Deep));
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*MaxDepth*");
    }

    [Fact]
    public void DeepListChainThrows()
    {
        // One member, a list of one, then a list member that is null.
        var payload = Repeat(new byte[] { 1, 1, 0, 0, 0 }, Deep, new byte[] { 1, 255, 255, 255, 255 });
        var act = () => MemoryPackSerializer.Deserialize<DepthListNode>(payload);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*MaxDepth*");
    }

    [Fact]
    public void DeepUnionChainThrows()
    {
        // Tag 0, one member, then a null union.
        var payload = Repeat(new byte[] { 0, 1 }, Deep, new byte[] { 255 });
        var act = () => MemoryPackSerializer.Deserialize<IDepthUnion>(payload);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*MaxDepth*");
    }

    [Fact]
    public void DeepSelfCollectionThrows()
    {
        // A list of one, nested, ending in an empty list.
        var payload = Repeat(new byte[] { 1, 0, 0, 0 }, Deep, new byte[] { 0, 0, 0, 0 });
        var act = () => MemoryPackSerializer.Deserialize<DepthSelfList>(payload);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*MaxDepth*");
    }

    [Fact]
    public void EverythingTheWriterWritesReadsBack()
    {
        // The writer allows 999 nested writes: here 998 objects and the last one's null child.
        var error = RunOnThread(() =>
        {
            var root = new DepthNode();
            var node = root;
            for (int i = 1; i < 998; i++) node = node.Child = new DepthNode();

            var read = MemoryPackSerializer.Deserialize<DepthNode>(MemoryPackSerializer.Serialize(root))!;
            var count = 0;
            for (var n = read; n != null; n = n.Child) count++;
            count.Should().Be(998);
        }, 64 * 1024 * 1024);
        error.Should().BeNull();
    }

    [Fact]
    public void MaxDepthOption()
    {
        // A null child is a level too, as it is for the writer.
        var options = MemoryPackSerializerOptions.Default with { MaxDepth = 10 };
        MemoryPackSerializer.Deserialize<DepthNode>(DeepObjects(9), options).Should().NotBeNull();
        var act = () => MemoryPackSerializer.Deserialize<DepthNode>(DeepObjects(10), options);
        act.Should().Throw<MemoryPackSerializationException>().WithMessage("*MaxDepth, 10*");

        var negative = () => MemoryPackSerializerOptions.Default with { MaxDepth = -1 };
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UnlimitedDepthStillStopsBeforeTheStackRunsOut()
    {
        var options = MemoryPackSerializerOptions.Default with { MaxDepth = 0 };
        var payload = DeepObjects(Deep);
        var error = RunOnThread(() => MemoryPackSerializer.Deserialize<DepthNode>(payload, options), 512 * 1024);
        error.Should().BeOfType<MemoryPackSerializationException>().Which.Message.Should().Contain("stack");
    }

    [Fact]
    public void NestedTopLevelReadsCarryTheDepthOn()
    {
        // Each holder's box reads the next holder through a fresh top-level Deserialize.
        var bytes = new byte[] { 1, 255, 255, 255, 255 };
        for (int i = 0; i < 1500; i++)
        {
            var wrapped = new byte[5 + bytes.Length];
            wrapped[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(wrapped.AsSpan(1), bytes.Length);
            bytes.CopyTo(wrapped, 5);
            bytes = wrapped;
        }

        var error = RunOnThread(() => MemoryPackSerializer.Deserialize<ReentrantHolder>(bytes), 256 * 1024 * 1024);
        error.Should().BeOfType<MemoryPackSerializationException>().Which.Message.Should().Contain("MaxDepth");
    }

    [Fact]
    public void NestedTopLevelReadLeavesTheOuterReadIntact()
    {
        // The back reference resolves through the outer read's reference table, after the box's nested read.
        var cycle = new ReentrantCycle { Box = new ReentrantBox { Payload = MemoryPackSerializer.Serialize(new ReentrantHolder()) } };
        cycle.Back = cycle;

        var read = MemoryPackSerializer.Deserialize<ReentrantCycle>(MemoryPackSerializer.Serialize(cycle))!;
        read.Back.Should().BeSameAs(read);
        read.Box!.Inner.Should().NotBeNull();
    }
}

[MemoryPackable]
public partial class DepthNode
{
    public DepthNode? Child { get; set; }
}

[MemoryPackable]
public partial class DepthListNode
{
    public List<DepthListNode>? Children { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(DepthBranch))]
public partial interface IDepthUnion
{
}

[MemoryPackable]
public partial class DepthBranch : IDepthUnion
{
    public IDepthUnion? Child { get; set; }
}

[MemoryPackable(GenerateType.Collection)]
public partial class DepthSelfList : List<DepthSelfList>
{
}

[MemoryPackable]
public partial class ReentrantHolder
{
    [MemoryPackAllowSerialize]
    public ReentrantBox? Box { get; set; }
}

[MemoryPackable(GenerateType.CircularReference)]
public partial class ReentrantCycle
{
    [MemoryPackOrder(0), MemoryPackAllowSerialize]
    public ReentrantBox? Box { get; set; }

    [MemoryPackOrder(1)]
    public ReentrantCycle? Back { get; set; }
}

public sealed class ReentrantBox
{
    public byte[]? Payload;
    public ReentrantHolder? Inner;
}

// Reads its bytes as another holder through a top-level Deserialize, as a formatter unpacking an embedded payload would.
sealed class ReentrantBoxFormatter : MemoryPackFormatter<ReentrantBox>
{
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref ReentrantBox? value)
    {
        writer.WriteValue(value?.Payload);
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref ReentrantBox? value)
    {
        var bytes = reader.ReadValue<byte[]>();
        value = bytes == null ? null : new ReentrantBox
        {
            Payload = bytes,
            Inner = bytes.Length == 0 ? null : MemoryPackSerializer.Deserialize<ReentrantHolder>(bytes),
        };
    }
}
