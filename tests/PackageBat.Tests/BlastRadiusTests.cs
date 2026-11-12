using System.Text.Json;
using PackageBat.Analysis;
using PackageBat.Ecosystems;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// End to end: compile two versions of a library and a consumer of v1, then
/// check that PackageBat reports exactly the changes the consumer would hit.
/// </summary>
public sealed class BlastRadiusTests : IClassFixture<BlastRadiusTests.Fixture>
{
    const string V1 = """
        using System;
        namespace Acme
        {
            public class Client
            {
                public Client(string url) { }
                public string Get(string path) => path;
                public void Send(string body) { }
                public int Timeout { get; set; }
                public void Unused() { }
                public void Retry(int times) { }
                public virtual void OnError(Exception e) { }
            }
            public interface IHandler { void Handle(string message); }
            public class Legacy { public static void Run() { } }
            public abstract class Base { }
            public class Derived : Base { public void Shared() { } }
            public class Widget { }
            public static class ClientExtensions { public static void Log(this Client c, string m) { } }
            public enum Mode { A, B }
        }
        """;

    const string V2 = """
        using System;
        using System.Threading;
        namespace Acme
        {
            public class Client
            {
                public Client(string url) { }
                public string Get(string path, CancellationToken ct = default) => path;
                public void Send(byte[] body) { }
                public TimeSpan Timeout { get; set; }
                [Obsolete("Use a retry policy")] public void Retry(int times) { }
            }
            public interface IHandler { void Handle(string message); void Close(); }
            public abstract class Base { public void Shared() { } }
            public class Derived : Base { }
            public sealed class Widget { }
            public static class ClientExtensions { public static void Log(this Client c, string m) { } }
            public enum Mode { A, B, C }
        }
        namespace Acme.Old
        {
            public class Legacy { public static void Run() { } }
        }
        """;

    // Line numbers matter: the assertions below refer to them.
    const string App = """
        using System;
        using Acme;

        public class App
        {
            public void Run()
            {
                var c = new Client("x");
                c.Get("/");
                c.Send("hi");
                c.Timeout = 5;
                c.Retry(3);
                Legacy.Run();
                new Derived().Shared();
                c.Log("m");
                var m = Mode.B;
            }
        }
        """;

    const string Types = """
        using System;
        using Acme;

        public class MyHandler : IHandler
        {
            public void Handle(string message) { }
        }

        public class MyClient : Client
        {
            public MyClient() : base("x") { }
            public override void OnError(Exception e) { }
        }

        public class MyWidget : Widget { }
        """;

    public sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packagebat-tests", Guid.NewGuid().ToString("N"));
        public ApiSurface OldApi { get; }
        public ApiSurface NewApi { get; }
        public UsageSet Usages { get; }
        public PackageReport Report { get; }

