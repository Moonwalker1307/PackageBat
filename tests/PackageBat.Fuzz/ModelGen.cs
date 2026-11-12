using CsCheck;
using PackageBat.Model;

namespace PackageBat.Fuzz;

/// <summary>
/// Generators for the language-neutral JSON model. Names come from small pools
/// so that old and new versions overlap a lot: types and members get removed,
/// moved to a base type, re-declared, deprecated, sealed, made abstract, and
/// hierarchies can contain cycles and types from outside the package, as
/// hand-written or other-language extractor output might.
/// </summary>
internal static class ModelGen
{
    static readonly string[] TypeNames =
    {
        "N.A", "N.B", "N.C", "N.A+Inner", "N.G`1", "M.A", "N.I", "N.J", "System.Object", "Other.External",
    };
    static readonly string[] ParamTypes = { "System.Int32", "System.String", "System.Int32[]", "System.String[]", "N.A", "!0" };
    static readonly string[] MemberNames = { "M", "N", ".ctor", "get_P", "set_P", "F", "add_E" };

    static readonly Gen<string> TypeName = Gen.OneOfConst(TypeNames);

    static readonly Gen<Deprecation?> Deprecation =
        Gen.Frequency((4, Gen.Const((Deprecation?)null)),
            (1, Gen.Select(Gen.OneOfConst<string?>(null, "", "Use N instead", "  "), Gen.Bool, (m, e) => (Deprecation?)new Deprecation { Message = m, IsError = e })));

    static readonly Gen<ApiParameter> Parameter =
        Gen.Select(Gen.OneOfConst(ParamTypes), Gen.OneOfConst<string?>(null, "x", "y"), Gen.Bool, Gen.Int[0, 5],
            (t, n, d, p) => new ApiParameter { Type = t, Name = n, HasDefault = d, IsParams = p == 0 && t.EndsWith("[]") });

    static Gen<ApiMember> Member(string declaringType) =>
        Gen.Select(Gen.OneOfConst(MemberNames), Parameter.List[0, 3], Gen.OneOfConst("System.Void", "System.Int32", "System.String"),
                Gen.Int[0, 7], Deprecation, Gen.Int[0, 4],
            (name, ps, ret, flags, dep, kind) =>
            {
                var memberKind = name == ".ctor" ? ApiMemberKind.Constructor
                    : name == "F" ? ApiMemberKind.Field
                    : (ApiMemberKind)(kind % 2 == 0 ? 0 : 2);
                if (memberKind == ApiMemberKind.Field) ps.Clear();
                var key = memberKind == ApiMemberKind.Field ? $"F|{ret}" : $"{name}({string.Join(",", ps.Select(p => p.Type))})";
                return new ApiMember
                {
                    Key = key,
                    Name = name,
                    DeclaringType = declaringType,
                    Display = $"{declaringType}.{key}",
                    Kind = memberKind,
                    IsStatic = (flags & 1) != 0,
                    IsAbstract = (flags & 2) != 0 && memberKind is ApiMemberKind.Method or ApiMemberKind.Property,
                    IsVirtual = (flags & 4) != 0,
                    ReturnType = ret,
                    Parameters = ps,
                    Deprecated = dep,
                };
            });

    static readonly Gen<ApiType> Type =
        TypeName.SelectMany(name =>
            Gen.Select(Gen.Enum<ApiTypeKind>(), Gen.Int[0, 7], Gen.OneOfConst<string?>(null, "N.A", "N.B", "N.C", "System.Object", "Other.External"),
                    TypeName.List[0, 2], Deprecation, Member(name).List[0, 5],
                (kind, flags, baseType, ifaces, dep, members) => new ApiType
                {
                    Name = name,
                    Display = name.Split('.', '+').Last(),
                    Module = "Pkg",
                    Kind = kind,
                    IsAbstract = (flags & 1) != 0,
                    IsSealed = (flags & 2) != 0,
                    IsInheritable = (flags & 4) != 0,
                    BaseType = baseType == name ? null : baseType,
                    Interfaces = ifaces,
                    Deprecated = dep,
                    Members = members,
                }));

