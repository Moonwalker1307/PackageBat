using System.Text.Json;
using PackageBat.Cli;
using PackageBat.Ecosystems;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// Runs the CLI in its language-neutral mode (JSON in, report out), so the
/// exit codes the GitHub Action relies on are covered without network access.
/// </summary>
public sealed class CliTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "packagebat-cli-tests", Guid.NewGuid().ToString("N"));
    readonly string _old, _new, _usages;

    public CliTests()
    {
        Directory.CreateDirectory(_dir);

        static ApiMember M(string name, string ret = "void", Deprecation? dep = null, params ApiParameter[] ps) => new()
        {
            Key = $"{name}({string.Join(",", ps.Select(p => p.Type))}):{ret}", Name = name, DeclaringType = "Acme.Client",
            Display = $"Client.{name}", ReturnType = ret, Parameters = ps.ToList(), Deprecated = dep,
        };
        static ApiParameter P(string t, bool optional = false) => new() { Type = t, HasDefault = optional };
        static ApiSurface Api(string version, params ApiMember[] ms) => new()
        {
            Ecosystem = "dotnet", Package = "Acme", Version = version,
            Types = { new ApiType { Name = "Acme.Client", Display = "Client", Members = ms.ToList() } },
        };

        var send = M("Send", ps: P("string"));
        var get = M("Get", ps: P("string"));
        var retry = M("Retry");
        var oldApi = Api("1.0.0", send, get, retry);
        var newApi = Api("2.0.0",
            M("Send", ps: P("byte[]")),                             // breaking
            M("Get", ps: new[] { P("string"), P("int", true) }),    // binary only
            M("Retry", dep: new Deprecation { Message = "x" }));    // warning
        var usages = new UsageSet
        {
            Ecosystem = "dotnet",
            Scanned = { "App.dll" },
            Usages = new[] { send, get, retry }.Select((m, i) => new Usage
            {
                TypeName = "Acme.Client", MemberKey = m.Key, Kind = UsageKind.Member,
                Location = new SourceLocation { File = "src/App.cs", Line = 10 + i, Module = "App" },
            }).ToList(),
        };

        _old = Write("old.json", oldApi);
        _new = Write("new.json", newApi);
        _usages = Write("usages.json", usages);
    }

    string Write<T>(string name, T value)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Program.Json));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    Task<int> Analyze(params string[] extra) => Program.Main(new[]
    {
        "analyze", "--old-api", _old, "--new-api", _new, "--usages", _usages, "--markdown", Path.Combine(_dir, "out.md"),
    }.Concat(extra).ToArray());

    [Theory]
    [InlineData(null, null, Program.ExitOk)]
    [InlineData("none", null, Program.ExitOk)]
    [InlineData("breaking", null, Program.ExitThreshold)]
    [InlineData("BREAKING", null, Program.ExitThreshold)]
    [InlineData("breaking", "1", Program.ExitOk)]           // 1 breaking is not more than 1
    [InlineData("binary", "1", Program.ExitThreshold)]      // breaking + binary = 2
    [InlineData("binary", "2", Program.ExitOk)]
    [InlineData("warning", "2", Program.ExitThreshold)]     // all 3
    [InlineData("any", "2", Program.ExitThreshold)]
    [InlineData("warning", "3", Program.ExitOk)]
    public async Task Fail_on_counts_used_changes_at_or_above_the_level(string? failOn, string? threshold, int expected)
    {
        var extra = new List<string>();
        if (failOn != null) extra.AddRange(new[] { "--fail-on", failOn });
        if (threshold != null) extra.AddRange(new[] { "--threshold", threshold });
        Assert.Equal(expected, await Analyze(extra.ToArray()));
    }

    [Theory]
    [InlineData("--fail-on", "sometimes")]
    [InlineData("--threshold", "lots")]
    [InlineData("--bogus", "1")]
    public async Task Bad_options_are_usage_errors(string name, string value) =>
        Assert.Equal(Program.ExitError, await Analyze(name, value));

    [Fact]
    public async Task Writes_markdown_and_json_reports()
    {
        var json = Path.Combine(_dir, "out.json");
        Assert.Equal(Program.ExitOk, await Analyze("--json", json, "--link-base", "https://example.test/blob/sha"));

        var md = File.ReadAllText(Path.Combine(_dir, "out.md"));
        Assert.StartsWith(Report.MarkdownReport.Marker, md);
        Assert.Contains("[src/App.cs:10](https://example.test/blob/sha/src/App.cs#L10)", md);

        var report = JsonSerializer.Deserialize<BlastRadiusReport>(File.ReadAllText(json), Program.Json)!;
        Assert.Equal(new[] { "App.dll" }, report.Scanned);
        var p = Assert.Single(report.Packages);
        Assert.Equal(("Acme", "1.0.0", "2.0.0"), (p.Package, p.FromVersion, p.ToVersion));
        Assert.Equal(new[] { Severity.Breaking, Severity.BinaryOnly, Severity.Warning }, p.Used.Select(u => u.Change.Severity));
    }

    [Fact]
    public async Task Coverage_file_annotates_the_report()
    {
        var coverage = Path.Combine(_dir, "coverage.cobertura.xml");
        File.WriteAllText(coverage, """
            <coverage><sources><source>/elsewhere/repo</source></sources><packages><package><classes>
            <class filename="src/App.cs"><lines><line number="10" hits="2"/><line number="11" hits="0"/></lines></class>
            </classes></package></packages></coverage>
            """);
        var json = Path.Combine(_dir, "out.json");
        Assert.Equal(Program.ExitOk, await Analyze("--json", json, "--coverage", coverage));

        var report = JsonSerializer.Deserialize<BlastRadiusReport>(File.ReadAllText(json), Program.Json)!;
        Assert.NotNull(report.Coverage);
        Assert.Equal(new[] { coverage }, report.Coverage.Files);
        Assert.Equal((3, 1, 1, 1), (report.Coverage.CallSites, report.Coverage.Covered, report.Coverage.NotCovered, report.Coverage.Unknown));
        Assert.Equal(new int?[] { 2, 0, null }, report.Packages[0].Used.Select(u => u.Locations[0].TestHits));
        Assert.Contains("| Run by tests |", File.ReadAllText(Path.Combine(_dir, "out.md")));
    }

    [Fact]
    public async Task Without_coverage_the_report_has_none()
    {
        var json = Path.Combine(_dir, "out.json");
        Assert.Equal(Program.ExitOk, await Analyze("--json", json));
        var text = File.ReadAllText(json);
        Assert.DoesNotContain("coverage", text);
        Assert.DoesNotContain("testHits", text);
    }

    [Theory]
    [InlineData("<coverage>")]
    [InlineData(null)]
    public async Task Bad_coverage_file_is_a_usage_error(string? content)
    {
        var path = Path.Combine(_dir, "bad.xml");
        if (content != null) File.WriteAllText(path, content);
        Assert.Equal(Program.ExitError, await Analyze("--coverage", path));
    }

    [Fact]
    public async Task Missing_input_file_option_is_a_usage_error() =>
        Assert.Equal(Program.ExitError, await Program.Main(new[] { "analyze", "--old-api", _old, "--usages", _usages }));

    [Fact]
    public async Task Unknown_command_is_a_usage_error() =>
        Assert.Equal(Program.ExitError, await Program.Main(new[] { "frobnicate" }));

    [Fact]
    public async Task Help_succeeds_but_no_command_fails()
    {
        Assert.Equal(Program.ExitOk, await Program.Main(new[] { "help" }));
        Assert.Equal(Program.ExitOk, await Program.Main(new[] { "analyze", "--help" }));
        Assert.Equal(Program.ExitError, await Program.Main(Array.Empty<string>()));
    }
}

