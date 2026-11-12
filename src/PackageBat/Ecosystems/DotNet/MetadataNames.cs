using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>Generic parameter names, used only to make display strings readable.</summary>
internal sealed record GenericNames(IReadOnlyList<string> Type, IReadOnlyList<string> Method);

/// <summary>
/// Turns metadata signatures into strings. The "key" flavour is the join key
/// shared by the API extractor and the usage scanner, so both sides of the
/// join must use it: full names, no assembly, generic parameters as !0 / !!0,
/// custom modifiers ignored. The "display" flavour is for people.
/// </summary>
internal sealed class TypeNameProvider : ISignatureTypeProvider<string, GenericNames?>, ICustomAttributeTypeProvider<string>
{
    public static readonly TypeNameProvider Key = new(false);
    public static readonly TypeNameProvider Display = new(true);

    readonly bool _display;
    TypeNameProvider(bool display) => _display = display;

    public string GetPrimitiveType(PrimitiveTypeCode t) => _display ? Keyword(t) : "System." + t switch
    {
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        _ => t.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Format(MetadataNames.FullName(r, h));
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Format(MetadataNames.FullName(r, h));
    public string GetTypeFromSpecification(MetadataReader r, GenericNames? ctx, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, ctx);

    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetByReferenceType(string e) => _display ? "ref " + e : e + "&";
    public string GetPointerType(string e) => e + "*";
    public string GetPinnedType(string e) => e;
    public string GetModifiedType(string modifier, string unmodified, bool isRequired) => unmodified;

    public string GetGenericInstantiation(string g, ImmutableArray<string> args)
    {
        if (_display && g == "Nullable" && args.Length == 1) return args[0] + "?";
        return g + "<" + string.Join(_display ? ", " : ",", args) + ">";
    }

    public string GetGenericTypeParameter(GenericNames? ctx, int i) =>
        _display && ctx != null && i < ctx.Type.Count ? ctx.Type[i] : "!" + i;
    public string GetGenericMethodParameter(GenericNames? ctx, int i) =>
        _display && ctx != null && i < ctx.Method.Count ? ctx.Method[i] : "!!" + i;

    public string GetFunctionPointerType(MethodSignature<string> s) =>
        "delegate*<" + string.Join(",", s.ParameterTypes.Append(s.ReturnType)) + ">";

    // ICustomAttributeTypeProvider (only needed to read [Obsolete] arguments)
    public string GetSystemType() => "System.Type";
    public bool IsSystemType(string type) => type is "System.Type" or "Type";
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

    string Format(string fullName) => _display ? MetadataNames.ShortName(fullName) : fullName;

    static string Keyword(PrimitiveTypeCode t) => t switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        _ => t.ToString(),
    };
}

internal static class MetadataNames
{
    public static string FullName(MetadataReader r, TypeDefinitionHandle h)
    {
        var td = r.GetTypeDefinition(h);
        var name = r.GetString(td.Name);
        var declaring = td.GetDeclaringType();
        if (!declaring.IsNil) return FullName(r, declaring) + "+" + name;
        var ns = r.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string FullName(MetadataReader r, TypeReferenceHandle h)
    {
        var tr = r.GetTypeReference(h);
        var name = r.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            return FullName(r, (TypeReferenceHandle)tr.ResolutionScope) + "+" + name;
        var ns = r.GetString(tr.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>The assembly a type reference points into; null when it points into the referencing module itself.</summary>
    public static string? TargetAssembly(MetadataReader r, TypeReferenceHandle h)
    {
        while (true)
        {
            var scope = r.GetTypeReference(h).ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.TypeReference: h = (TypeReferenceHandle)scope; continue;
                case HandleKind.AssemblyReference: return r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                default: return null;
            }
        }
    }

    /// <summary>
    /// For a TypeDef/TypeRef returns it; for a generic instantiation TypeSpec
    /// (List&lt;int&gt;) returns the generic definition (List`1). Arrays, pointers etc. give nil.
    /// </summary>
    public static EntityHandle UnderlyingType(MetadataReader r, EntityHandle h)
    {
        if (h.Kind != HandleKind.TypeSpecification) return h;
        var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return default;
        blob.ReadSignatureTypeCode(); // CLASS or VALUETYPE
        return UnderlyingType(r, blob.ReadTypeHandle());
    }

    public static string? TypeName(MetadataReader r, EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => FullName(r, (TypeDefinitionHandle)h),
        HandleKind.TypeReference => FullName(r, (TypeReferenceHandle)h),
        HandleKind.TypeSpecification => UnderlyingType(r, h) is { IsNil: false } u ? TypeName(r, u) : null,
        _ => null,
    };

    public static string MethodKey(string name, MethodSignature<string> sig) =>
        $"{name}{(sig.GenericParameterCount > 0 ? "``" + sig.GenericParameterCount : "")}({string.Join(",", sig.ParameterTypes)}):{sig.ReturnType}{(sig.Header.IsInstance ? "" : " static")}";

    public static string FieldKey(string name, string type) => $"{name}|{type}";

    public static string? AttributeTypeName(MetadataReader r, CustomAttribute ca) => ca.Constructor.Kind switch
    {
        HandleKind.MemberReference => TypeName(r, r.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
        HandleKind.MethodDefinition => FullName(r, r.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
        _ => null,
    };

    /// <summary>"Newtonsoft.Json.Linq.JToken" -> "JToken", "A.Outer+Inner`1" -> "Outer.Inner", "List`1" -> "List".</summary>
    public static string ShortName(string fullName)
    {
        var sb = new StringBuilder();
        var nestedParts = fullName.Split('+');
        for (int i = 0; i < nestedParts.Length; i++)
        {
            var part = nestedParts[i];
            if (i == 0) { var dot = part.LastIndexOf('.'); if (dot >= 0) part = part[(dot + 1)..]; }
            var tick = part.IndexOf('`');
            if (tick >= 0) part = part[..tick];
            if (i > 0) sb.Append('.');
            sb.Append(part);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> GenericParameterNames(MetadataReader r, GenericParameterHandleCollection c) =>
        c.Select(h => r.GetString(r.GetGenericParameter(h).Name)).ToList();
}
