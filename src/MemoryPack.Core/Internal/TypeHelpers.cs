using System.Runtime.CompilerServices;

namespace MemoryPack.Internal;

internal static class TypeHelpers
{
    public static bool IsAnonymous(Type type)
    {
        return type.Namespace == null
               && type.IsSealed
               && (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                   || type.Name.StartsWith("<>__AnonType", StringComparison.Ordinal)
                   || type.Name.StartsWith("VB$AnonymousType_", StringComparison.Ordinal))
               && type.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }
}
