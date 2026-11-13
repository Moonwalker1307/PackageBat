using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace PackageBat.Tests;

/// <summary>Compiles C# snippets to a dll (+ portable PDB) on disk, so tests need no network or fixture projects.</summary>
internal static class Compiler
{
    static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p) is var n && (n.StartsWith("System.") || n is "mscorlib.dll" or "netstandard.dll"))
        .Select(p => MetadataReference.CreateFromFile(p))
        .ToArray();

    public static string Compile(string dir, string assemblyName, string version, IEnumerable<(string Path, string Code)> files, params string[] references)
    {
        Directory.CreateDirectory(dir);
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Code, path: f.Path, encoding: Encoding.UTF8))
            .Append(CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]", path: "AssemblyInfo.cs", encoding: Encoding.UTF8));
        var compilation = CSharpCompilation.Create(assemblyName, trees,
            Framework.Concat(references.Select(r => MetadataReference.CreateFromFile(r))),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));

        var dll = Path.Combine(dir, assemblyName + ".dll");
        var pdb = Path.Combine(dir, assemblyName + ".pdb");
        using (var dllStream = File.Create(dll))
        using (var pdbStream = File.Create(pdb))
        {
            var result = compilation.Emit(dllStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: pdb));
            if (!result.Success)
                throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
        return dll;
    }
}
