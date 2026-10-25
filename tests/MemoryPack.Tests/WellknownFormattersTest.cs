using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MemoryPack.Tests;

public class WellknownFormattersTest
{
    private T Convert<T>(T value)
    {
        return MemoryPackSerializer.Deserialize<T>(MemoryPackSerializer.Serialize(value))!;
    }

    private void ConvertEqual<T>(T value)
    {
        Convert(value).Should().Be(value);
    }

    [Fact]
    public void Geenrics()
    {
        ConvertEqual(new KeyValuePair<int, string>(100, "hoge"));
        Convert(new Lazy<int>(100)).Value.Should().Be(100);
    }

    [Fact]
    public void Nullable()
    {
        Convert(new Nullable<Sonota>(new Sonota { MyProperty = "9" })!)!.Value.MyProperty.Should().Be("9");
        Convert(default(Sonota?)).HasValue.Should().BeFalse();
    }


    [Fact]
    public void Others()
    {
        ConvertEqual(new Version(1, 3, 4, 5));
        ConvertEqual(new Uri("http://hoehoge.com/huu?q=takotako"));
        var sb = new StringBuilder(new string('a', 99999));
        Convert(sb).ToString().Should().Be(sb.ToString());
        var bitArray = new BitArray(Enumerable.Range(1, 1000).Select(x => x % 3 == 0).ToArray());
        Convert(bitArray).OfType<bool>().ToArray().Should().Equal(bitArray.OfType<bool>().ToArray());
    }

    // One test, since registration is process-wide and can't be undone: nothing else registers these.
    [Fact]
    public void PayloadNamedTypesAreOptIn()
    {
        MemoryPackFormatterProvider.IsRegistered<Type>().Should().BeFalse();
        MemoryPackFormatterProvider.IsRegistered<CultureInfo>().Should().BeFalse();
        MemoryPackFormatterProvider.IsRegistered<TimeZoneInfo>().Should().BeFalse();

        // A type name in the data no longer loads anything.
        var name = MemoryPackSerializer.Serialize("System.Text.StringBuilder, System.Runtime");
        var act = () => MemoryPackSerializer.Deserialize<Type>(name);
        act.Should().Throw<MemoryPackSerializationException>();

        MemoryPackFormatterProvider.RegisterPayloadNamedTypeFormatters();

        ConvertEqual(TimeZoneInfo.Utc);
        ConvertEqual(typeof(WellknownFormattersTest));
        MemoryPackSerializer.Deserialize<Type>(name).Should().Be(typeof(StringBuilder));
        ConvertEqual(CultureInfo.InvariantCulture);
        ConvertEqual(CultureInfo.GetCultureInfo("ja"));
        ConvertEqual(CultureInfo.GetCultureInfo("ja-JP"));
        ConvertEqual(CultureInfo.GetCultureInfo("en"));
    }
}

[MemoryPackable]
public partial struct Sonota
{
    public string MyProperty { get; set; }
}
