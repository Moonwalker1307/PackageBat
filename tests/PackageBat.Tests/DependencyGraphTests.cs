using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// End to end, offline: a local feed where a client package's new major
/// version drags in a new major of its core package (a transitive bump), and
/// that core package starts loading an optional companion assembly by name.
/// The consumer's build compiles and nothing references the companion, so only
/// these two checks can see the problem.
/// </summary>
public sealed class DependencyGraphTests : IDisposable
{
    // Unique ids, so the shared download cache in the temp folder can't serve another run's packages.
    readonly string _p = "PbT" + Guid.NewGuid().ToString("N")[..8];
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-graph-" + Guid.NewGuid().ToString("N"));
    readonly string _feed, _consumer;

    string Client => _p + ".Client";
    string Core => _p + ".Core";
    string Engine => _p + ".Engine";

    const string CoreV1 = """
        using System.Threading.Tasks;
        namespace NS.Core
        {
            public class Response { public Task<Response> EnsureAsync() => Task.FromResult(this); public string Body => ""; }
            public static class Builder
            {
                public static object Create() => new object();
                // Names a platform type by its qualified name: never reported.
                public static System.Type? Uri() => System.Type.GetType("System.Uri, System.Private.Uri");
            }
        }
        """;

    const string CoreV2 = """
        using System;
        using System.Threading.Tasks;
        namespace NS.Core
        {
            public class Response { public ValueTask<Response> EnsureAsync() => new(this); public string Body => ""; }
            public static class Builder
            {
                public static object Create()
                {
                    var t = Type.GetType("NS.Core.EngineFactory, ENGINE", throwOnError: false)
                        ?? throw new NotSupportedException("Add a reference to the ENGINE package.");
                    return Activator.CreateInstance(t)!;
                }
                public static System.Type? Uri() => System.Type.GetType("System.Uri, System.Private.Uri");
            }
        }
        """;

    const string ClientCode = """
        namespace NS.Client { public static class Factory { public static object Make() => NS.Core.Builder.Create(); } }
        """;

    // Line numbers matter: the assertions refer to them.
    const string App = """
        using System.Threading.Tasks;

        public class App
        {
            public async Task Run(NS.Core.Response r)
            {
                NS.Client.Factory.Make();
                await r.EnsureAsync();
            }
        }
        """;

    public DependencyGraphTests()
    {
        _feed = Path.Combine(_dir, "feed");
        _consumer = Path.Combine(_dir, "app");
        var build = Path.Combine(_dir, "build");

        var core1 = Compiler.Compile(Path.Combine(build, "core1"), Core, "1.0.0.0", new[] { ("Core.cs", CoreV1.Replace("ENGINE", Engine)) });
        var core2 = Compiler.Compile(Path.Combine(build, "core2"), Core, "2.0.0.0", new[] { ("Core.cs", CoreV2.Replace("ENGINE", Engine)) });
        var client1 = Compiler.Compile(Path.Combine(build, "client1"), Client, "1.0.0.0", new[] { ("Client.cs", ClientCode) }, core1);
        var client2 = Compiler.Compile(Path.Combine(build, "client2"), Client, "2.0.0.0", new[] { ("Client.cs", ClientCode) }, core2);
        var engine = Compiler.Compile(Path.Combine(build, "engine"), Engine, "2.0.0.0", new[] { ("Engine.cs", "namespace NS.Core { public sealed class EngineFactory { } }") });

        Publish(Core, "1.0.0", core1);
        Publish(Core, "2.0.0", core2);
        Publish(Client, "1.0.0", client1, (Core, "1.0.0"));
        Publish(Client, "2.0.0", client2, (Core, "[2.0.0, 3.0.0)"));
        Publish(Engine, "2.0.0", engine, (Core, "2.0.0"));

        Compiler.Compile(_consumer, "App", "1.0.0.0", new[] { (Path.Combine(_dir, "src", "App.cs"), App) }, client1, core1);
    }

