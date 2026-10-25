using MemoryPack.Formatters;
using MemoryPack.Internal;
using System.Collections.Frozen;

// Frozen Collections formatters

namespace MemoryPack
{
    public static partial class MemoryPackFormatterProvider
    {
        static readonly Dictionary<Type, Type> FrozenCollectionFormatters = new Dictionary<Type, Type>()
        {
            { typeof(FrozenDictionary<,>), typeof(FrozenDictionaryFormatter<,>) },
            { typeof(FrozenSet<>), typeof(FrozenSetFormatter<>) },
        };
    }
}

namespace MemoryPack.Formatters
{
    [Preserve]
    public sealed class FrozenDictionaryFormatter<TKey, TValue> : MemoryPackFormatter<FrozenDictionary<TKey, TValue?>>
        where TKey : notnull
    {
        readonly IEqualityComparer<TKey>? equalityComparer;

        public FrozenDictionaryFormatter() : this(null)
        {

        }

        public FrozenDictionaryFormatter(IEqualityComparer<TKey>? equalityComparer)
        {
            this.equalityComparer = equalityComparer;
        }

        [Preserve]
        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref FrozenDictionary<TKey, TValue?>? value)
        {
            if (value == null)
            {
                writer.WriteNullCollectionHeader();
                return;
            }

            var keyFormatter = writer.GetFormatter<TKey>();
            var valueFormatter = writer.GetFormatter<TValue>();

            var count = value.Count;
            writer.WriteCollectionHeader(count);
            var i = 0;
            foreach (var item in value)
            {
                i++;
                KeyValuePairFormatter.Serialize(keyFormatter, valueFormatter, ref writer, item!);
            }

            if (i != count) MemoryPackSerializationException.ThrowInvalidConcurrrentCollectionOperation();
        }

        [Preserve]
        public override void Deserialize(ref MemoryPackReader reader, scoped ref FrozenDictionary<TKey, TValue?>? value)
        {
            if (!reader.TryReadCollectionHeader(out var length))
            {
                value = null;
                return;
            }

            var comparer = HashFloodingResistantEqualityComparer.Resolve(equalityComparer, reader.Options);
            var dict = new Dictionary<TKey, TValue?>(length, comparer);

            var keyFormatter = reader.GetFormatter<TKey>();
            var valueFormatter = reader.GetFormatter<TValue>();
            for (var i = 0; i < length; i++)
            {
                KeyValuePairFormatter.Deserialize(keyFormatter, valueFormatter, ref reader, out var k, out var v);
                if (k is null || !dict.TryAdd(k, v)) MemoryPackSerializationException.ThrowInvalidKey(typeof(FrozenDictionary<TKey, TValue?>), k);
            }
            value = dict.ToFrozenDictionary(comparer);
        }
    }

    public sealed class FrozenSetFormatter<T> : MemoryPackFormatter<FrozenSet<T?>>
    {
        readonly IEqualityComparer<T?>? equalityComparer;

        public FrozenSetFormatter() : this(null)
        {
        }

        public FrozenSetFormatter(IEqualityComparer<T?>? equalityComparer)
        {
            this.equalityComparer = equalityComparer;
        }

        [Preserve]
        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref FrozenSet<T?>? value)
        {
            if (value == null)
            {
                writer.WriteNullCollectionHeader();
                return;
            }

            var formatter = writer.GetFormatter<T?>();
            writer.WriteCollectionHeader(value.Count);
            foreach (var item in value)
            {
                var v = item;
                formatter.Serialize(ref writer, ref v);
            }
        }

        [Preserve]
        public override void Deserialize(ref MemoryPackReader reader, scoped ref FrozenSet<T?>? value)
        {
            if (!reader.TryReadCollectionHeader(out var length))
            {
                value = null;
                return;
            }

            var comparer = HashFloodingResistantEqualityComparer.Resolve(equalityComparer, reader.Options);
            var set = new HashSet<T?>(length, comparer);

            var formatter = reader.GetFormatter<T?>();
            for (int i = 0; i < length; i++)
            {
                T? v = default;
                formatter.Deserialize(ref reader, ref v);
                set.Add(v);
            }

            value = set.ToFrozenSet(comparer);
        }
    }
}