        public Fixture()
        {
            var v1 = Compiler.Compile(Path.Combine(Root, "v1"), "Acme.Lib", "1.0.0.0", new[] { ("Lib.cs", V1) });
            var v2 = Compiler.Compile(Path.Combine(Root, "v2"), "Acme.Lib", "2.0.0.0", new[] { ("Lib.cs", V2) });
            Compiler.Compile(Path.Combine(Root, "repo", "bin"), "Consumer", "1.0.0.0",
                new[] { (Path.Combine(Root, "repo", "src", "App.cs"), App), (Path.Combine(Root, "repo", "src", "Types.cs"), Types) }, v1);
            // The package's own dll sits next to the consumer in bin/, like a real build; it must be skipped.
            File.Copy(v1, Path.Combine(Root, "repo", "bin", "Acme.Lib.dll"));

            OldApi = ApiExtractor.Extract(new[] { v1 }, "Acme.Lib", "1.0.0", "net8.0");
            NewApi = ApiExtractor.Extract(new[] { v2 }, "Acme.Lib", "2.0.0", "net8.0");
            Usages = UsageScanner.Scan(new[] { Path.Combine(Root, "repo", "bin") }, OldApi, Path.Combine(Root, "repo"), TextWriter.Null);
            Report = UsageMatcher.Analyze(OldApi, NewApi, Usages);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    readonly Fixture _f;
    public BlastRadiusTests(Fixture f) => _f = f;

    UsedChange Used(string idPrefix) =>
        _f.Report.Used.SingleOrDefault(u => u.Change.Id.StartsWith(idPrefix, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"No used change starting with '{idPrefix}'. Got:\n{string.Join("\n", _f.Report.Used.Select(u => u.Change.Id))}");

    static string Where(UsedChange u) => string.Join(",", u.Locations.Select(l => $"{l.File}:{l.Line}"));

    [Fact]
    public void Scans_only_the_consumer_not_the_package_copy()
    {
        Assert.Single(_f.Usages.Scanned);
        Assert.EndsWith("Consumer.dll", _f.Usages.Scanned[0]);
    }

    [Fact]
    public void Changed_parameter_type_is_breaking_with_line()
    {
        var u = Used("signature-changed:Acme.Client::Send(");
        Assert.Equal(Severity.Breaking, u.Change.Severity);
        Assert.Equal("src/App.cs:10", Where(u));
        Assert.Contains("Client.Send(byte[])", u.Change.Suggestions);
    }

    [Fact]
    public void Added_optional_parameter_is_binary_only()
    {
        var u = Used("signature-changed:Acme.Client::Get(");
        Assert.Equal(Severity.BinaryOnly, u.Change.Severity);
        Assert.Equal("src/App.cs:9", Where(u));
    }

    [Fact]
    public void Property_type_change_is_reported_on_the_setter_used()
    {
        var u = Used("signature-changed:Acme.Client::set_Timeout(");
        Assert.Equal(Severity.Breaking, u.Change.Severity);
        Assert.Equal("src/App.cs:11", Where(u));
        Assert.DoesNotContain(_f.Report.Used, x => x.Change.Id.Contains("get_Timeout")); // the getter isn't used
    }

    [Fact]
    public void Newly_obsolete_member_is_a_warning()
    {
        var u = Used("member-deprecated:Acme.Client::Retry(");
        Assert.Equal(Severity.Warning, u.Change.Severity);
        Assert.Contains("Use a retry policy", u.Change.Description);
        Assert.Equal("src/App.cs:12", Where(u));
    }

    [Fact]
    public void Moved_type_is_reported_with_its_new_home()
    {
        var u = Used("type-removed:Acme.Legacy");
        Assert.Equal("src/App.cs:13", Where(u));
        Assert.Contains("Acme.Old.Legacy", u.Change.Suggestions);
        Assert.Contains("Run", u.MembersUsed);
    }

    [Fact]
    public void Interface_gaining_a_member_breaks_implementers()
    {
        var u = Used("abstract-added:Acme.IHandler::Close(");
        Assert.Equal(Severity.Breaking, u.Change.Severity);
        Assert.Equal("src/Types.cs", u.Locations.Single().File);
    }

    [Fact]
    public void Removed_virtual_breaks_overrides()
    {
        var u = Used("member-removed:Acme.Client::OnError(");
        Assert.Equal("src/Types.cs:12", Where(u));
    }

    [Fact]
    public void Sealed_type_breaks_subclasses()
    {
        var u = Used("type-sealed:Acme.Widget");
        Assert.Equal("src/Types.cs", u.Locations.Single().File);
    }

    [Fact]
    public void Unchanged_and_moved_to_base_members_are_not_reported()
    {
        Assert.DoesNotContain(_f.Report.Used, u => u.Change.Id.Contains("Shared"));
        Assert.DoesNotContain(_f.Report.Used, u => u.Change.Id.Contains("ClientExtensions"));
        Assert.DoesNotContain(_f.Report.Used, u => u.Change.Id.Contains("Mode"));
        Assert.DoesNotContain(_f.Report.Used, u => u.Change.Id.Contains("::.ctor"));
    }

    [Fact]
    public void Unused_removals_are_listed_but_not_counted_as_used()
    {
        Assert.Contains(_f.Report.AllChanges, c => c.Id.StartsWith("member-removed:Acme.Client::Unused("));
        Assert.DoesNotContain(_f.Report.Used, u => u.Change.Id.Contains("Unused"));
    }

    [Fact]
    public void Exactly_the_expected_changes_are_used()
    {
        Assert.Equal(8, _f.Report.Used.Count);
        Assert.Equal(6, _f.Report.Used.Count(u => u.Change.Severity == Severity.Breaking));
    }

    [Fact]
    public void Language_neutral_json_round_trip_gives_the_same_report()
    {
        string Rt<T>(T value) => JsonSerializer.Serialize(value, Program.Json);
        T Back<T>(string json) => JsonSerializer.Deserialize<T>(json, Program.Json)!;

        var again = UsageMatcher.Analyze(Back<ApiSurface>(Rt(_f.OldApi)), Back<ApiSurface>(Rt(_f.NewApi)), Back<UsageSet>(Rt(_f.Usages)));
        Assert.Equal(Rt(_f.Report), Rt(again));
    }

    [Fact]
    public void Markdown_summarises_and_links()
    {
        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { _f.Report }, Scanned = { "Consumer.dll" } }, "https://github.com/o/r/blob/abc");
        Assert.Contains(MarkdownReport.Marker, md);
        Assert.Contains("This bump changes 8 APIs you use, in 2 files.", md);
        Assert.Contains("[src/App.cs:10](https://github.com/o/r/blob/abc/src/App.cs#L10)", md);
    }
}
