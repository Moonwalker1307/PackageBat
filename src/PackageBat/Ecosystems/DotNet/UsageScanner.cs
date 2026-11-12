using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// Finds references into a package in the consumer's compiled assemblies and
/// maps them back to source lines through the portable PDB. Working from the
/// compiled output means overload resolution, extension methods, implicit
/// conversions etc. are already done by the compiler, so what we see is
/// exactly what the code binds to.
/// </summary>
internal static class UsageScanner
{
    public static UsageSet Scan(IReadOnlyList<string> consumerPaths, ApiSurface package, string repoRoot, TextWriter log)
    {
        var set = new UsageSet { Ecosystem = "dotnet" };
        var packageModules = new HashSet<string>(package.Modules, StringComparer.OrdinalIgnoreCase);
        var seenMvids = new HashSet<Guid>();

        foreach (var dll in FindAssemblies(consumerPaths))
        {
            try
            {
                using var stream = File.OpenRead(dll);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var r = pe.GetMetadataReader();
                if (!r.IsAssembly) continue;
                var name = r.GetString(r.GetAssemblyDefinition().Name);
                if (packageModules.Contains(name)) continue; // the package itself, copied to bin
                if (!seenMvids.Add(r.GetGuid(r.GetModuleDefinition().Mvid))) continue; // same build copied to another bin folder
                if (!r.AssemblyReferences.Any(h => packageModules.Contains(r.GetString(r.GetAssemblyReference(h).Name)))) continue;

                MetadataReaderProvider? pdbProvider = null;
                try
                {
                    pe.TryOpenAssociatedPortablePdb(dll, p => File.Exists(p) ? File.OpenRead(p) : null, out pdbProvider, out _);
                }
                catch (Exception e) when (e is BadImageFormatException or IOException) { }
                using (pdbProvider)
                {
                    var pdb = pdbProvider?.GetMetadataReader();
                    if (pdb == null)
                        log.WriteLine($"warning: no portable PDB for {dll}; usages will have no file/line.");
                    var before = set.Usages.Count;
                    new AssemblyScan(r, pdb, name, packageModules, repoRoot, set.Usages).WithBodies(pe).Run();
                    set.Scanned.Add(Rel(dll, repoRoot));
                    log.WriteLine($"Scanned {name}: {set.Usages.Count - before} references");
                }
            }
            catch (BadImageFormatException) { }
        }
        return set;
    }