public class ArgsTests
{
    static readonly HashSet<string> Flags = new() { "help" };

    [Fact]
    public void Parses_command_values_and_flags()
    {
        var a = new Args(new[] { "analyze", "--from", "1.0", "--to=2.0", "--help" }, Flags);
        Assert.Equal("analyze", a.Command);
        Assert.Equal("1.0", a.Get("from"));
        Assert.Equal("2.0", a.Get("TO"));
        Assert.True(a.Flag("help"));
        Assert.False(a.Flag("verbose"));
        Assert.Null(a.Get("missing"));
    }

    [Fact]
    public void Repeated_option_collects_and_get_returns_the_last()
    {
        var a = new Args(new[] { "x", "--consumer", "a", "--consumer", "b" }, Flags);
        Assert.Equal(new[] { "a", "b" }, a.All("consumer"));
        Assert.Equal("b", a.Get("consumer"));
        Assert.Empty(a.All("other"));
    }

    [Fact]
    public void No_command_when_first_argument_is_an_option()
    {
        var a = new Args(new[] { "--from", "1" }, Flags);
        Assert.Null(a.Command);
        Assert.Equal("1", a.Get("from"));
    }

    [Fact]
    public void Flag_with_explicit_value_is_a_value()
    {
        var a = new Args(new[] { "x", "--help=no" }, Flags);
        Assert.False(a.Flag("help"));
        Assert.Equal("no", a.Get("help"));
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<UsageException>(() => new Args(new[] { "x", "stray" }, Flags));
        Assert.Throws<UsageException>(() => new Args(new[] { "x", "--from" }, Flags));
        Assert.Equal("--to is required.", Assert.Throws<UsageException>(() => new Args(new[] { "x" }, Flags).Require("to")).Message);
    }

    [Fact]
    public void Unused_options_are_reported()
    {
        var a = new Args(new[] { "x", "--from", "1", "--oops", "2", "--help" }, Flags);
        a.Get("from");
        var e = Assert.Throws<UsageException>(a.EnsureAllUsed);
        Assert.Equal("Unknown option(s): --oops, --help", e.Message);
        a.Get("oops"); a.Flag("help");
        a.EnsureAllUsed();
    }
}