    /// <summary>Writes a .nupkg (lib/net8.0 + nuspec) into a flat-container folder feed.</summary>
    void Publish(string id, string version, string dll, params (string Id, string Range)[] deps)
    {
        var lower = id.ToLowerInvariant();
        var dir = Path.Combine(_feed, lower, version);
        Directory.CreateDirectory(dir);
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id><version>{version}</version>
                <dependencies>
                  <group targetFramework=".NETFramework4.6.2"><dependency id="Unrelated.Legacy" version="1.0.0" /></group>
                  <group targetFramework="net8.0">{string.Concat(deps.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" exclude=\"Build,Analyzers\" />"))}</group>
                </dependencies>
              </metadata>
            </package>
            """;
        File.WriteAllText(Path.Combine(dir, lower + ".nuspec"), nuspec);
        using (var zip = ZipFile.Open(Path.Combine(dir, $"{lower}.{version}.nupkg"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(dll, $"lib/net8.0/{Path.GetFileName(dll)}");
            using var w = new StreamWriter(zip.CreateEntry(lower + ".nuspec").Open());
            w.Write(nuspec);
        }
        var index = Path.Combine(_feed, lower, "index.json");
        var versions = File.Exists(index) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(index))!["versions"] : new List<string>();
        versions.Add(version);
        File.WriteAllText(index, JsonSerializer.Serialize(new { versions }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    async Task<(int Exit, BlastRadiusReport Report, string Markdown)> Analyze(string? bumpsFile = null, params string[] extra)
    {
        var json = Path.Combine(_dir, "report.json");
        var md = Path.Combine(_dir, "report.md");
        var args = new List<string> { "analyze", "--consumer", _consumer, "--repo-root", _dir, "--source", _feed, "--json", json, "--markdown", md };
        if (bumpsFile != null) args.AddRange(new[] { "--bumps", bumpsFile });
        else args.AddRange(new[] { "--package", Client, "--from", "1.0.0", "--to", "2.0.0" });
        args.AddRange(extra);
        var exit = await Program.Main(args.ToArray());
        return (exit, JsonSerializer.Deserialize<BlastRadiusReport>(File.ReadAllText(json), Program.Json)!, File.ReadAllText(md));
    }

    [Fact]
    public async Task Follows_the_core_package_the_client_bump_drags_in()
    {
        var (_, report, _) = await Analyze();

        Assert.Equal(new[] { Client, Core }, report.Packages.Select(p => p.Package));
        var core = report.Packages[1];
        Assert.Equal(Client, core.Via);
        Assert.Equal(("1.0.0", "2.0.0"), (core.FromVersion, core.ToVersion));
        var used = Assert.Single(core.Used, u => u.Change.Kind == ChangeKind.SignatureChanged);
        Assert.Equal("Response.EnsureAsync() : Task<Response>", used.Change.Subject);
        Assert.Equal(("src/App.cs", 8), (used.Locations.Single().File, used.Locations.Single().Line));
    }

    [Fact]
    public async Task Warns_about_the_assembly_loaded_by_name_and_suggests_its_package()
    {
        var (exit, report, md) = await Analyze(null, "--fail-on", "warning");

        Assert.Equal(Program.ExitThreshold, exit);
        var warning = Assert.Single(report.Packages.SelectMany(p => p.Used), u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing).Change;
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(Engine, warning.Subject);
        Assert.Equal("NS.Core.EngineFactory", warning.TypeName);
        Assert.Equal($"{Core}.dll passes \"NS.Core.EngineFactory, {Engine}\" to Type.GetType", warning.Description);
        Assert.Equal(new[] { $"{Engine} 2.0.0" }, warning.Suggestions);
        Assert.Contains($"⚠️ **{Core} 2.0.0 loads `{Engine}` by name at run time, but nothing in your dependency graph provides it.** If your code uses this feature, add the `{Engine}` package (2.0.0).", md);
        Assert.Contains($"### 🦇 {Core} 1.0.0 → 2.0.0 (via {Client})", md);
        Assert.Contains($"⚠️ **Its new version also updates {Core} 1.0.0 → 2.0.0: see below.**", md);
        // The platform type named in both versions is not reported.
        Assert.DoesNotContain("System.Private.Uri", md);
    }

    [Fact]
    public async Task Breaking_alone_is_still_breaking_and_warnings_dont_count_below_their_level()
    {
        var (exit, report, _) = await Analyze(null, "--fail-on", "breaking");
        Assert.Equal(Program.ExitThreshold, exit);
        Assert.Equal(1, report.Count(Severity.Breaking));
        Assert.Equal(1, report.Count(Severity.Warning));
    }

    [Fact]
    public async Task Adding_the_companion_package_in_the_same_change_clears_the_warning()
    {
        var bumps = Path.Combine(_dir, "bumps.json");
        File.WriteAllText(bumps, JsonSerializer.Serialize(new[]
        {
            new PackageBump { Ecosystem = "dotnet", Package = Client, From = "1.0.0", To = "2.0.0" },
            new PackageBump { Ecosystem = "dotnet", Package = Engine, From = "", To = "2.0.0", Added = true },
        }, Program.Json));

        var (_, report, md) = await Analyze(bumps);

        Assert.DoesNotContain(report.Packages.SelectMany(p => p.Used), u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing);
        Assert.DoesNotContain(report.Packages, p => p.Package == Engine); // added, not bumped: nothing to compare
        Assert.Contains(report.Packages, p => p.Package == Core && p.Via == Client); // the API change is still there
        Assert.DoesNotContain("by name at run time", md);
    }

    [Fact]
    public async Task Companion_already_in_the_build_output_is_not_reported()
    {
        File.Copy(Path.Combine(_dir, "build", "engine", Engine + ".dll"), Path.Combine(_consumer, Engine + ".dll"));
        var (_, report, _) = await Analyze();
        Assert.DoesNotContain(report.Packages.SelectMany(p => p.Used), u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing);
    }

    [Fact]
    public async Task Companion_the_consumer_already_restored_is_not_reported()
    {
        var obj = Path.Combine(_dir, "app", "obj");
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "project.assets.json"),
            $$"""{ "version": 3, "libraries": { "{{Engine}}/2.0.0": { "type": "package" }, "{{Core}}/1.0.0": { "type": "package" } } }""");
        var (_, report, _) = await Analyze();
        Assert.DoesNotContain(report.Packages.SelectMany(p => p.Used), u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing);
    }

    [Fact]
    public async Task Restored_version_already_at_the_new_minimum_is_not_a_bump()
    {
        var obj = Path.Combine(_dir, "app", "obj");
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), $$"""{ "libraries": { "{{Core}}/2.0.0": { "type": "package" } } }""");
        var (_, report, _) = await Analyze();
        Assert.Equal(new[] { Client }, report.Packages.Select(p => p.Package));
    }

    [Fact]
    public async Task Transitive_bump_the_code_never_references_is_listed_but_not_reported()
    {
        // A consumer that only calls the client: the core package changes under it, unseen,
        // and the run-time check still runs on it (the core is what loads the companion).
        Directory.Delete(_consumer, true);
        Compiler.Compile(_consumer, "App", "1.0.0.0", new[] { (Path.Combine(_dir, "src", "App.cs"), "public class App { public void Run() => NS.Client.Factory.Make(); }") },
            Path.Combine(_dir, "build", "client1", Client + ".dll"));
        File.Copy(Path.Combine(_dir, "build", "engine", Engine + ".dll"), Path.Combine(_consumer, Engine + ".dll"));

        var (_, report, md) = await Analyze();

        Assert.Equal(new[] { Client }, report.Packages.Select(p => p.Package));
        var b = Assert.Single(report.Unreferenced);
        Assert.Equal((Core, "1.0.0", "2.0.0", Client), (b.Package, b.From, b.To, b.Via));
        Assert.Contains($"- {Core} 1.0.0 → 2.0.0 (via {Client}): your compiled code doesn't reference it", md);
    }

    [Fact]
    public async Task Detect_lists_added_packages_after_the_bumps()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        }
        void Props(params string[] items) => File.WriteAllText(Path.Combine(repo, "Directory.Packages.props"),
            $"<Project><ItemGroup>{string.Concat(items.Select(i => $"<PackageVersion {i} />"))}</ItemGroup></Project>");

        Git("init", "-q");
        Props("Include=\"A.Client\" Version=\"1.0.0\"", "Include=\"Moved\" Version=\"1.0.0\"");
        File.WriteAllText(Path.Combine(repo, "App.csproj"), "<Project><ItemGroup><PackageReference Include=\"Other\" Version=\"3.0.0\" /></ItemGroup></Project>");
        Git("add", "-A"); Git("commit", "-qm", "base");
        Props("Include=\"A.Client\" Version=\"2.0.0\"", "Include=\"A.Engine\" Version=\"2.0.0\"", "Include=\"Other\" Version=\"3.0.0\"");
        File.WriteAllText(Path.Combine(repo, "App.csproj"), "<Project><ItemGroup><PackageReference Include=\"Moved\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Git("commit", "-qam", "bump");

        var bumps = BumpDetector.Detect(repo, "HEAD~1", "HEAD");

        Assert.Equal(new[] { ("A.Client", "1.0.0", "2.0.0", (bool?)null), ("A.Engine", "", "2.0.0", true) },
            bumps.Select(b => (b.Package, b.From, b.To, b.Added)));
    }
}
