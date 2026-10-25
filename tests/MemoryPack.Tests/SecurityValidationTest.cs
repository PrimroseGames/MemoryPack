using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MemoryPack.Tests;

// MemoryPackGenerator_ValidateEnumsAndBools is on for this project. Raw-copied enums must hold a
// declared value or zero, bools read back as 0 or 1, and structs check their fields, on every path
// that copies bytes without a formatter looking at them.
public class SecurityValidationTest
{
    static byte[] Patch(byte[] bytes, int offset, byte value)
    {
        var copy = (byte[])bytes.Clone();
        copy[offset] = value;
        return copy;
    }

    static byte Raw(bool value) => Unsafe.As<bool, byte>(ref value);

    static void Rejects<T>(byte[] payload)
    {
        var act = () => MemoryPackSerializer.Deserialize<T>(payload);
        act.Should().Throw<MemoryPackSerializationException>();
    }

    [Fact]
    public void UndefinedEnumMemberIsRejected()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedEnumHolder { Value = ValidatedLevel.High });
        bytes.Length.Should().Be(5); // object header, then the int

        Rejects<ValidatedEnumHolder>(Patch(bytes, 1, 7));
        MemoryPackSerializer.Deserialize<ValidatedEnumHolder>(Patch(bytes, 1, 1))!.Value.Should().Be(ValidatedLevel.Low);
    }

    [Fact]
    public void ZeroIsAcceptedWhereNoMemberDeclaresIt()
    {
        // A field nobody set holds zero, and writes it.
        var bytes = MemoryPackSerializer.Serialize(new ValidatedEnumHolder());
        MemoryPackSerializer.Deserialize<ValidatedEnumHolder>(bytes)!.Value.Should().Be((ValidatedLevel)0);
    }

    [Fact]
    public void FlagsEnumTakesAnyBits()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedFlagsHolder { Value = ValidatedPerms.Read });
        MemoryPackSerializer.Deserialize<ValidatedFlagsHolder>(Patch(bytes, 1, 0xFF))!.Value.Should().Be((ValidatedPerms)0xFF);
    }

    [Fact]
    public void BoolMemberIsNormalized()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedBoolHolder { Value = true });
        bytes.Length.Should().Be(2);

        var read = MemoryPackSerializer.Deserialize<ValidatedBoolHolder>(Patch(bytes, 1, 2))!;
        Raw(read.Value).Should().Be(1);
        (read.Value == true).Should().BeTrue();
    }

    [Fact]
    public void NullableEnumMemberIsChecked()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedNullableHolder { Value = ValidatedLevel.High });
        var valueOffset = bytes.Length - 4; // the int ends the Nullable<int-backed enum>

        Rejects<ValidatedNullableHolder>(Patch(bytes, valueOffset, 9));
        MemoryPackSerializer.Deserialize<ValidatedNullableHolder>(bytes)!.Value.Should().Be(ValidatedLevel.High);

        // Without a value the payload bytes aren't looked at.
        var empty = MemoryPackSerializer.Serialize(new ValidatedNullableHolder());
        MemoryPackSerializer.Deserialize<ValidatedNullableHolder>(Patch(empty, valueOffset, 9))!.Value.Should().BeNull();
    }

    [Fact]
    public void PlainStructMemberChecksItsFields()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedPairHolder { Value = new ValidatedPair { Level = ValidatedLevel.High, On = true } });

        Rejects<ValidatedPairHolder>(Patch(bytes, 1, 5));

        var read = MemoryPackSerializer.Deserialize<ValidatedPairHolder>(Patch(bytes, 5, 3))!;
        Raw(read.Value.On).Should().Be(1);
    }

    [Fact]
    public void PackableStructChecksPrivateFieldsAndRejectsReadOnlyBools()
    {
        var cell = new ValidatedCell(ValidatedLevel.High, hidden: true) { Locked = true };
        var bytes = MemoryPackSerializer.Serialize(cell);
        bytes.Length.Should().Be(Unsafe.SizeOf<ValidatedCell>()); // copied whole

        Rejects<ValidatedCell>(Patch(bytes, 0, 9));
        Rejects<ValidatedCell>(Patch(bytes, 5, 2)); // Locked has no setter to normalize it through

        var read = MemoryPackSerializer.Deserialize<ValidatedCell>(Patch(bytes, 4, 2));
        Raw(read.Hidden).Should().Be(1);
        read.Should().Be(cell);
    }

    [Fact]
    public void PackableStructInsideAnObjectIsChecked()
    {
        var bytes = MemoryPackSerializer.Serialize(new ValidatedCellHolder { Cell = new ValidatedCell(ValidatedLevel.Low, hidden: false) });

        Rejects<ValidatedCellHolder>(Patch(bytes, 1, 9));
        Raw(MemoryPackSerializer.Deserialize<ValidatedCellHolder>(Patch(bytes, 5, 4))!.Cell.Hidden).Should().Be(1);
    }

    [Fact]
    public void TopLevelValuesAreChecked()
    {
        Rejects<ValidatedLevel>(BitConverter.GetBytes(3));
        MemoryPackSerializer.Deserialize<ValidatedLevel>(BitConverter.GetBytes(2)).Should().Be(ValidatedLevel.High);

        var nullable = MemoryPackSerializer.Serialize<ValidatedLevel?>(ValidatedLevel.Low);
        Rejects<ValidatedLevel?>(Patch(nullable, nullable.Length - 4, 8));
    }

    [Fact]
    public void ArraysAndCollectionsAreChecked()
    {
        var array = MemoryPackSerializer.Serialize(new[] { ValidatedLevel.Low, ValidatedLevel.High });
        Rejects<ValidatedLevel[]>(Patch(array, 8, 6)); // header, then the second element

        var list = MemoryPackSerializer.Serialize(new List<ValidatedLevel> { ValidatedLevel.Low, ValidatedLevel.High });
        Rejects<List<ValidatedLevel>>(Patch(list, 8, 6));

        var map = MemoryPackSerializer.Serialize(new Dictionary<int, ValidatedLevel> { [10] = ValidatedLevel.High });
        Rejects<Dictionary<int, ValidatedLevel>>(Patch(map, 8, 6)); // header, key, then the value

        var cells = MemoryPackSerializer.Serialize(new[] { new ValidatedCell(ValidatedLevel.Low, hidden: false) });
        Rejects<ValidatedCell[]>(Patch(cells, 4, 9));

        var bools = MemoryPackSerializer.Serialize(new[] { true, false, true });
        var read = MemoryPackSerializer.Deserialize<bool[]>(Patch(bools, 4, 0x80))!;
        Raw(read[0]).Should().Be(1);
        read.Should().Equal(true, false, true);
    }

    [Fact]
    public void LegitimateValuesReadBackUnchanged()
    {
        var value = new ValidatedEverything
        {
            Level = ValidatedLevel.High,
            Perms = ValidatedPerms.Read | ValidatedPerms.Write,
            On = true,
            MaybeLevel = ValidatedLevel.Low,
            MaybeOn = false,
            Levels = new[] { ValidatedLevel.Low, (ValidatedLevel)0 },
            LevelList = new List<ValidatedLevel> { ValidatedLevel.High },
            Pair = new ValidatedPair { Level = ValidatedLevel.Low, On = true },
            Cell = new ValidatedCell(ValidatedLevel.High, hidden: true) { Locked = true },
        };

        var bytes = MemoryPackSerializer.Serialize(value);
        var read = MemoryPackSerializer.Deserialize<ValidatedEverything>(bytes)!;
        MemoryPackSerializer.Serialize(read).Should().Equal(bytes);
        read.Cell.Should().Be(value.Cell);
    }
}

