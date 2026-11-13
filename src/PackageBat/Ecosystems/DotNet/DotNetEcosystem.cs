using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>C#, F# and VB via NuGet packages and compiled assemblies + portable PDBs.</summary>
public sealed class DotNetEcosystem : IEcosystem
{
    public string Id => "dotnet";

    public IReadOnlyList<PackageBump> DetectBumps(string repoRoot, string baseRef, string headRef) =>
        BumpDetector.Detect(repoRoot, baseRef, headRef);

    public async Task<ApiSurface> GetApiAsync(string package, string version, EcosystemOptions options, CancellationToken ct = default)
    {
        var framework = options.Framework ?? InferFramework(options.ConsumerPaths);
        string folder;
        if (Directory.Exists(version) || File.Exists(version))
        {
            // A local build of the package (handy for library authors and tests).
            folder = version;
            version = Path.GetFileName(Path.GetFullPath(version).TrimEnd(Path.DirectorySeparatorChar));
        }
        else
        {
            folder = await new NuGetFetcher(options.Source, options.Log).GetPackageFolderAsync(package, version, ct);
        }

        if (File.Exists(folder))
            return ApiExtractor.Extract(new[] { folder }, package, version, framework);

        var selection = FrameworkSelector.Select(folder, framework)
            ?? throw new UsageException($"{package} {version} has no assemblies for {framework ?? "any framework"}.");
        options.Log.WriteLine($"{package} {version}: using {selection.Framework} ({string.Join(", ", selection.Assemblies.Select(Path.GetFileName))})");
        return ApiExtractor.Extract(selection.Assemblies, package, version, selection.Framework);
    }

    public UsageSet ScanUsages(IReadOnlyList<string> consumerPaths, ApiSurface package, EcosystemOptions options) =>
        UsageScanner.Scan(consumerPaths, package, options.RepoRoot, options.Log);

    /// <summary>Reads [TargetFramework] from the first consumer assembly, so we pick the same package build the consumer compiled against.</summary>
    static string? InferFramework(IReadOnlyList<string> consumerPaths)
    {
        foreach (var dll in UsageScanner.FindAssemblies(consumerPaths).Take(50))
        {
            try
            {
                using var s = File.OpenRead(dll);
                using var pe = new PEReader(s);
                if (!pe.HasMetadata) continue;
                var r = pe.GetMetadataReader();
                if (!r.IsAssembly) continue;
                foreach (var h in r.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var ca = r.GetCustomAttribute(h);
                    if (MetadataNames.AttributeTypeName(r, ca) != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                    var value = ca.DecodeValue(TypeNameProvider.Key);
                    if (value.FixedArguments.Length > 0 && FrameworkSelector.FromTargetFrameworkAttribute(value.FixedArguments[0].Value as string) is { } tfm)
                        return tfm;
                }
            }
            catch (Exception e) when (e is IOException || MetadataNames.IsMalformed(e)) { }
        }
        return null;
    }
}
