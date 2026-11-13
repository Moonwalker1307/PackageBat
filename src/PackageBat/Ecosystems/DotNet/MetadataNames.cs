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
    [ThreadStatic] static int _specDepth;

    public string GetTypeFromSpecification(MetadataReader r, GenericNames? ctx, TypeSpecificationHandle h, byte k)
    {
        // A type spec can refer to itself in a malformed file; don't recurse forever.
        if (_specDepth >= MetadataNames.MaxDepth) throw MetadataNames.TooDeep("type specifications");
        _specDepth++;
        try { return r.GetTypeSpecification(h).DecodeSignature(this, ctx); }
        finally { _specDepth--; }
    }

    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
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
    /// <summary>
    /// How deep nesting, generic instantiations and type specs may go. Real code
    /// stays far below this; a malformed or hostile file can make these chains
    /// cyclic, which would otherwise recurse until the process dies.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// True when <paramref name="e"/> means the file is malformed, as opposed to a
    /// bug in PackageBat. System.Reflection.Metadata reports most bad input as
    /// BadImageFormatException, but some as OverflowException,
    /// InvalidOperationException, InvalidCastException or ArgumentException; those count only when
    /// System.Reflection.Metadata threw them, not when our own code did.
    /// </summary>
    internal static bool IsMalformed(Exception e)
    {
        if (e is BadImageFormatException) return true;
        if (e is not (OverflowException or InvalidOperationException or InvalidCastException or ArgumentException or IndexOutOfRangeException)) return false;
        foreach (var f in new System.Diagnostics.StackTrace(e, false).GetFrames())
        {
            var assembly = f.GetMethod()?.DeclaringType?.Assembly;
            if (assembly == typeof(MetadataReader).Assembly) return true;
            if (assembly == typeof(MetadataNames).Assembly) return false;
        }
        return false;
    }

    internal static BadImageFormatException TooDeep(string what) =>
        new($"Malformed metadata: {what} nest deeper than {MaxDepth} levels (or form a cycle).");

    public static string FullName(MetadataReader r, TypeDefinitionHandle h)
    {
        var parts = new List<string>();
        for (int depth = 0; ; depth++)
        {
            if (depth > MaxDepth) throw TooDeep("nested types");
            var td = r.GetTypeDefinition(h);
            var declaring = td.GetDeclaringType();
            if (declaring.IsNil)
            {
                var ns = r.GetString(td.Namespace);
                parts.Add(ns.Length == 0 ? r.GetString(td.Name) : ns + "." + r.GetString(td.Name));
                break;
            }
            parts.Add(r.GetString(td.Name));
            h = declaring;
        }
        parts.Reverse();
        return string.Join("+", parts);
    }

    public static string FullName(MetadataReader r, TypeReferenceHandle h)
    {
        var parts = new List<string>();
        for (int depth = 0; ; depth++)
        {
            if (depth > MaxDepth) throw TooDeep("nested type references");
            var tr = r.GetTypeReference(h);
            if (tr.ResolutionScope.Kind != HandleKind.TypeReference)
            {
                var ns = r.GetString(tr.Namespace);
                parts.Add(ns.Length == 0 ? r.GetString(tr.Name) : ns + "." + r.GetString(tr.Name));
                break;
            }
            parts.Add(r.GetString(tr.Name));
            h = (TypeReferenceHandle)tr.ResolutionScope;
        }
        parts.Reverse();
        return string.Join("+", parts);
    }

    /// <summary>The assembly a type reference points into; null when it points into the referencing module itself.</summary>
    public static string? TargetAssembly(MetadataReader r, TypeReferenceHandle h)
    {
        for (int depth = 0; ; depth++)
        {
            if (depth > MaxDepth) throw TooDeep("nested type references");
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
        for (int depth = 0; h.Kind == HandleKind.TypeSpecification; depth++)
        {
            if (depth > MaxDepth) throw TooDeep("type specifications");
            var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return default;
            blob.ReadSignatureTypeCode(); // CLASS or VALUETYPE
            h = blob.ReadTypeHandle();
        }
        return h;
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

    /// <summary>
    /// Reads the fixed (constructor) arguments of an attribute whose constructor takes
    /// only strings and bools, like [Obsolete] and [TargetFramework]. Null for any other
    /// constructor or a malformed blob.
    /// </summary>
    /// <remarks>
    /// Deliberately not CustomAttribute.DecodeValue: it trusts array lengths in the blob,
    /// so a corrupt or hostile assembly can make it allocate gigabytes (found by fuzzing).
    /// Named arguments are skipped, since nothing here needs them.
    /// </remarks>
    public static object?[]? ReadStringAndBoolArguments(MetadataReader r, CustomAttribute ca)
    {
        try
        {
            var signature = ca.Constructor.Kind switch
            {
                HandleKind.MethodDefinition => r.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).Signature,
                HandleKind.MemberReference => r.GetMemberReference((MemberReferenceHandle)ca.Constructor).Signature,
                _ => default,
            };
            if (signature.IsNil) return null;
            var sig = r.GetBlobReader(signature);
            sig.ReadSignatureHeader();
            var count = sig.ReadCompressedInteger();
            if (count > 16) return null;
            sig.ReadSignatureTypeCode(); // return type (void)
            var types = new SignatureTypeCode[count];
            for (int i = 0; i < count; i++)
            {
                types[i] = sig.ReadSignatureTypeCode();
                if (types[i] is not (SignatureTypeCode.String or SignatureTypeCode.Boolean)) return null;
            }

            var value = r.GetBlobReader(ca.Value);
            if (value.ReadUInt16() != 1) return null; // prolog
            var args = new object?[count];
            for (int i = 0; i < count; i++)
                args[i] = types[i] == SignatureTypeCode.String ? value.ReadSerializedString() : value.ReadBoolean();
            return args;
        }
        catch (Exception e) when (IsMalformed(e))
        {
            return null;
        }
    }

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
