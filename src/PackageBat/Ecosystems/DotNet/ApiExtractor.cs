using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>Reads the public API of .NET assemblies straight from metadata (nothing is loaded or executed).</summary>
internal static class ApiExtractor
{
    public static ApiSurface Extract(IEnumerable<string> assemblyPaths, string package, string version, string? framework)
    {
        var surface = new ApiSurface { Ecosystem = "dotnet", Package = package, Version = version, Framework = framework };
        foreach (var path in assemblyPaths)
        {
            try
            {
                ExtractOne(surface, path);
            }
            catch (Exception e) when (e is not BadImageFormatException && MetadataNames.IsMalformed(e))
            {
                throw new BadImageFormatException($"{Path.GetFileName(path)} has malformed metadata: {e.Message}", path, e);
            }
        }
        return surface;
    }

    static void ExtractOne(ApiSurface surface, string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return;
        var r = pe.GetMetadataReader();
        if (!r.IsAssembly) return;
        var module = r.GetString(r.GetAssemblyDefinition().Name);
        if (!surface.Modules.Contains(module)) surface.Modules.Add(module);

        foreach (var th in r.TypeDefinitions)
        {
            var td = r.GetTypeDefinition(th);
            if (IsVisible(r, td))
                surface.Types.Add(ReadType(r, th, td, module));
        }

        foreach (var eh in r.ExportedTypes)
        {
            var et = r.GetExportedType(eh);
            if (et.IsForwarder)
            {
                var ns = r.GetString(et.Namespace);
                var name = r.GetString(et.Name);
                surface.ForwardedTypes.Add(ns.Length == 0 ? name : ns + "." + name);
            }
        }
    }

