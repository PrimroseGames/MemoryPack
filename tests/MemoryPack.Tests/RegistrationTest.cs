using System.Collections.Generic;
using System.IO.Compression;

namespace MemoryPack.Tests;

// Formatters register without reflecting over the type: the module initializer runs every
// nameable [MemoryPackable] type's static constructor, and the provider runs it for the rest.
public class RegistrationTest
{
    [Fact]
    public void ModuleInitializerRegistersUntouchedType()
    {
        // Nothing else names RegistrationProbe, so only the module initializer could have registered it.
        MemoryPackFormatterProvider.IsRegistered<RegistrationProbe>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<RegistrationProbe[]>().Should().BeTrue();
    }

    [Fact]
    public void ClosedGenericRegistersThroughItsStaticConstructor()
    {
        var value = new GenericRegistrationProbe<long> { Value = 42 };
        var bin = MemoryPackSerializer.Serialize(value);
        MemoryPackSerializer.Deserialize<GenericRegistrationProbe<long>>(bin)!.Value.Should().Be(42);
    }

    [Fact]
    public void NonGenericLookupRegistersThroughStaticConstructor()
    {
        var bin = MemoryPackSerializer.Serialize(typeof(NonGenericRegistrationProbe), new NonGenericRegistrationProbe { Text = "x" });
        var read = (NonGenericRegistrationProbe)MemoryPackSerializer.Deserialize(typeof(NonGenericRegistrationProbe), bin)!;
        read.Text.Should().Be("x");
    }

    [Fact]
    public void UnregisteredUnmanagedStructGetsFormatter()
    {
        var value = new UnregisteredStruct { A = 3, B = 4.5 };
        var bin = MemoryPackSerializer.Serialize(new UnregisteredStructHolder { Items = new() { value } });
        MemoryPackSerializer.Deserialize<UnregisteredStructHolder>(bin)!.Items![0].Should().Be(value);
    }

    [Fact]
    public void CallSiteTypesRegisterAtModuleLoad()
    {
        // The registrations happen before this method runs; the calls below only make them call sites.
        MemoryPackFormatterProvider.IsRegistered<Dictionary<int, CallSiteOnly>>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<List<List<CallSiteOnly>>>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<List<CallSiteOnly>>().Should().BeTrue();
        MemoryPackFormatterProvider.IsRegistered<UnregisteredStruct[]>().Should().BeTrue();

        var map = new Dictionary<int, CallSiteOnly> { [1] = new() { Name = "a" } };
        MemoryPackSerializer.Deserialize<Dictionary<int, CallSiteOnly>>(MemoryPackSerializer.Serialize(map))![1].Name.Should().Be("a");

        var nested = new List<List<CallSiteOnly>> { new() { new() { Name = "b" } } };
        MemoryPackSerializer.Deserialize<List<List<CallSiteOnly>>>(MemoryPackSerializer.Serialize(nested))![0][0].Name.Should().Be("b");

        var structs = new[] { new UnregisteredStruct { A = 1, B = 2 } };
        MemoryPackSerializer.Deserialize<UnregisteredStruct[]>(MemoryPackSerializer.Serialize(structs)).Should().Equal(structs);
    }

    [Fact]
    public void CustomFormatterTakesAttributeArguments()
    {
        // The attribute caps decompression at 16 bytes, so a 100-byte payload must be rejected on read.
        var bin = MemoryPackSerializer.Serialize(new CappedBrotli { Payload = new byte[100] });
        var act = () => MemoryPackSerializer.Deserialize<CappedBrotli>(bin);
        act.Should().Throw<MemoryPackSerializationException>();

        var small = MemoryPackSerializer.Serialize(new CappedBrotli { Payload = new byte[8] });
        MemoryPackSerializer.Deserialize<CappedBrotli>(small)!.Payload.Should().HaveCount(8);
    }
}

[MemoryPackable]
public partial class RegistrationProbe
{
    public int Value { get; set; }
}

[MemoryPackable]
public partial class GenericRegistrationProbe<T>
{
    public T? Value { get; set; }
}

[MemoryPackable]
public partial class NonGenericRegistrationProbe
{
    public string? Text { get; set; }
}

public struct UnregisteredStruct
{
    public int A;
    public double B;
}

[MemoryPackable]
public partial class UnregisteredStructHolder
{
    public List<UnregisteredStruct>? Items { get; set; }
}

[MemoryPackable]
public partial class CallSiteOnly
{
    public string? Name { get; set; }
}

[MemoryPackable]
public partial class CappedBrotli
{
    [BrotliFormatter(CompressionLevel.Fastest, decompressionSizeLimit: 16)]
    public byte[]? Payload { get; set; }
}