    /// <summary>A surface, optionally with duplicate type names (several assemblies in one package can define the same type).</summary>
    static Gen<ApiSurface> Surface(bool allowDuplicates) =>
        Gen.Select(Type.List[0, 7], TypeName.List[0, 2], Gen.OneOfConst("1.0.0", "2.0.0-beta", ""), (types, fwd, v) =>
        {
            if (!allowDuplicates)
                types = types.GroupBy(t => t.Name).Select(g => g.First()).ToList();
            foreach (var t in types) // members unique by key within a type, as the model requires
                t.Members = t.Members.GroupBy(m => m.Key).Select(g => g.First()).ToList();
            return new ApiSurface { Ecosystem = "test", Package = "Pkg", Version = v, Modules = { "Pkg" }, Types = types, ForwardedTypes = fwd };
        });

    /// <summary>Surfaces that follow every identity rule in ApiSurface.cs.</summary>
    public static readonly Gen<ApiSurface> WellFormed = Surface(allowDuplicates: false);
    /// <summary>Surfaces that may break the identity rules (duplicate type names).</summary>
    public static readonly Gen<ApiSurface> Loose = Surface(allowDuplicates: true);

    /// <summary>A new version derived from an old one by a handful of edits, so the diff has real overlap.</summary>
    public static Gen<(ApiSurface Old, ApiSurface New)> Bump(Gen<ApiSurface> surfaces) =>
        Gen.Select(surfaces, surfaces, Gen.Int[0, 3], (a, b, mode) => mode switch
        {
            0 => (a, b),                       // unrelated versions
            1 => (a, Clone(a)),               // no change
            _ => (a, Merge(Clone(a), b)),     // old plus/minus pieces of another surface
        });

    static ApiSurface Merge(ApiSurface a, ApiSurface b)
    {
        var byName = b.Types.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        var types = new List<ApiType>();
        foreach (var t in a.Types)
        {
            if (byName.Remove(t.Name, out var other))
            {
                // Same type in both: keep some members of each side.
                var members = t.Members.Where((m, i) => i % 2 == 0).Concat(other.Members)
                    .GroupBy(m => m.Key).Select(g => g.First()).ToList();
                foreach (var m in members) m.DeclaringType = t.Name;
                types.Add(new ApiType
                {
                    Name = t.Name, Display = t.Display, Module = t.Module, Kind = t.Kind, IsAbstract = other.IsAbstract,
                    IsSealed = other.IsSealed, IsInheritable = other.IsInheritable, BaseType = other.BaseType,
                    Interfaces = other.Interfaces, Deprecated = other.Deprecated, Members = members,
                });
            }
            else if (t.Name.Length % 2 == 0)
                types.Add(t); // keep
            // else: removed
        }
        types.AddRange(byName.Values);
        return new ApiSurface { Ecosystem = a.Ecosystem, Package = a.Package, Version = b.Version, Modules = a.Modules, Types = types, ForwardedTypes = b.ForwardedTypes };
    }

    static readonly Gen<SourceLocation> Location =
        Gen.Select(Gen.OneOfConst<string?>(null, "src/a.cs", "src/b.cs", "src/dir with space/ü.cs", "/_/x.cs", "a|b`c*d.cs"),
            Gen.OneOfConst<int?>(null, 0, 1, 42, int.MaxValue), Gen.OneOfConst("App", "Tests"), Gen.OneOfConst<string?>(null, "App.Main", "<>c.<M>b__0"),
            (f, l, m, s) => new SourceLocation { File = f, Line = l, Module = m, Symbol = s });

    public static Gen<UsageSet> Usages(ApiSurface api)
    {
        var keys = api.Types.SelectMany(t => t.Members.Select(m => m.Key)).Concat(new[] { "Missing()", ".ctor()", "M(System.Int32)" }).Distinct().ToArray();
        var memberKey = Gen.Frequency((1, Gen.Const((string?)null)), (4, Gen.OneOfConst(keys).Select(k => (string?)k)));
        var usage = Gen.Select(TypeName, memberKey, Gen.Enum<UsageKind>(), Location,
            (t, k, kind, loc) => new Usage { TypeName = t, MemberKey = k, Kind = kind, Location = loc });
        return usage.List[0, 25].Select(us => new UsageSet { Ecosystem = "test", Scanned = { "bin/App.dll" }, Usages = us });
    }

    public static ApiSurface Clone(ApiSurface s) =>
        System.Text.Json.JsonSerializer.Deserialize<ApiSurface>(System.Text.Json.JsonSerializer.Serialize(s, Program.Json), Program.Json)!;
}
