using MemoryPack.Internal;

namespace MemoryPack.Formatters;

[Preserve]
public sealed class VersionFormatter : MemoryPackFormatter<Version>
{
    // Serialize as [Major, Minor, Build, Revision]

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Version? value)
    {
        if (value == null)
        {
            writer.WriteNullObjectHeader();
            return;
        }

        writer.WriteUnmanagedWithObjectHeader(4, value.Major, value.Minor, value.Build, value.Revision);
    }

    [Preserve]
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Version? value)
    {
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = null;
            return;
        }

        if (count != 4) MemoryPackSerializationException.ThrowInvalidPropertyCount(4, count);

        reader.ReadUnmanaged(out int major, out int minor, out int build, out int revision);

        // What a Version can hold: build and revision are -1 when unset, and revision only with build.
        if (major < 0 || minor < 0 || build < -1 || revision < -1 || (build == -1 && revision != -1))
        {
            MemoryPackSerializationException.ThrowMessage($"The data holds an invalid Version, {major}.{minor}.{build}.{revision}.");
        }

        // when use new Version(major, minor), build and revision will be -1, it can not use constructor.
        if (revision == -1)
        {
            if (build == -1)
            {
                value = new Version(major, minor);
            }
            else
            {
                value = new Version(major, minor, build);
            }
        }
        else
        {
            value = new Version(major, minor, build, revision);
        }
    }
}
