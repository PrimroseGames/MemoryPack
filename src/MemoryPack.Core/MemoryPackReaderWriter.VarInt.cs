using System.Numerics;
using System.Runtime.CompilerServices;

namespace MemoryPack;

// VarInt, first sbyte is value or typeCode

// 0~127 = unsigned byte value
// -1~-120 = signed byte value
// -121 = byte
// -122 = sbyte
// -123 = ushort
// -124 = short
// -125 = uint
// -126 = int
// -127 = ulong
// -128 = long 

internal static class VarIntCodes
{
    public const byte MaxSingleValue = 127;
    public const sbyte MinSingleValue = -120;

    public const sbyte Byte = -121;
    public const sbyte SByte = -122;
    public const sbyte UInt16 = -123;
    public const sbyte Int16 = -124;
    public const sbyte UInt32 = -125;
    public const sbyte Int32 = -126;
    public const sbyte UInt64 = -127;
    public const sbyte Int64 = -128;
}

public ref partial struct MemoryPackWriter<TBufferWriter>
{
    public void WriteVarInt(byte x)
    {
        if (x <= VarIntCodes.MaxSingleValue)
        {
            WriteUnmanaged((sbyte)x);
        }
        else
        {
            WriteUnmanaged(VarIntCodes.Byte, x);
        }
    }

    public void WriteVarInt(sbyte x)
    {
        if (VarIntCodes.MinSingleValue <= x)
        {
            WriteUnmanaged(x);
        }
        else
        {
            WriteUnmanaged(VarIntCodes.SByte, x);
        }
    }

    public void WriteVarInt(ushort x)
    {
        if (x <= VarIntCodes.MaxSingleValue)
        {
            WriteUnmanaged((sbyte)x);
        }
        else
        {
            WriteUnmanaged(VarIntCodes.UInt16, (UInt16)x);
        }
    }

    public void WriteVarInt(short x)
    {
        if (0 <= x)
        {
            if (x <= VarIntCodes.MaxSingleValue) // same as sbyte.MaxValue
            {
                WriteUnmanaged((sbyte)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
        }
        else
        {
            if (VarIntCodes.MinSingleValue <= x)
            {
                WriteUnmanaged((sbyte)x);
            }
            else if (sbyte.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.SByte, (SByte)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
        }
    }

    public void WriteVarInt(uint x)
    {
        if (x <= VarIntCodes.MaxSingleValue)
        {
            WriteUnmanaged((sbyte)x);
        }
        else if (x <= ushort.MaxValue)
        {
            WriteUnmanaged(VarIntCodes.UInt16, (UInt16)x);
        }
        else
        {
            WriteUnmanaged(VarIntCodes.UInt32, (UInt32)x);
        }
    }

    public void WriteVarInt(int x)
    {
        if (0 <= x)
        {
            if (x <= VarIntCodes.MaxSingleValue) // same as sbyte.MaxValue
            {
                WriteUnmanaged((sbyte)x);
            }
            else if (x <= short.MaxValue)
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int32, (Int32)x);
            }
        }
        else
        {
            if (VarIntCodes.MinSingleValue <= x)
            {
                WriteUnmanaged((sbyte)x);
            }
            else if (sbyte.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.SByte, (SByte)x);
            }
            else if (short.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int32, (Int32)x);
            }
        }
    }

    public void WriteVarInt(ulong x)
    {
        if (x <= VarIntCodes.MaxSingleValue)
        {
            WriteUnmanaged((sbyte)x);
        }
        else if (x <= ushort.MaxValue)
        {
            WriteUnmanaged(VarIntCodes.UInt16, (UInt16)x);
        }
        else if (x <= uint.MaxValue)
        {
            WriteUnmanaged(VarIntCodes.UInt32, (UInt32)x);
        }
        else
        {
            WriteUnmanaged(VarIntCodes.UInt64, (UInt64)x);
        }
    }

    public void WriteVarInt(long x)
    {
        if (0 <= x)
        {
            if (x <= VarIntCodes.MaxSingleValue) // same as sbyte.MaxValue
            {
                WriteUnmanaged((sbyte)x);
            }
            else if (x <= short.MaxValue)
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
            else if (x <= int.MaxValue)
            {
                WriteUnmanaged(VarIntCodes.Int32, (Int32)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int64, (Int64)x);
            }
        }
        else
        {
            if (VarIntCodes.MinSingleValue <= x)
            {
                WriteUnmanaged((sbyte)x);
            }
            else if (sbyte.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.SByte, (SByte)x);
            }
            else if (short.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.Int16, (Int16)x);
            }
            else if (int.MinValue <= x)
            {
                WriteUnmanaged(VarIntCodes.Int32, (Int32)x);
            }
            else
            {
                WriteUnmanaged(VarIntCodes.Int64, (Int64)x);
            }
        }
    }
}

public ref partial struct MemoryPackReader
{
    public byte ReadVarIntByte() => ReadVarInt<byte>();
    public sbyte ReadVarIntSByte() => ReadVarInt<sbyte>();
    public ushort ReadVarIntUInt16() => ReadVarInt<ushort>();
    public short ReadVarIntInt16() => ReadVarInt<short>();
    public uint ReadVarIntUInt32() => ReadVarInt<uint>();
    public int ReadVarIntInt32() => ReadVarInt<int>();
    public ulong ReadVarIntUInt64() => ReadVarInt<ulong>();
    public long ReadVarIntInt64() => ReadVarInt<long>();

    // Reads the full width the code names, then rejects a value the target type can't hold.
    T ReadVarInt<T>() where T : IBinaryInteger<T>
    {
        ReadUnmanaged(out sbyte typeCode);

        return typeCode switch
        {
            VarIntCodes.Byte => Narrow<byte, T>(ReadUnmanaged<byte>()),
            VarIntCodes.SByte => Narrow<sbyte, T>(ReadUnmanaged<sbyte>()),
            VarIntCodes.UInt16 => Narrow<ushort, T>(ReadUnmanaged<ushort>()),
            VarIntCodes.Int16 => Narrow<short, T>(ReadUnmanaged<short>()),
            VarIntCodes.UInt32 => Narrow<uint, T>(ReadUnmanaged<uint>()),
            VarIntCodes.Int32 => Narrow<int, T>(ReadUnmanaged<int>()),
            VarIntCodes.UInt64 => Narrow<ulong, T>(ReadUnmanaged<ulong>()),
            VarIntCodes.Int64 => Narrow<long, T>(ReadUnmanaged<long>()),
            _ => Narrow<sbyte, T>(typeCode),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static TTo Narrow<TFrom, TTo>(TFrom value)
        where TFrom : IBinaryInteger<TFrom>
        where TTo : IBinaryInteger<TTo>
    {
        var result = TTo.CreateTruncating(value);
        if (TFrom.CreateTruncating(result) != value || TFrom.IsNegative(value) != TTo.IsNegative(result))
        {
            MemoryPackSerializationException.ThrowVarIntOutOfRange(typeof(TTo), value);
        }
        return result;
    }
}
