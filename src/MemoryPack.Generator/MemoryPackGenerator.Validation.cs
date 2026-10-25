using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace MemoryPack.Generator;

// MemoryPackGenerator_ValidateEnumsAndBools: checks on what the reader copies as raw bytes. An enum
// without [Flags] must hold a declared value or zero, a bool is normalized to 0 or 1, and an
// unmanaged struct checks its fields. Generated types register the checks with
// UnmanagedValidator<T>, which the reader runs after every raw copy, and run them on the members
// they read raw themselves. Legitimate data reads back unchanged.
public partial class TypeMeta
{
    public bool ValidateEnumsAndBools { get; set; }

    public const string OwnValidatorName = "__MemoryPackValidate";

    const string Validator = "global::MemoryPack.UnmanagedValidator";
    const string Validation = "global::MemoryPack.MemoryPackValidation";
    const string AsRef = "global::System.Runtime.CompilerServices.Unsafe.AsRef";

    ValidationPlan? validationPlan;

    ValidationPlan Plan => validationPlan ??= new ValidationPlan(this, reference);

    // Runs the checks on the members this type reads raw into locals; placed where every read path joins.
    string EmitValidateMembers(string indent)
    {
        if (!ValidateEnumsAndBools) return "";

        var sb = new StringBuilder();
        foreach (var item in Members)
        {
            if (item.Symbol == null) continue;
            if (item.Kind is not (MemberKind.Unmanaged or MemberKind.Enum or MemberKind.UnmanagedNullable)) continue;
            if (!Plan.HasValidator(item.MemberType)) continue;

            sb.AppendLine($"{indent}{Validator}<{item.MemberType.FullyQualifiedToString()}>.Run(ref __{item.Name});");
        }
        return sb.ToString();
    }

    // A raw-copied struct of this type checks itself through its own validator.
    string EmitValidateSelf(string indent)
    {
        if (!ValidateEnumsAndBools || !Plan.HasOwnValidator) return "";
        return $"{indent}{Validator}<{TypeName}>.Run(ref value);";
    }

    string EmitValidationRegistrations(string indent)
    {
        if (!ValidateEnumsAndBools) return "";
        return Plan.EmitRegistrations(indent);
    }

    string EmitValidatorMethods()
    {
        if (!ValidateEnumsAndBools) return "";
        return Plan.EmitMethods();
    }

    sealed class ValidationPlan
    {
        readonly TypeMeta type;
        readonly ReferenceSymbols reference;
        readonly Dictionary<ITypeSymbol, bool> hasValidator = new(SymbolEqualityComparer.Default);

        // Types whose checks this type registers, in a stable order, and what each needs.
        readonly List<ITypeSymbol> registered = new();
        readonly HashSet<ITypeSymbol> seen = new(SymbolEqualityComparer.Default);
        readonly List<ITypeSymbol> ownedElsewhere = new();
        bool registersBool;

        public bool HasOwnValidator { get; }

        public ValidationPlan(TypeMeta type, ReferenceSymbols reference)
        {
            this.type = type;
            this.reference = reference;

            HasOwnValidator = type.IsUnmanagedType && Checks(type.Symbol, type.Symbol).Count != 0;
            if (HasOwnValidator)
            {
                foreach (var field in InstanceFields(type.Symbol))
                {
                    Collect(field.Type);
                }
            }

            foreach (var item in type.Members)
            {
                if (item.Symbol == null) continue;
                Collect(item.MemberType);
            }
        }

        // Whether a validator for the type exists once this type's registrations have run.
        public bool HasValidator(ITypeSymbol t)
        {
            if (hasValidator.TryGetValue(t, out var result)) return result;
            hasValidator[t] = false; // structs can't contain themselves, but keep the walk finite
            result = ComputeHasValidator(t);
            hasValidator[t] = result;
            return result;
        }