    static ApiType ReadType(MetadataReader r, TypeDefinitionHandle th, TypeDefinition td, string module)
    {
        var name = MetadataNames.FullName(r, th);
        var attrs = td.Attributes;
        var baseName = td.BaseType.IsNil ? null : MetadataNames.TypeName(r, td.BaseType);
        var typeGenerics = MetadataNames.GenericParameterNames(r, td.GetGenericParameters());
        var shortName = MetadataNames.ShortName(name) + (typeGenerics.Count > 0 ? "<" + string.Join(", ", typeGenerics) + ">" : "");

        var kind = (attrs & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Interface ? ApiTypeKind.Interface
            : baseName == "System.Enum" ? ApiTypeKind.Enum
            : baseName == "System.ValueType" ? ApiTypeKind.Struct
            : baseName == "System.MulticastDelegate" ? ApiTypeKind.Delegate
            : ApiTypeKind.Class;

        var type = new ApiType
        {
            Name = name,
            Display = shortName,
            Module = module,
            Kind = kind,
            IsAbstract = (attrs & TypeAttributes.Abstract) != 0,
            IsSealed = (attrs & TypeAttributes.Sealed) != 0,
            BaseType = baseName,
            Deprecated = ReadObsolete(r, td.GetCustomAttributes()),
        };
        foreach (var ih in td.GetInterfaceImplementations())
            if (MetadataNames.TypeName(r, r.GetInterfaceImplementation(ih).Interface) is { } iname)
                type.Interfaces.Add(iname);

        bool hasVisibleCtor = false;
        foreach (var mh in td.GetMethods())
        {
            var md = r.GetMethodDefinition(mh);
            if (!IsVisible(md.Attributes & MethodAttributes.MemberAccessMask)) continue;
            var member = ReadMethod(r, md, type, typeGenerics);
            if (member.Kind == ApiMemberKind.Constructor && !member.IsStatic) hasVisibleCtor = true;
            type.Members.Add(member);
        }
        type.IsInheritable = kind == ApiTypeKind.Class && !type.IsSealed && hasVisibleCtor;

        foreach (var fh in td.GetFields())
        {
            var fd = r.GetFieldDefinition(fh);
            var fa = fd.Attributes;
            if ((fa & FieldAttributes.RTSpecialName) != 0) continue; // enum value__
            if (!IsVisible((MethodAttributes)(int)(fa & FieldAttributes.FieldAccessMask))) continue;
            var fname = r.GetString(fd.Name);
            var key = fd.DecodeSignature(TypeNameProvider.Key, null);
            var disp = fd.DecodeSignature(TypeNameProvider.Display, new GenericNames(typeGenerics, Array.Empty<string>()));
            type.Members.Add(new ApiMember
            {
                Key = MetadataNames.FieldKey(fname, key),
                Name = fname,
                DeclaringType = name,
                Kind = ApiMemberKind.Field,
                IsStatic = (fa & FieldAttributes.Static) != 0,
                ReturnType = key,
                Display = $"{type.Display}.{fname} ({(kind == ApiTypeKind.Enum ? "enum value" : "field: " + disp)})",
                Deprecated = ReadObsolete(r, fd.GetCustomAttributes()),
            });
        }

        // Properties/events carry [Obsolete] on themselves, not on their accessors.
        foreach (var ph in td.GetProperties())
        {
            var pd = r.GetPropertyDefinition(ph);
            if (ReadObsolete(r, pd.GetCustomAttributes()) is not { } dep) continue;
            var acc = pd.GetAccessors();
            foreach (var a in new[] { acc.Getter, acc.Setter }.Where(a => !a.IsNil))
                MarkAccessor(r, type, a, dep);
        }
        foreach (var eh in td.GetEvents())
        {
            var ed = r.GetEventDefinition(eh);
            if (ReadObsolete(r, ed.GetCustomAttributes()) is not { } dep) continue;
            var acc = ed.GetAccessors();
            foreach (var a in new[] { acc.Adder, acc.Remover }.Where(a => !a.IsNil))
                MarkAccessor(r, type, a, dep);
        }
        return type;
    }

    static void MarkAccessor(MetadataReader r, ApiType type, MethodDefinitionHandle accessor, Deprecation dep)
    {
        var md = r.GetMethodDefinition(accessor);
        var key = MetadataNames.MethodKey(r.GetString(md.Name), md.DecodeSignature(TypeNameProvider.Key, null));
        if (type.Members.FirstOrDefault(m => m.Key == key) is { } m)
            m.Deprecated ??= dep;
    }

    static ApiMember ReadMethod(MetadataReader r, MethodDefinition md, ApiType type, IReadOnlyList<string> typeGenerics)
    {
        var name = r.GetString(md.Name);
        var methodGenerics = MetadataNames.GenericParameterNames(r, md.GetGenericParameters());
        var keySig = md.DecodeSignature(TypeNameProvider.Key, null);
        var dispSig = md.DecodeSignature(TypeNameProvider.Display, new GenericNames(typeGenerics, methodGenerics));
        var attrs = md.Attributes;

        var paramInfo = new (string? Name, bool HasDefault, bool IsParams)[keySig.ParameterTypes.Length];
        foreach (var ph in md.GetParameters())
        {
            var p = r.GetParameter(ph);
            if (p.SequenceNumber < 1 || p.SequenceNumber > paramInfo.Length) continue;
            bool isParams = p.GetCustomAttributes().Any(a => MetadataNames.AttributeTypeName(r, r.GetCustomAttribute(a)) is "System.ParamArrayAttribute" or "System.Runtime.CompilerServices.ParamCollectionAttribute");
            paramInfo[p.SequenceNumber - 1] = (r.GetString(p.Name), (p.Attributes & (ParameterAttributes.HasDefault | ParameterAttributes.Optional)) != 0, isParams);
        }

        bool special = (attrs & MethodAttributes.SpecialName) != 0;
        var kind = name is ".ctor" or ".cctor" ? ApiMemberKind.Constructor
            : special && (name.StartsWith("get_") || name.StartsWith("set_")) ? ApiMemberKind.Property
            : special && (name.StartsWith("add_") || name.StartsWith("remove_")) ? ApiMemberKind.Event
            : ApiMemberKind.Method;

        var paramList = string.Join(", ", dispSig.ParameterTypes);
        var generic = methodGenerics.Count > 0 ? "<" + string.Join(", ", methodGenerics) + ">" : "";
        string display = kind switch
        {
            ApiMemberKind.Constructor => $"new {type.Display}({paramList})",
            ApiMemberKind.Property when name.StartsWith("get_") => $"{type.Display}.{name[4..]}{(paramList.Length > 0 ? $"[{paramList}]" : "")} (get) : {dispSig.ReturnType}",
            ApiMemberKind.Property => $"{type.Display}.{name[4..]}{(dispSig.ParameterTypes.Length > 1 ? $"[{string.Join(", ", dispSig.ParameterTypes.SkipLast(1))}]" : "")} (set)",
            ApiMemberKind.Event => $"{type.Display}.{name[(name.IndexOf('_') + 1)..]} (event {name[..name.IndexOf('_')]})",
            _ => $"{type.Display}.{name}{generic}({paramList})",
        };
        // Overloads that differ only in return type (or op_Implicit) are rare; show it when it's not void.
        if (kind == ApiMemberKind.Method && dispSig.ReturnType != "void") display += " : " + dispSig.ReturnType;

        return new ApiMember
        {
            Key = MetadataNames.MethodKey(name, keySig),
            Name = name,
            DeclaringType = type.Name,
            Display = display,
            Kind = kind,
            IsStatic = (attrs & MethodAttributes.Static) != 0,
            IsAbstract = (attrs & MethodAttributes.Abstract) != 0,
            IsVirtual = (attrs & MethodAttributes.Virtual) != 0 && (attrs & MethodAttributes.Final) == 0,
            ReturnType = keySig.ReturnType,
            Parameters = keySig.ParameterTypes.Select((t, i) => new ApiParameter { Type = t, Name = paramInfo[i].Name, HasDefault = paramInfo[i].HasDefault, IsParams = paramInfo[i].IsParams }).ToList(),
            Deprecated = ReadObsolete(r, md.GetCustomAttributes()),
        };
    }

    static bool IsVisible(MetadataReader r, TypeDefinition td)
    {
        for (int depth = 0; ; depth++)
        {
            if (depth > MetadataNames.MaxDepth) throw MetadataNames.TooDeep("nested types");
            switch (td.Attributes & TypeAttributes.VisibilityMask)
            {
                case TypeAttributes.Public: return true;
                case TypeAttributes.NestedPublic:
                case TypeAttributes.NestedFamily:
                case TypeAttributes.NestedFamORAssem:
                    td = r.GetTypeDefinition(td.GetDeclaringType());
                    continue;
                default: return false;
            }
        }
    }

    static bool IsVisible(MethodAttributes access) =>
        access is MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    static Deprecation? ReadObsolete(MetadataReader r, CustomAttributeHandleCollection attrs)
    {
        foreach (var h in attrs)
        {
            var ca = r.GetCustomAttribute(h);
            if (MetadataNames.AttributeTypeName(r, ca) != "System.ObsoleteAttribute") continue;
            var dep = new Deprecation();
            try
            {
                var v = ca.DecodeValue(TypeNameProvider.Key);
                if (v.FixedArguments.Length > 0) dep.Message = v.FixedArguments[0].Value as string;
                if (v.FixedArguments.Length > 1 && v.FixedArguments[1].Value is bool b) dep.IsError = b;
            }
            catch (BadImageFormatException) { }
            return dep;
        }
        return null;
    }
}
