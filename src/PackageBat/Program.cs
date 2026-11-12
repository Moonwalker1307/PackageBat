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

            var scanned = new HashSet<string>();
            foreach (var bump in bumps)
            {
                try
                {
                    var oldApi = await eco.GetApiAsync(bump.Package, bump.From, opts);
                    var newApi = await eco.GetApiAsync(bump.Package, bump.To, opts);
                    var usages = eco.ScanUsages(opts.ConsumerPaths, oldApi, opts);
                    scanned.UnionWith(usages.Scanned);
                    report.Packages.Add(UsageMatcher.Analyze(oldApi, newApi, usages));
                }
                catch (Exception e) when (e is UsageException or HttpRequestException or IOException or BadImageFormatException)
                {
                    opts.Log.WriteLine($"warning: skipped {bump.Package}: {e.Message}");
                    report.Packages.Add(new PackageReport
                    {
                        Ecosystem = eco.Id, Package = bump.Package, FromVersion = bump.From, ToVersion = bump.To,
                        Notes = { $"Could not analyze this package: {e.Message}" },
                    });
                }
            }
            report.Scanned.AddRange(scanned.Order());
        }

        var markdown = MarkdownReport.Render(report, linkBase);
        if (markdownOut != null) await File.WriteAllTextAsync(markdownOut, markdown);
        else Console.WriteLine(markdown);
        if (jsonOut != null) await File.WriteAllTextAsync(jsonOut, JsonSerializer.Serialize(report, Json));

        foreach (var p in report.Packages)
            opts.Log.WriteLine($"{p.Package} {p.FromVersion} -> {p.ToVersion}: {p.Used.Count} used API(s) changed " +
                $"({p.Used.Count(u => u.Change.Severity == Severity.Breaking)} breaking), {p.AllChanges.Count} changed in total, {p.ReferenceCount} references scanned.");

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
