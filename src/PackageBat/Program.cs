using System.Text.Json;
using System.Text.Json.Serialization;
using PackageBat.Analysis;
using PackageBat.Cli;
using PackageBat.Ecosystems;
using PackageBat.Model;
using PackageBat.Report;

namespace PackageBat;

public static class Program
{
    public const int ExitOk = 0, ExitError = 1, ExitThreshold = 2;

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    const string Help = """
        PackageBat: what will this dependency upgrade break in *my* code?

        Usage:
          packagebat analyze  --package <id> --from <version> --to <version> --consumer <path> [options]
          packagebat analyze  --base <git-ref> [--head <git-ref>] --consumer <path> [options]
          packagebat analyze  --old-api <api.json> --new-api <api.json> --usages <usages.json> [options]
          packagebat detect   --base <git-ref> [--head <git-ref>] [--repo <dir>]
          packagebat api      --package <id> --version <version> [--framework <tfm>]
          packagebat usages   --package <id> --version <version> --consumer <path>

        analyze options:
          --consumer <path>      Built assemblies or a folder to search for them (repeatable). Needs PDBs for file/line.
          --repo-root <dir>      Repository root, for relative paths in the report (default: current dir).
          --framework <tfm>      Package build to compare, e.g. net8.0 (default: inferred from your assemblies).
          --source <url>         NuGet v3 index (default: nuget.org).
          --link-base <url>      Turn locations into links: <url>/<path>#L<line>.
          --markdown <file>      Write the Markdown report here (default: stdout).
          --json <file>          Write the full report as JSON.
          --coverage <file>      Cobertura XML coverage from your tests (repeatable). Adds whether tests run each affected line.
          --fail-on <level>      breaking | binary | warning: exit 2 when used changes at or above this level
          --threshold <n>        ...number more than n (default 0).
          --ecosystem <id>       Default: dotnet.

        detect/api/usages print JSON in PackageBat's language-neutral format,
        so other tools (or other languages) can produce or consume it.
        """;

    public static async Task<int> Main(string[] argv)
    {
        try
        {
            var args = new Args(argv, new HashSet<string> { "help" });
            if (args.Command is null or "help" || args.Flag("help"))
            {
                Console.WriteLine(Help);
                return args.Command is null ? ExitError : ExitOk;
            }
            return args.Command switch
            {
                "analyze" => await Analyze(args),
                "detect" => Detect(args),
                "api" => await Api(args),
                "usages" => await Usages(args),
                _ => throw new UsageException($"Unknown command '{args.Command}'. Run 'packagebat help'."),
            };
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return ExitError;
        }
    }

    static EcosystemOptions Options(Args args) => new()
    {
        RepoRoot = Path.GetFullPath(args.Get("repo-root") ?? args.Get("repo") ?? Directory.GetCurrentDirectory()),
        Framework = args.Get("framework"),
        Source = args.Get("source"),
        ConsumerPaths = args.All("consumer").Select(Path.GetFullPath).ToList(),
    };

    static int Detect(Args args)
    {
        var eco = Ecosystems.Ecosystems.Get(args.Get("ecosystem") ?? "dotnet");
        var repo = Path.GetFullPath(args.Get("repo") ?? Directory.GetCurrentDirectory());
        var bumps = eco.DetectBumps(repo, args.Require("base"), args.Get("head") ?? "HEAD");
        args.EnsureAllUsed();
        Console.WriteLine(JsonSerializer.Serialize(bumps, Json));
        return ExitOk;
    }

    static async Task<int> Api(Args args)
    {
        var eco = Ecosystems.Ecosystems.Get(args.Get("ecosystem") ?? "dotnet");
        var opts = Options(args);
        var api = await eco.GetApiAsync(args.Require("package"), args.Require("version"), opts);
        args.EnsureAllUsed();
        Console.WriteLine(JsonSerializer.Serialize(api, Json));
        return ExitOk;
    }

    static async Task<int> Usages(Args args)
    {
        var eco = Ecosystems.Ecosystems.Get(args.Get("ecosystem") ?? "dotnet");
        var opts = Options(args);
        if (opts.ConsumerPaths.Count == 0) throw new UsageException("--consumer is required.");
        var api = await eco.GetApiAsync(args.Require("package"), args.Require("version"), opts);
        args.EnsureAllUsed();
        Console.WriteLine(JsonSerializer.Serialize(eco.ScanUsages(opts.ConsumerPaths, api, opts), Json));
        return ExitOk;
    }

