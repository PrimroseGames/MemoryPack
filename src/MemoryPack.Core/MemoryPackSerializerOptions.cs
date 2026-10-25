namespace MemoryPack
{
    public record MemoryPackSerializerOptions
    {
        // Default is Utf8
        public static readonly MemoryPackSerializerOptions Default = new MemoryPackSerializerOptions { StringEncoding = StringEncoding.Utf8 };

        public static readonly MemoryPackSerializerOptions Utf8 = Default with { StringEncoding = StringEncoding.Utf8 };
        public static readonly MemoryPackSerializerOptions Utf16 = Default with { StringEncoding = StringEncoding.Utf16 };

        public StringEncoding StringEncoding { get; init; }
        public IServiceProvider? ServiceProvider { get; init; }

        /// <summary>
        /// Deepest nesting of objects a read accepts; zero means unlimited, though running out of stack still throws.
        /// The default matches the writer's own limit, so anything MemoryPack wrote reads back.
        /// </summary>
        public int MaxDepth
        {
            get => maxDepth;
            init
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth), value, "MaxDepth can't be negative.");
                maxDepth = value;
            }
        }

        readonly int maxDepth = DefaultMaxDepth;

        /// <summary>
        /// Most bytes DeserializeAsync buffers from a stream before reading it, which bounds the memory an oversized or
        /// hostile stream can hold. A MemoryStream whose buffer is exposed is read in place and isn't limited. Defaults to 64 MiB.
        /// </summary>
        public long MaxBufferedStreamLength
        {
            get => maxBufferedStreamLength;
            init
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBufferedStreamLength), value, "MaxBufferedStreamLength must be positive.");
                maxBufferedStreamLength = value;
            }
        }

        readonly long maxBufferedStreamLength = 64 * 1024 * 1024;

        /// <summary>
        /// Whether hash collections a read builds hash integer-like keys (integers, enums, Guid, dates and the like)
        /// with a keyed hash, so data can't choose keys that all collide and make each insert linear. String keys
        /// are already safe, and a formatter given its own comparer keeps it. On by default.
        /// </summary>
        public bool HashFloodingResistant { get; init; } = true;

        internal const int DefaultMaxDepth = 1000;
    }

    public enum StringEncoding : byte
    {
        Utf16,
        Utf8,
    }
}
