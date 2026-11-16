using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// A string in a package's code that names another assembly, the way code that
/// loads a type at run time does: "Namespace.Type, AssemblyName" for
/// Type.GetType, or a bare assembly name passed to Assembly.Load. Such a
/// dependency is invisible in metadata: no assembly reference, no nuspec entry.
/// </summary>
/// <param name="Literal">The string as it appears in the IL.</param>
/// <param name="TypeName">The type part, or null for a bare assembly name.</param>
/// <param name="Loader">The loading API the string flows straight into (e.g. "Type.GetType"), if it does.</param>
internal sealed record LateBoundName(string AssemblyName, string? TypeName, string Literal, string? Loader, string Module);

/// <summary>Finds late-bound assembly names in method bodies (ldstr operands), reading metadata only.</summary>
internal static partial class LateBoundScanner
{
    /// <summary>How many instructions after the ldstr a loader call still counts as taking that string.</summary>
    const int FlowWindow = 6;

    public static IReadOnlyList<LateBoundName> Scan(IEnumerable<string> assemblyPaths)
    {
        var result = new List<LateBoundName>();
        foreach (var path in assemblyPaths)
        {
            try
            {
                ScanOne(path, result);
            }
            catch (Exception e) when (e is not BadImageFormatException && MetadataNames.IsMalformed(e))
            {
                throw new BadImageFormatException($"{Path.GetFileName(path)} has malformed metadata: {e.Message}", path, e);
            }
        }
        return result;
    }

    static void ScanOne(string path, List<LateBoundName> output)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return;
        var r = pe.GetMetadataReader();
        if (!r.IsAssembly) return;
        var module = r.GetString(r.GetAssemblyDefinition().Name);
        var seen = new HashSet<(string, string?)>();

        foreach (var th in r.TypeDefinitions)
        {
            foreach (var mh in r.GetTypeDefinition(th).GetMethods())
            {
                var md = r.GetMethodDefinition(mh);
                if (md.RelativeVirtualAddress == 0) continue;
                MethodBodyBlock body;
                try { body = pe.GetMethodBody(md.RelativeVirtualAddress); }
                catch (BadImageFormatException) { continue; }
                ScanBody(r, body.GetILReader(), module, output, seen);
            }
        }
    }

    static void ScanBody(MetadataReader r, BlobReader il, string module, List<LateBoundName> output, HashSet<(string, string?)> seen)
    {
        // Strings seen recently, waiting to see whether a loader call takes them.
        var pending = new List<(string Literal, int Instruction)>();
        int instruction = 0;
        while (il.RemainingBytes > 0)
        {
            instruction++;
            int code = il.ReadByte();
            OperandType operand;
            if (code == 0xFE)
            {
                if (il.RemainingBytes == 0) break;
                code = il.ReadByte();
                operand = IlTable.TwoByte[code];
            }
            else operand = IlTable.OneByte[code];

            if (operand == OperandType.InlineString)
            {
                if (il.RemainingBytes < 4) break;
                var token = il.ReadInt32();
                if (UserString(r, token) is { Length: > 0 and <= 1024 } s && Parse(s) is not null)
                    pending.Add((s, instruction));
                continue;
            }
            if (operand == OperandType.InlineMethod && (code is 0x28 or 0x6F or 0x73) && il.RemainingBytes >= 4)
            {
                var loader = Loader(r, il.ReadInt32());
                pending.RemoveAll(p => instruction - p.Instruction > FlowWindow && Emit(p.Literal, null));
                if (loader != null)
                {
                    foreach (var p in pending) Emit(p.Literal, loader);
                    pending.Clear();
                }
                continue;
            }
            if (operand == OperandType.InlineSwitch)
            {
                if (il.RemainingBytes < 4) break;
                var n = il.ReadInt32();
                if (n < 0 || (long)n * 4 > il.RemainingBytes) break;
                il.Offset += 4 * n;
                continue;
            }
            var size = IlTable.OperandSize(operand);
            if (size > il.RemainingBytes) break;
            il.Offset += size;
        }
        foreach (var p in pending) Emit(p.Literal, null);

        bool Emit(string literal, string? loader)
        {
            var parsed = Parse(literal);
            if (parsed is not { } name) return true;
            // A bare dotted name is only an assembly name when it goes into Assembly.Load.
            if (name.TypeName == null && loader is not ("Assembly.Load" or "AppDomain.Load")) return true;
            if (seen.Add((literal, loader)))
                output.Add(new LateBoundName(name.AssemblyName, name.TypeName, literal, loader, module));
            return true;
        }
    }

    static string? UserString(MetadataReader r, int token)
    {
        if ((token >> 24) != 0x70) return null;
        try { return r.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)); }
        catch (Exception e) when (e is ArgumentException or BadImageFormatException) { return null; }
    }

    /// <summary>"Type.GetType" etc. when the call target is an API that loads a type or assembly by name; otherwise null.</summary>
    static string? Loader(MetadataReader r, int token)
    {
        EntityHandle h;
        try { h = MetadataTokens.EntityHandle(token); } catch (ArgumentException) { return null; }
        if (h.IsNil || !MetadataTokens.TryGetTableIndex(h.Kind, out var table) || MetadataTokens.GetRowNumber(h) > r.GetTableRowCount(table)) return null;
        if (h.Kind == HandleKind.MethodSpecification)
        {
            h = r.GetMethodSpecification((MethodSpecificationHandle)h).Method;
            if (h.IsNil || !MetadataTokens.TryGetTableIndex(h.Kind, out table) || MetadataTokens.GetRowNumber(h) > r.GetTableRowCount(table)) return null;
        }
        if (h.Kind != HandleKind.MemberReference) return null;
        var mr = r.GetMemberReference((MemberReferenceHandle)h);
        if (mr.Parent.Kind != HandleKind.TypeReference) return null;
        var type = MetadataNames.FullName(r, (TypeReferenceHandle)mr.Parent);
        var name = r.GetString(mr.Name);
        return (type, name) switch
        {
            ("System.Type", "GetType") => "Type.GetType",
            ("System.Reflection.Assembly", "Load") => "Assembly.Load",
            ("System.AppDomain", "Load") => "AppDomain.Load",
            ("System.Activator", "CreateInstance") => "Activator.CreateInstance",
            ("System.AppDomain", "CreateInstance" or "CreateInstanceAndUnwrap") => "AppDomain.CreateInstance",
            _ => null,
        };
    }

    /// <summary>
    /// Splits "Namespace.Type, Assembly[, Version=...]" or a bare "Some.Assembly".
    /// Null for anything else, including sentences that happen to contain a comma.
    /// </summary>
    internal static (string AssemblyName, string? TypeName)? Parse(string s)
    {
        try { return TryParse(s); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    static (string AssemblyName, string? TypeName)? TryParse(string s)
    {
        s = s.Trim();
        var m = QualifiedName().Match(s);
        if (m.Success)
        {
            var type = m.Groups["type"].Value;
            // A namespace is required, so "Hello, World" or "Done, Retry" don't count.
            if (!type.Contains('.')) return null;
            return (m.Groups["asm"].Value, type);
        }
        m = BareAssemblyName().Match(s);
        return m.Success ? (s, null) : null;
    }

    [GeneratedRegex(@"^(?<type>[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*(?:`[0-9]+)?(?:\[.*\])?)\s*,\s*(?<asm>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:,\s*(?:Version|Culture|PublicKeyToken)\s*=\s*[^,]*)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex BareAssemblyName();
}
