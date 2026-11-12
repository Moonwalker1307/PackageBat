using PackageBat.Model;

namespace PackageBat.Ecosystems;

/// <summary>
/// Everything that depends on a language or package manager lives behind this
/// interface. The rest of PackageBat (diff, matching, report, the GitHub
/// Action) only sees the neutral model in PackageBat.Model.
///
/// To add a language, implement this (or produce the same JSON from any
/// tool in any language and feed it to `packagebat analyze --old-api/--new-api/--usages`).
/// </summary>
public interface IEcosystem
{
    /// <summary>Short id used on the command line and in JSON, e.g. "dotnet".</summary>
    string Id { get; }

    /// <summary>Finds dependency version changes between two git revisions of a repository.</summary>
    IReadOnlyList<PackageBump> DetectBumps(string repoRoot, string baseRef, string headRef);

    /// <summary>Downloads (or finds locally) a package version and extracts its public API.</summary>
    Task<ApiSurface> GetApiAsync(string package, string version, EcosystemOptions options, CancellationToken ct = default);

    /// <summary>Finds every reference to the given package's modules in the consumer's code.</summary>
    UsageSet ScanUsages(IReadOnlyList<string> consumerPaths, ApiSurface package, EcosystemOptions options);
}

public sealed class EcosystemOptions
{
    /// <summary>Repository root; source paths in the report are made relative to it.</summary>
    public string RepoRoot { get; set; } = Directory.GetCurrentDirectory();
    /// <summary>Target framework / platform to pick from the package (ecosystem specific, e.g. "net8.0").</summary>
    public string? Framework { get; set; }
    /// <summary>Package source (ecosystem specific, e.g. a NuGet v3 index URL).</summary>
    public string? Source { get; set; }
    /// <summary>Consumer paths; ecosystems may use them to infer the framework.</summary>
    public IReadOnlyList<string> ConsumerPaths { get; set; } = Array.Empty<string>();
    public TextWriter Log { get; set; } = Console.Error;
}

public static class Ecosystems
{
    public static readonly IReadOnlyList<IEcosystem> All = new IEcosystem[] { new DotNet.DotNetEcosystem() };

    public static IEcosystem Get(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new UsageException($"Unknown ecosystem '{id}'. Known: {string.Join(", ", All.Select(e => e.Id))}");
}

public sealed class UsageException(string message) : Exception(message);
