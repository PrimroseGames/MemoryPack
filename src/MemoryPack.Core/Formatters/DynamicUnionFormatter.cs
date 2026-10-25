namespace MemoryPack.Formatters;

public sealed class DynamicUnionFormatter<T> : MemoryPackFormatter<T>
    where T : class
{
    readonly Dictionary<Type, ushort> typeToTag;
    readonly Dictionary<ushort, Type> tagToType;

    public DynamicUnionFormatter(params (ushort Tag, Type Type)[] memoryPackUnions)
    {
        typeToTag = memoryPackUnions.ToDictionary(x => x.Type, x => x.Tag);
        tagToType = memoryPackUnions.ToDictionary(x => x.Tag, x => x.Type);
    }

    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref T? value)
    {
        if (value == null)
        {
            writer.WriteNullUnionHeader();
            return;
        }

        var type = value.GetType();
        if (typeToTag.TryGetValue(type, out var tag))
        {
            writer.WriteUnionHeader(tag);
            writer.WriteValue(type, value);
        }
        else
        {
            MemoryPackSerializationException.ThrowNotFoundInUnionType(type, typeof(T));
        }
    }

    public override void Deserialize(ref MemoryPackReader reader, scoped ref T? value)
    {
        if (!reader.TryReadUnionHeader(out var tag))
        {
            value = default;
            return;
        }
        
        if (tagToType.TryGetValue(tag, out var type))
        {
            // The existing value is read into only when it's what the tag names; casting another to it would throw.
            object? v = value != null && type.IsInstanceOfType(value) ? value : null;
            reader.ReadValue(type, ref v);
            if (v is not (null or T))
            {
                MemoryPackSerializationException.ThrowNotFoundInUnionType(v.GetType(), typeof(T));
            }
            value = (T?)v;
        }
        else
        {
            MemoryPackSerializationException.ThrowInvalidTag(tag, typeof(T));
        }
    }
}