    /// <summary>Expands directories into the assemblies a build produced (those with a PDB next to them).</summary>
    internal static IEnumerable<string> FindAssemblies(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p)) { yield return Path.GetFullPath(p); continue; }
            if (!Directory.Exists(p)) continue;
            var sep = Path.DirectorySeparatorChar;
            foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
            {
                if (!(f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) continue;
                if (f.Contains($"{sep}obj{sep}") || f.Contains($"{sep}.git{sep}") || f.Contains($"{sep}node_modules{sep}")) continue;
                if (!File.Exists(Path.ChangeExtension(f, ".pdb"))) continue;
                yield return Path.GetFullPath(f);
            }
        }
    }

    internal static string Rel(string path, string repoRoot)
    {
        path = path.Replace('\\', '/');
        // Deterministic CI builds map the source root to /_/
        if (path.StartsWith("/_/")) return path[3..];
        var root = Path.GetFullPath(repoRoot).Replace('\\', '/').TrimEnd('/') + "/";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path[root.Length..] : path;
    }

    sealed class AssemblyScan(MetadataReader r, MetadataReader? pdb, string module, HashSet<string> packageModules, string repoRoot, List<Usage> output)
    {
        readonly Dictionary<EntityHandle, string?> _typeCache = new();

        public void Run()
        {
            foreach (var th in r.TypeDefinitions)
            {
                var td = r.GetTypeDefinition(th);
                ScanTypeShape(th, td);
                foreach (var mh in td.GetMethods())
                {
                    ScanSignature(mh);
                    ScanBody(mh);
                }
                // Explicit interface implementations / overrides of package members.
                foreach (var ih in td.GetMethodImplementations())
                {
                    var impl = r.GetMethodImplementation(ih);
                    if (impl.MethodDeclaration.Kind == HandleKind.MemberReference && Member((MemberReferenceHandle)impl.MethodDeclaration) is { } m)
                        Emit(m.Type, m.Key, UsageKind.Override, LocateEntity(impl.MethodBody), SymbolOf(impl.MethodBody));
                }
                foreach (var fh in td.GetFields())
                {
                    var fd = r.GetFieldDefinition(fh);
                    foreach (var t in PackageTypesIn(fd.DecodeSignature(TypeNameProvider.Key, null)))
                        Emit(t, null, UsageKind.Type, LocateType(th), Symbol(th) + "." + r.GetString(fd.Name));
                }
            }

            foreach (var ch in r.CustomAttributes)
            {
                var ca = r.GetCustomAttribute(ch);
                if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                if (Member((MemberReferenceHandle)ca.Constructor) is { } m)
                    Emit(m.Type, m.Key, UsageKind.Member, LocateEntity(ca.Parent), SymbolOf(ca.Parent));
            }

        }

        void ScanTypeShape(TypeDefinitionHandle th, TypeDefinition td)
        {
            if (!td.BaseType.IsNil && PackageType(td.BaseType) is { } baseType)
            {
                Emit(baseType, null, UsageKind.Inherit, LocateType(th), Symbol(th));
                // Every virtual method could be overriding something in the package; the matcher keeps only real overrides.
                foreach (var mh in td.GetMethods())
                {
                    var md = r.GetMethodDefinition(mh);
                    if ((md.Attributes & MethodAttributes.Virtual) == 0 || (md.Attributes & MethodAttributes.NewSlot) != 0) continue;
                    var key = MetadataNames.MethodKey(r.GetString(md.Name), md.DecodeSignature(TypeNameProvider.Key, null));
                    Emit(baseType, key, UsageKind.Override, LocateMethod(mh), Symbol(mh));
                }
            }
            foreach (var ih in td.GetInterfaceImplementations())
                if (PackageType(r.GetInterfaceImplementation(ih).Interface) is { } iface)
                    Emit(iface, null, UsageKind.Implement, LocateType(th), Symbol(th));
        }

        void ScanSignature(MethodDefinitionHandle mh)
        {
            MethodSignature<string> sig;
            try { sig = r.GetMethodDefinition(mh).DecodeSignature(TypeNameProvider.Key, null); }
            catch (BadImageFormatException) { return; }
            foreach (var t in sig.ParameterTypes.Append(sig.ReturnType).SelectMany(PackageTypesIn).Distinct())
                Emit(t, null, UsageKind.Type, LocateMethod(mh), Symbol(mh));
        }

        void ScanBody(MethodDefinitionHandle mh)
        {
            var md = r.GetMethodDefinition(mh);
            if (md.RelativeVirtualAddress == 0) return;
            MethodBodyBlock? body;
            try { body = _peBody(md.RelativeVirtualAddress); }
            catch (BadImageFormatException) { return; }
            if (body == null) return;

            var il = body.GetILReader();
            var points = SequencePoints(mh);
            while (il.RemainingBytes > 0)
            {
                int offset = il.Offset;
                int code = il.ReadByte();
                OperandType operand;
                if (code == 0xFE) { code = il.ReadByte(); operand = IlTable.TwoByte[code]; }
                else operand = IlTable.OneByte[code];

                switch (operand)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        var token = il.ReadInt32();
                        HandleToken(token, mh, offset, points);
                        break;
                    case OperandType.InlineSwitch:
                        var n = il.ReadInt32();
                        il.Offset += 4 * n;
                        break;
                    default:
                        il.Offset += IlTable.OperandSize(operand);
                        break;
                }
            }
        }

        Func<int, MethodBodyBlock?> _peBody = _ => null;
        public AssemblyScan WithBodies(PEReader pe) { _peBody = rva => pe.GetMethodBody(rva); return this; }

        void HandleToken(int token, MethodDefinitionHandle mh, int offset, IReadOnlyList<SequencePoint> points)
        {
            EntityHandle h;
            try { h = MetadataTokens.EntityHandle(token); } catch (ArgumentException) { return; }
            switch (h.Kind)
            {
                case HandleKind.MethodSpecification:
                    var ms = r.GetMethodSpecification((MethodSpecificationHandle)h);
                    if (ms.Method.Kind == HandleKind.MemberReference) goto case HandleKind.MemberReference;
                    foreach (var t in PackageTypesInMethodSpec(ms))
                        Emit(t, null, UsageKind.Type, Locate(points, offset, mh), Symbol(mh));
                    return;
                case HandleKind.MemberReference:
                    var mrh = h.Kind == HandleKind.MethodSpecification
                        ? (MemberReferenceHandle)r.GetMethodSpecification((MethodSpecificationHandle)h).Method
                        : (MemberReferenceHandle)h;
                    if (Member(mrh) is { } m)
                        Emit(m.Type, m.Key, UsageKind.Member, Locate(points, offset, mh), Symbol(mh));
                    if (h.Kind == HandleKind.MethodSpecification)
                        foreach (var t in PackageTypesInMethodSpec(r.GetMethodSpecification((MethodSpecificationHandle)h)))
                            Emit(t, null, UsageKind.Type, Locate(points, offset, mh), Symbol(mh));
                    return;
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    if (PackageType(h) is { } tn)
                        Emit(tn, null, UsageKind.Type, Locate(points, offset, mh), Symbol(mh));
                    if (h.Kind == HandleKind.TypeSpecification)
                    {
                        string spec;
                        try { spec = r.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(TypeNameProvider.Key, null); }
                        catch (BadImageFormatException) { return; }
                        foreach (var t in PackageTypesIn(spec))
                            Emit(t, null, UsageKind.Type, Locate(points, offset, mh), Symbol(mh));
                    }
                    return;
            }
        }

        IEnumerable<string> PackageTypesInMethodSpec(MethodSpecification ms)
        {
            System.Collections.Immutable.ImmutableArray<string> args;
            try { args = ms.DecodeSignature(TypeNameProvider.Key, null); }
            catch (BadImageFormatException) { yield break; }
            foreach (var a in args)
                foreach (var t in PackageTypesIn(a))
                    yield return t;
        }

        /// <summary>A member reference into the package, as (declaring type, member key).</summary>
        (string Type, string Key)? Member(MemberReferenceHandle h)
        {
            var mr = r.GetMemberReference(h);
            if (PackageType(mr.Parent) is not { } type) return null;
            var name = r.GetString(mr.Name);
            try
            {
                return mr.GetKind() == MemberReferenceKind.Method
                    ? (type, MetadataNames.MethodKey(name, mr.DecodeMethodSignature(TypeNameProvider.Key, null)))
                    : (type, MetadataNames.FieldKey(name, mr.DecodeFieldSignature(TypeNameProvider.Key, null)));
            }
            catch (BadImageFormatException) { return null; }
        }

        /// <summary>The package type name for a TypeRef/TypeSpec handle, or null when it isn't in the package.</summary>
        string? PackageType(EntityHandle h)
        {
            if (_typeCache.TryGetValue(h, out var cached)) return cached;
            string? result = null;
            var u = MetadataNames.UnderlyingType(r, h);
            if (!u.IsNil && u.Kind == HandleKind.TypeReference
                && MetadataNames.TargetAssembly(r, (TypeReferenceHandle)u) is { } asm && packageModules.Contains(asm))
                result = MetadataNames.FullName(r, (TypeReferenceHandle)u);
            return _typeCache[h] = result;
        }

        HashSet<string>? _packageTypeNames;

        /// <summary>Package type names mentioned anywhere in a key-formatted signature string.</summary>
        IEnumerable<string> PackageTypesIn(string signature)
        {
            _packageTypeNames ??= r.TypeReferences
                .Where(t => MetadataNames.TargetAssembly(r, t) is { } a && packageModules.Contains(a))
                .Select(t => MetadataNames.FullName(r, t))
                .ToHashSet(StringComparer.Ordinal);
            if (_packageTypeNames.Count == 0) yield break;
            foreach (var token in signature.Split(SigSeparators, StringSplitOptions.RemoveEmptyEntries))
                if (_packageTypeNames.Contains(token))
                    yield return token;
        }

        static readonly char[] SigSeparators = { '<', '>', ',', '[', ']', '&', '*', '(', ')', ':', ' ' };

        void Emit(string type, string? key, UsageKind kind, SourceLocation loc, string symbol)
        {
            loc.Module = module;
            loc.Symbol = symbol;
            output.Add(new Usage { TypeName = type, MemberKey = key, Kind = kind, Location = loc });
        }

        // ---- source locations -------------------------------------------------

        readonly Dictionary<MethodDefinitionHandle, IReadOnlyList<SequencePoint>> _points = new();

        IReadOnlyList<SequencePoint> SequencePoints(MethodDefinitionHandle mh)
        {
            if (_points.TryGetValue(mh, out var p)) return p;
            var list = new List<SequencePoint>();
            if (pdb != null)
            {
                try
                {
                    var info = pdb.GetMethodDebugInformation(mh.ToDebugInformationHandle());
                    foreach (var sp in info.GetSequencePoints())
                        if (!sp.IsHidden) list.Add(sp);
                }
                catch (BadImageFormatException) { }
            }
            return _points[mh] = list;
        }

        SourceLocation Locate(IReadOnlyList<SequencePoint> points, int offset, MethodDefinitionHandle mh)
        {
            SequencePoint? best = null;
            foreach (var sp in points)
            {
                if (sp.Offset > offset) break;
                best = sp;
            }
            best ??= points.Count > 0 ? points[0] : null;
            return best is { } b ? At(b) : new SourceLocation();
        }

        SourceLocation At(SequencePoint sp) => new()
        {
            File = Rel(pdb!.GetString(pdb.GetDocument(sp.Document).Name), repoRoot),
            Line = sp.StartLine,
        };

        SourceLocation LocateMethod(MethodDefinitionHandle mh)
        {
            var pts = SequencePoints(mh);
            return pts.Count > 0 ? At(pts[0]) : new SourceLocation();
        }

        SourceLocation LocateType(TypeDefinitionHandle th)
        {
            // A type has no line of its own in the PDB; use the earliest line of its methods
            // (in the first document) as a close stand-in for "where the class is".
            SequencePoint? best = null;
            foreach (var mh in r.GetTypeDefinition(th).GetMethods())
            {
                var pts = SequencePoints(mh);
                if (pts.Count == 0) continue;
                var first = pts[0];
                if (best == null || (first.Document == best.Value.Document && first.StartLine < best.Value.StartLine))
                    best = first;
            }
            return best is { } b ? At(b) : TypeDocument(th);
        }

        static readonly Guid TypeDefinitionDocumentsKind = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");

        /// <summary>
        /// Types with no method bodies of their own (e.g. "class MyWidget : Widget { }")
        /// have no sequence points; newer compilers record their file separately.
        /// </summary>
        SourceLocation TypeDocument(TypeDefinitionHandle th)
        {
            if (pdb == null) return new SourceLocation();
            try
            {
                foreach (var h in pdb.GetCustomDebugInformation(th))
                {
                    var cdi = pdb.GetCustomDebugInformation(h);
                    if (pdb.GetGuid(cdi.Kind) != TypeDefinitionDocumentsKind) continue;
                    var blob = pdb.GetBlobReader(cdi.Value);
                    if (blob.RemainingBytes == 0) break;
                    var doc = System.Reflection.Metadata.Ecma335.MetadataTokens.DocumentHandle(blob.ReadCompressedInteger());
                    return new SourceLocation { File = Rel(pdb.GetString(pdb.GetDocument(doc).Name), repoRoot) };
                }
            }
            catch (BadImageFormatException) { }
            return new SourceLocation();
        }

        SourceLocation LocateEntity(EntityHandle h) => h.Kind switch
        {
            HandleKind.MethodDefinition => LocateMethod((MethodDefinitionHandle)h),
            HandleKind.TypeDefinition => LocateType((TypeDefinitionHandle)h),
            HandleKind.FieldDefinition => LocateType(r.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType()),
            HandleKind.PropertyDefinition => r.GetPropertyDefinition((PropertyDefinitionHandle)h).GetAccessors() is var a && !a.Getter.IsNil ? LocateMethod(a.Getter)
                : !a.Setter.IsNil ? LocateMethod(a.Setter) : new SourceLocation(),
            HandleKind.Parameter => new SourceLocation(),
            _ => new SourceLocation(),
        };

        string Symbol(TypeDefinitionHandle th) => MetadataNames.FullName(r, th);
        string Symbol(MethodDefinitionHandle mh)
        {
            var md = r.GetMethodDefinition(mh);
            return MetadataNames.FullName(r, md.GetDeclaringType()) + "." + r.GetString(md.Name);
        }
        string SymbolOf(EntityHandle h) => h.Kind switch
        {
            HandleKind.MethodDefinition => Symbol((MethodDefinitionHandle)h),
            HandleKind.TypeDefinition => Symbol((TypeDefinitionHandle)h),
            HandleKind.FieldDefinition => Symbol(r.GetFieldDefinition((FieldDefinitionHandle)h).GetDeclaringType()) + "." + r.GetString(r.GetFieldDefinition((FieldDefinitionHandle)h).Name),
            HandleKind.PropertyDefinition => r.GetString(r.GetPropertyDefinition((PropertyDefinitionHandle)h).Name),
            _ => module,
        };
    }

    static class IlTable
    {
        public static readonly OperandType[] OneByte = new OperandType[256];
        public static readonly OperandType[] TwoByte = new OperandType[256];

        static IlTable()
        {
            Array.Fill(OneByte, OperandType.InlineNone);
            Array.Fill(TwoByte, OperandType.InlineNone);
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                var v = (ushort)op.Value;
                if (op.Size == 1) OneByte[v] = op.OperandType;
                else TwoByte[v & 0xFF] = op.OperandType;
            }
        }

        public static int OperandSize(OperandType t) => t switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            _ => 4,
        };
    }
}
