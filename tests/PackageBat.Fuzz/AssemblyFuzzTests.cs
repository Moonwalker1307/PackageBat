using CsCheck;
using System.Reflection;
using PackageBat.Ecosystems;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using Xunit;
using Xunit.Abstractions;

namespace PackageBat.Fuzz;

/// <summary>
/// Feeds corrupted assemblies and PDBs to the .NET extractor and scanner.
///
/// The contract is the one Program.Analyze relies on: a bad package or consumer
/// file may be rejected with BadImageFormatException or IOException (the
/// package is then skipped with a note), but nothing else may escape, and
/// nothing may hang.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class AssemblyFuzzTests(ITestOutputHelper output)
{
    static bool Expected(Exception e) => e is BadImageFormatException or IOException or UsageException;

    static readonly ApiSurface LibrarySurface = ApiExtractor.Extract(new[] { Seeds.LibraryPath }, "Fuzz.Lib", "1.0.0", "net8.0");

    [Fact]
    public void Seeds_are_valid()
    {
        // If these fail the mutations below are fuzzing nothing useful.
        Assert.Contains(LibrarySurface.Types, t => t.Name == "Fuzz.Lib.Client");
        var usages = Scan(Seeds.ConsumerDll, Seeds.ConsumerPdb);
        Assert.Contains(usages.Usages, u => u.TypeName == "Fuzz.Lib.Client" && u.Location.Line != null);
    }

    [Fact]
    public void Api_extractor_survives_corrupt_package_assemblies() =>
        Fuzz.Crashes("api-extractor", output, Mutate.Ops.Select(ops => Mutate.Apply(Seeds.LibraryDll, ops)),
            dll =>
            {
                var path = Path.Combine(Fuzz.ScratchDir, "Fuzz.Lib.dll");
                File.WriteAllBytes(path, dll);
                var api = ApiExtractor.Extract(new[] { path }, "Fuzz.Lib", "1.0.0", "net8.0");
                // What happens next to the surface in a real run.
                _ = Analysis.ApiDiff.Compute(LibrarySurface, api);
                _ = Analysis.ApiDiff.Compute(api, LibrarySurface);
            },
            Expected, dll => dll, ".dll");

    [Fact]
    public void Usage_scanner_survives_corrupt_consumer_assemblies() =>
        Fuzz.Crashes("usage-scanner-dll", output, Mutate.Ops.Select(ops => Mutate.Apply(Seeds.ConsumerDll, ops)),
            dll => Scan(dll, Seeds.ConsumerPdb), Expected, dll => dll, ".dll");

    [Fact]
    public void Usage_scanner_survives_corrupt_pdbs() =>
        Fuzz.Crashes("usage-scanner-pdb", output, Mutate.Ops.Select(ops => Mutate.Apply(Seeds.ConsumerPdb, ops)),
            pdb => Scan(Seeds.ConsumerDll, pdb), Expected, pdb => pdb, ".pdb");

    static readonly MethodInfo InferFramework = typeof(DotNetEcosystem).GetMethod("InferFramework", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void Framework_inference_survives_corrupt_consumer_assemblies() =>
        Fuzz.Crashes("infer-framework", output, Mutate.Ops.Select(ops => Mutate.Apply(Seeds.ConsumerDll, ops)),
            dll =>
            {
                var dir = Fuzz.ScratchDir;
                File.WriteAllBytes(Path.Combine(dir, "Fuzz.App.dll"), dll);
                File.WriteAllBytes(Path.Combine(dir, "Fuzz.App.pdb"), Seeds.ConsumerPdb);
                try { InferFramework.Invoke(null, new object[] { new List<string> { dir } }); }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                }
            },
            Expected, dll => dll, ".dll");

    static UsageSet Scan(byte[] dll, byte[] pdb)
    {
        var dir = Fuzz.ScratchDir;
        var dllPath = Path.Combine(dir, "Fuzz.App.dll");
        File.WriteAllBytes(dllPath, dll);
        File.WriteAllBytes(Path.Combine(dir, "Fuzz.App.pdb"), pdb);
        var usages = UsageScanner.Scan(new[] { dllPath }, LibrarySurface, dir, TextWriter.Null);
        // Usages from a corrupt consumer then go through the matcher.
        _ = Analysis.UsageMatcher.Analyze(LibrarySurface, LibrarySurface, usages);
        return usages;
    }
}