public enum ValidatedLevel
{
    Low = 1,
    High = 2,
}

[Flags]
public enum ValidatedPerms : byte
{
    Read = 1,
    Write = 2,
}

public struct ValidatedPair
{
    public ValidatedLevel Level;
    public bool On;
}

[MemoryPackable]
public partial struct ValidatedCell
{
    public ValidatedLevel Level;
    bool hidden;

    public ValidatedCell(ValidatedLevel level, bool hidden)
    {
        Level = level;
        this.hidden = hidden;
    }

    public bool Hidden => hidden;
    public bool Locked { get; init; }
}

[MemoryPackable]
public partial class ValidatedEnumHolder
{
    public ValidatedLevel Value;
}

[MemoryPackable]
public partial class ValidatedFlagsHolder
{
    public ValidatedPerms Value;
}

[MemoryPackable]
public partial class ValidatedBoolHolder
{
    public bool Value;
}

[MemoryPackable]
public partial class ValidatedNullableHolder
{
    public ValidatedLevel? Value;
}

[MemoryPackable]
public partial class ValidatedPairHolder
{
    public ValidatedPair Value;
}

[MemoryPackable]
public partial class ValidatedCellHolder
{
    public ValidatedCell Cell;
}

[MemoryPackable]
public partial class ValidatedEverything
{
    public ValidatedLevel Level;
    public ValidatedPerms Perms;
    public bool On;
    public ValidatedLevel? MaybeLevel;
    public bool? MaybeOn;
    public ValidatedLevel[]? Levels;
    public List<ValidatedLevel>? LevelList;
    public ValidatedPair Pair;
    public ValidatedCell Cell;
}