        bool ComputeHasValidator(ITypeSymbol t)
        {
            if (t.SpecialType == SpecialType.System_Boolean) return true;
            if (t.TypeKind == TypeKind.TypeParameter) return true; // registered wherever the type argument's checks are
            if (t.TypeKind == TypeKind.Enum) return !IsFlags(t);
            if (t.SpecialType != SpecialType.None) return false;
            if (!t.IsValueType || !t.IsUnmanagedType || t is not INamedTypeSymbol named) return false;

            if (IsNullable(named)) return HasValidator(named.TypeArguments[0]);
            if (OwnsValidator(named)) return true;
            return Checks(named, type.Symbol).Count != 0;
        }

        // A [MemoryPackable] struct generated with these checks registers its own, covering private fields.
        bool OwnsValidator(INamedTypeSymbol t)
        {
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, type.Symbol.OriginalDefinition)) return HasOwnValidator;
            if (t.Locations.Any(x => x.IsInSource))
            {
                return t.TryGetMemoryPackableType(reference, out var generateType, out _)
                       && generateType != GenerateType.NoGenerate
                       && Checks(t, t).Count != 0;
            }
            return t.GetMembers(OwnValidatorName).Length != 0;
        }

        void Collect(ITypeSymbol t)
        {
            if (!seen.Add(t)) return;

            switch (t)
            {
                case IArrayTypeSymbol array:
                    Collect(array.ElementType);
                    return;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                    {
                        Collect(argument);
                    }
                    break;
                default:
                    return; // type parameters, pointers
            }

            if (t.SpecialType == SpecialType.System_Boolean)
            {
                registersBool = true;
                return;
            }
            if (!HasValidator(t)) return;

            var n = (INamedTypeSymbol)t;
            if (t.TypeKind == TypeKind.Enum)
            {
                registered.Add(t);
                return;
            }
            if (IsNullable(n))
            {
                registered.Add(t);
                return;
            }
            if (SymbolEqualityComparer.Default.Equals(n.OriginalDefinition, type.Symbol.OriginalDefinition)) return;
            if (OwnsValidator(n))
            {
                ownedElsewhere.Add(t);
                return;
            }

            registered.Add(t);
            foreach (var check in Checks(n, type.Symbol))
            {
                Collect(check.Type);
            }
        }

        public string EmitRegistrations(string indent)
        {
            var sb = new StringBuilder();
            if (HasOwnValidator)
            {
                sb.AppendLine($"{indent}{Validator}<{type.TypeName}>.Register({OwnValidatorName});");
            }
            if (registersBool)
            {
                sb.AppendLine($"{indent}{Validator}<bool>.Register({Validation}.NormalizeBools);");
            }
            foreach (var t in ownedElsewhere)
            {
                sb.AppendLine($"{indent}global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({t.FullyQualifiedToString()}).TypeHandle);");
            }
            for (int i = 0; i < registered.Count; i++)
            {
                var t = registered[i];
                var name = t.FullyQualifiedToString();
                if (t is INamedTypeSymbol n && IsNullable(n))
                {
                    sb.AppendLine($"{indent}{Validator}<{name}>.Register({Validation}.ValidateNullables<{n.TypeArguments[0].FullyQualifiedToString()}>);");
                }
                else
                {
                    sb.AppendLine($"{indent}{Validator}<{name}>.Register({OwnValidatorName}{i});");
                }
            }
            return sb.ToString();
        }

        public string EmitMethods()
        {
            var sb = new StringBuilder();
            if (HasOwnValidator)
            {
                sb.AppendLine($$"""
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    public static void {{OwnValidatorName}}(global::System.Span<{{type.TypeName}}> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ref var value = ref values[i];
{{EmitChecks(Checks(type.Symbol, type.Symbol), type.Symbol, "            ")}}
        }
    }
""");
            }

            for (int i = 0; i < registered.Count; i++)
            {
                var t = registered[i];
                if (t is INamedTypeSymbol n && IsNullable(n)) continue;

                var name = t.FullyQualifiedToString();
                if (t.TypeKind == TypeKind.Enum)
                {
                    sb.AppendLine($$"""
    static void {{OwnValidatorName}}{{i}}(global::System.Span<{{name}}> values)
    {
        foreach (var value in values)
        {
            if ({{EnumUndefined(t, "value")}})
            {
                global::MemoryPack.MemoryPackSerializationException.ThrowUndefinedEnumValue(typeof({{name}}), unchecked((long)value));
            }
        }
    }
""");
                }
                else
                {
                    sb.AppendLine($$"""
    static void {{OwnValidatorName}}{{i}}(global::System.Span<{{name}}> values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ref var value = ref values[i];
{{EmitChecks(Checks((INamedTypeSymbol)t, type.Symbol), t, "            ")}}
        }
    }
""");
                }
            }
            return sb.ToString();
        }

        string EmitChecks(List<Check> checks, ITypeSymbol owner, string indent)
        {
            var sb = new StringBuilder();
            foreach (var check in checks)
            {
                var typeName = check.Type.FullyQualifiedToString();
                var access = $"value.@{check.Name}";
                if (check.Property == null)
                {
                    sb.AppendLine($"{indent}{Validator}<{typeName}>.Run(ref {AsRef}(in {access}));");
                }
                else if (check.Property.SetMethod is { IsInitOnly: false } setter
                         && reference.Compilation.IsSymbolAccessibleWithin(setter, type.Symbol))
                {
                    sb.AppendLine($"{indent}{{ var __v = {access}; {Validator}<{typeName}>.Run(ref __v); {access} = __v; }}");
                }
                else if (check.Type.SpecialType == SpecialType.System_Boolean)
                {
                    var member = SymbolDisplay.FormatLiteral($"{owner.Name}.{check.Name}", quote: true);
                    sb.AppendLine($"{indent}{Validation}.RejectNonCanonicalBool({access}, {member});");
                }
                else
                {
                    // Read-only: enum values are still rejected; a bool inside can't be put back.
                    sb.AppendLine($"{indent}{{ var __v = {access}; {Validator}<{typeName}>.Run(ref __v); }}");
                }
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        static string EnumUndefined(ITypeSymbol enumType, string value)
        {
            var name = enumType.FullyQualifiedToString();
            var members = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(x => x.IsConst)
                .Select(x => $"{name}.@{x.Name}")
                .ToArray();

            // Zero is default(T), which any field holds before it's set, so it's always accepted.
            if (members.Length == 0) return $"{value} != 0";
            return $"{value} is not ({string.Join(" or ", members)}) && {value} != 0";
        }

        // The fields a raw copy of t holds that need checks, as reached from the type `from`. An
        // auto-property's backing field is reached through the property.
        List<Check> Checks(INamedTypeSymbol t, INamedTypeSymbol from)
        {
            var result = new List<Check>();
            foreach (var field in InstanceFields(t))
            {
                if (!HasValidator(field.Type)) continue;

                if (field.IsImplicitlyDeclared)
                {
                    if (field.AssociatedSymbol is not IPropertySymbol property) continue;
                    if (property.GetMethod == null || !reference.Compilation.IsSymbolAccessibleWithin(property.GetMethod, from)) continue;
                    result.Add(new Check(property.Name, field.Type, property));
                }
                else
                {
                    if (!reference.Compilation.IsSymbolAccessibleWithin(field, from)) continue;
                    result.Add(new Check(field.Name, field.Type, null));
                }
            }
            return result;
        }

        static IEnumerable<IFieldSymbol> InstanceFields(INamedTypeSymbol t)
        {
            return t.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(x => !x.IsStatic && !x.IsConst && !x.IsFixedSizeBuffer);
        }

        static bool IsFlags(ITypeSymbol enumType)
        {
            return enumType.GetAttributes().Any(x => x.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
        }

        bool IsNullable(INamedTypeSymbol t)
        {
            return t.EqualsUnconstructedGenericType(reference.KnownTypes.System_Nullable_T);
        }

        readonly record struct Check(string Name, ITypeSymbol Type, IPropertySymbol? Property);
    }
}
