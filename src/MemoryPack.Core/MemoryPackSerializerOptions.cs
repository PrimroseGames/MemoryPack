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

        internal const int DefaultMaxDepth = 1000;
    }

    public enum StringEncoding : byte
    {
        Utf16,
        Utf8,
    }
}

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit
    {
    }
}

#endif
