using MemoryPack.Internal;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MemoryPack.Formatters;

[Preserve]
public sealed partial class TypeFormatter : MemoryPackFormatter<Type>
{
    // Remove Version, Culture, PublicKeyToken from AssemblyQualifiedName.
    // Result will be "TypeName, Assembly"
    // see:http://msdn.microsoft.com/en-us/library/w3f99sx1.aspx

#if NET7_0_OR_GREATER

    [GeneratedRegex(@", Version=\d+.\d+.\d+.\d+, Culture=[\w-]+, PublicKeyToken=(?:null|[a-f0-9]{16})")]
    private static partial Regex ShortTypeNameRegex();

#else

    static readonly Regex _shortTypeNameRegex = new Regex(@", Version=\d+.\d+.\d+.\d+, Culture=[\w-]+, PublicKeyToken=(?:null|[a-f0-9]{16})", RegexOptions.Compiled);
    static Regex ShortTypeNameRegex() => _shortTypeNameRegex;

#endif

    // A Type read back is whatever type, in whatever assembly, the data names.
#if NET5_0_OR_GREATER
    [RequiresUnreferencedCode("The data names the types to load, which trimming may have removed.")]
#endif
    public TypeFormatter()
    {
    }

    [Preserve]
    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Type? value)
    {
        var full = value?.AssemblyQualifiedName;
        if (full == null)
        {
            writer.WriteNullCollectionHeader();
            return;
        }

        var shortName = ShortTypeNameRegex().Replace(full, "");
        writer.WriteString(shortName);
    }

    [Preserve]
#if NET5_0_OR_GREATER
    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "Constructing a TypeFormatter requires unreferenced code.")]
#endif
    public override void Deserialize(ref MemoryPackReader reader, scoped ref Type? value)
    {
        var typeName = reader.ReadString();
        if (typeName == null)
        {
            value = null;
            return;
        }

        value = Type.GetType(typeName, throwOnError: true);
    }
}