    static async Task<int> Analyze(Args args)
    {
        var opts = Options(args);
        var report = new BlastRadiusReport();
        var linkBase = args.Get("link-base");
        var markdownOut = args.Get("markdown");
        var jsonOut = args.Get("json");
        var failOn = ParseFailOn(args.Get("fail-on"));
        var threshold = int.TryParse(args.Get("threshold") ?? "0", out var t) ? t : throw new UsageException("--threshold must be a number.");
        // Read coverage up front so a bad file fails before any download.
        var coverage = args.All("coverage") is { Count: > 0 } coverageFiles ? LineCoverage.Read(coverageFiles) : null;

        if (args.Get("old-api") is { } oldApiPath)
        {
            // Language-neutral mode: everything comes from JSON files.
            var oldApi = ReadJson<ApiSurface>(oldApiPath, Validation.Check);
            var newApi = ReadJson<ApiSurface>(args.Require("new-api"), Validation.Check);
            var usages = ReadJson<UsageSet>(args.Require("usages"), Validation.Check);
            args.EnsureAllUsed();
            report.Scanned.AddRange(usages.Scanned);
            report.Packages.Add(UsageMatcher.Analyze(oldApi, newApi, usages));
        }
        else
        {
            var eco = Ecosystems.Ecosystems.Get(args.Get("ecosystem") ?? "dotnet");
            if (opts.ConsumerPaths.Count == 0) throw new UsageException("--consumer is required (your built assemblies, or a folder containing them).");

            List<PackageBump> bumps;
            if (args.Get("base") is { } baseRef)
                bumps = eco.DetectBumps(opts.RepoRoot, baseRef, args.Get("head") ?? "HEAD").ToList();
            else if (args.Get("bumps") is { } bumpsFile)
                bumps = ReadJson<List<PackageBump>>(bumpsFile, Validation.Check);
            else
                bumps = new() { new PackageBump { Ecosystem = eco.Id, Package = args.Require("package"), From = args.Require("from"), To = args.Require("to") } };
            args.EnsureAllUsed();

            // Packages the change adds have nothing to compare, but the run-time check needs to know about them.
            var direct = bumps.Where(b => b.Added != true).ToList();
            var transitive = direct.Count == 0 ? Array.Empty<PackageBump>() : await eco.FindTransitiveBumpsAsync(direct, opts);
            var consumerPackages = bumps.Concat(transitive).Select(b => b.Package).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var scanned = new HashSet<string>();
            foreach (var bump in direct.Concat(transitive))
            {
                try
                {
                    var oldApi = await eco.GetApiAsync(bump.Package, bump.From, opts);
                    var usages = eco.ScanUsages(opts.ConsumerPaths, oldApi, opts);
                    var (runtime, runtimeNote) = await RuntimeDependencies(eco, bump, consumerPackages, opts);
                    if (bump.Via != null && usages.Usages.Count == 0 && runtime.Count == 0 && runtimeNote == null)
                    {
                        report.Unreferenced.Add(bump); // a dependency of a dependency that the code doesn't touch: keep the report short
                        continue;
                    }
                    var newApi = await eco.GetApiAsync(bump.Package, bump.To, opts);
                    scanned.UnionWith(usages.Scanned);
                    var package = UsageMatcher.Analyze(oldApi, newApi, usages);
                    package.Via = bump.Via;
                    package.AllChanges.AddRange(runtime);
                    package.Used.InsertRange(0, runtime.Select(c => new UsedChange { Change = c }));
                    if (runtimeNote != null) package.Notes.Add(runtimeNote);
                    report.Packages.Add(package);
                }
                catch (Exception e) when (e is UsageException or HttpRequestException or IOException or BadImageFormatException)
                {
                    opts.Log.WriteLine($"warning: skipped {bump.Package}: {e.Message}");
                    if (bump.Via != null) continue; // e.g. a metapackage with no assemblies: not worth a section of its own
                    report.Packages.Add(new PackageReport
                    {
                        Ecosystem = eco.Id, Package = bump.Package, FromVersion = bump.From, ToVersion = bump.To,
                        Notes = { $"Could not analyze this package: {e.Message}" },
                    });
                }
            }
            report.Packages = DependenciesAfterTheirParents(report.Packages);
            report.Scanned.AddRange(scanned.Order());
        }

        if (coverage != null)
        {
            var c = coverage.Annotate(report);
            opts.Log.WriteLine($"Coverage from {c.Files.Count} report(s): {c.Covered} of {c.CallSites} affected call site(s) run by tests, {c.NotCovered} not, {c.Unknown} without coverage data.");
        }

        var markdown = MarkdownReport.Render(report, linkBase);
        if (markdownOut != null) await File.WriteAllTextAsync(markdownOut, markdown);
        else Console.WriteLine(markdown);
        if (jsonOut != null) await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(report, Json));

        foreach (var p in report.Packages)
        {
            var runtime = p.Used.Count(u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing);
            opts.Log.WriteLine($"{p.Package} {p.FromVersion} -> {p.ToVersion}{(p.Via != null ? $" (via {p.Via})" : "")}: {p.Used.Count - runtime} used API(s) changed " +
                $"({p.Used.Count(u => u.Change.Severity == Severity.Breaking)} breaking), {p.AllChanges.Count - runtime} changed in total, {p.ReferenceCount} references scanned" +
                (runtime > 0 ? $", {runtime} missing run-time dependenc{(runtime == 1 ? "y" : "ies")}." : "."));
        }
        if (report.Unreferenced.Count > 0)
            opts.Log.WriteLine($"Also followed {report.Unreferenced.Count} dependency version change(s) that your code doesn't reference.");

        if (failOn is { } level)
        {
            var count = report.Packages.Sum(p => p.Used.Count(u => u.Change.Severity <= level));
            if (count > threshold)
            {
                opts.Log.WriteLine($"Failing: {count} used API change(s) at or above '{level}' (threshold {threshold}).");
                return ExitThreshold;
            }
        }
        return ExitOk;
    }

    /// <summary>Puts each transitive bump right after the package that brought it in, so the report reads top-down.</summary>
    static List<PackageReport> DependenciesAfterTheirParents(List<PackageReport> packages)
    {
        var children = packages.Where(p => p.Via != null).ToLookup(p => p.Via!, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<PackageReport>();
        var placed = new HashSet<PackageReport>();
        void Place(PackageReport p)
        {
            if (!placed.Add(p)) return;
            ordered.Add(p);
            foreach (var c in children[p.Package]) Place(c);
        }
        foreach (var p in packages.Where(p => p.Via == null)) Place(p);
        foreach (var p in packages) Place(p); // parents that were left out of the report
        return ordered;
    }

    /// <summary>The run-time check for one bump; a failure becomes a note rather than failing the package.</summary>
    static async Task<(IReadOnlyList<ApiChange> Changes, string? Note)> RuntimeDependencies(IEcosystem eco, PackageBump bump, IReadOnlyCollection<string> consumerPackages, EcosystemOptions opts)
    {
        try
        {
            return (await eco.FindMissingRuntimeDependenciesAsync(bump, consumerPackages, opts), null);
        }
        catch (Exception e) when (e is UsageException or HttpRequestException or IOException or BadImageFormatException)
        {
            opts.Log.WriteLine($"warning: could not check what {bump.Package} {bump.To} loads at run time: {e.Message}");
            return (Array.Empty<ApiChange>(), $"Could not check what this version loads at run time: {e.Message}");
        }
    }

    static Severity? ParseFailOn(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "none" => null,
        "breaking" => Severity.Breaking,
        "binary" => Severity.BinaryOnly,
        "warning" or "any" => Severity.Warning,
        _ => throw new UsageException("--fail-on must be none, breaking, binary or warning."),
    };

    /// <summary>Reads one of PackageBat's JSON files; anything wrong with it is reported as a usage error, not a crash.</summary>
    static T ReadJson<T>(string path, Func<T, string?> check)
    {
        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new UsageException($"{path} is empty.");
        }
        catch (JsonException e)
        {
            throw new UsageException($"{path} is not valid PackageBat JSON: {e.Message}");
        }
        catch (IOException e)
        {
            throw new UsageException($"Could not read {path}: {e.Message}");
        }
        if (check(value) is { } problem)
            throw new UsageException($"{path}: {problem}.");
        return value;
    }
}
