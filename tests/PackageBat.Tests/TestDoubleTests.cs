using PackageBat.Analysis;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// Affected call sites whose type the consumer's tests replace with a mock
/// (a mocking library's generic argument) or a hand-written fake (a test class
/// implementing the interface), so covered lines may never have run the package.
/// </summary>
public sealed class TestDoubleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pb-doubles-" + Guid.NewGuid().ToString("N"));

    const string V1 = """
        namespace Acme
        {
            public interface IClient { string Get(); }
            public class Client : IClient { public virtual string Get() => ""; }
            public static class Helper { public static void Run() { } }
            public interface IOther { void Ping(); }
        }
        """;

    const string V2 = """
        namespace Acme
        {
            public interface IClient { [System.Obsolete("Use GetAsync")] string Get(); }
            public class Client : IClient { [System.Obsolete("Use GetAsync")] public virtual string Get() => ""; }
            public static class Helper { [System.Obsolete("Gone soon")] public static void Run() { } }
            public interface IOther { [System.Obsolete("x")] void Ping(); }
        }
        """;

    // Line numbers matter: the assertions refer to them.
    const string App = """
        public class Service
        {
            readonly Acme.IClient _client;
            public Service(Acme.IClient client) => _client = client;
            public string Read() => _client.Get();
            public void Tick() => Acme.Helper.Run();
            public void Ping(Acme.IOther o) => o.Ping();
        }
        """;

    const string MoqLike = """
        namespace Moq
        {
            public class Mock<T> where T : class { public T Object => null!; public void Setup(System.Func<T, string> f) { } }
            public static class Mock { public static T Of<T>() where T : class => null!; }
        }
        """;

    const string XunitLike = "namespace Xunit { public sealed class FactAttribute : System.Attribute { } }";

    const string MockTests = """
        public class ServiceTests
        {
            [Xunit.Fact]
            public void Reads()
            {
                var mock = new Moq.Mock<Acme.IClient>();
                new Service(mock.Object).Read();
                new Service(Moq.Mock.Of<Acme.IClient>()).Tick();
            }
        }
        """;

    const string FakeTests = """
        public sealed class FakeOther : Acme.IOther
        {
            public void Ping() { }
        }
        """;

    readonly ApiSurface _old, _new;
    readonly string _bin, _lib1, _xunit;

    public TestDoubleTests()
    {
        _lib1 = Compiler.Compile(Path.Combine(_root, "v1"), "Acme", "1.0.0.0", new[] { ("Lib.cs", V1) });
        var lib2 = Compiler.Compile(Path.Combine(_root, "v2"), "Acme", "2.0.0.0", new[] { ("Lib.cs", V2) });
        _bin = Path.Combine(_root, "repo", "bin");
        var app = Compiler.Compile(Path.Combine(_bin, "app"), "App", "1.0.0.0", new[] { (Path.Combine(_root, "repo", "src", "Service.cs"), App) }, _lib1);
        var moq = Compiler.Compile(Path.Combine(_root, "libs"), "Moq", "4.0.0.0", new[] { ("Moq.cs", MoqLike) });
        _xunit = Compiler.Compile(Path.Combine(_root, "libs"), "xunit.core", "2.0.0.0", new[] { ("Xunit.cs", XunitLike) });
        Compiler.Compile(Path.Combine(_bin, "tests"), "App.Tests", "1.0.0.0", new[] { (Path.Combine(_root, "repo", "tests", "ServiceTests.cs"), MockTests) }, _lib1, app, moq, _xunit);
        _old = ApiExtractor.Extract(new[] { _lib1 }, "Acme", "1.0.0", "net8.0");
        _new = ApiExtractor.Extract(new[] { lib2 }, "Acme", "2.0.0", "net8.0");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    PackageReport Analyze(params string[] paths) =>
        UsageMatcher.Analyze(_old, _new, UsageScanner.Scan(paths.Length > 0 ? paths : new[] { _bin }, _old, Path.Combine(_root, "repo"), TextWriter.Null));

    UsedChange Change(PackageReport p, string subjectStart) => Assert.Single(p.Used, u => u.Change.Subject.StartsWith(subjectStart, StringComparison.Ordinal));

    [Fact]
    public void A_mocked_interface_is_attached_to_the_change_with_where_the_mock_is_made()
    {
        var get = Change(Analyze(), "IClient.Get()");

        Assert.Equal(("src/Service.cs", 5), (get.Locations.Single().File, get.Locations.Single().Line));
        // Mock<IClient> is used on lines 6, 7 and 8 of one test method: one double, where it's first made.
        var d = Assert.Single(get.TestDoubles);
        Assert.Equal(("Acme.IClient", "tests/ServiceTests.cs", 6), (d.TypeName, d.Location.File, d.Location.Line));
    }

    [Fact]
    public void Static_members_cant_be_mocked_and_get_no_double()
    {
        Assert.Empty(Change(Analyze(), "Helper.Run()").TestDoubles);
    }

    [Fact]
    public void Mocks_dont_count_as_references()
    {
        var withTests = Analyze();
        var appOnly = Analyze(Path.Combine(_bin, "app"));
        // The test assembly calls Service, not the package, apart from making the mocks.
        Assert.Equal(appOnly.ReferenceCount, withTests.ReferenceCount - CountTypeReferencesInTests());
        Assert.Empty(appOnly.Used.SelectMany(u => u.TestDoubles));
    }

    int CountTypeReferencesInTests() =>
        UsageScanner.Scan(new[] { Path.Combine(_bin, "tests") }, _old, Path.Combine(_root, "repo"), TextWriter.Null).Usages
            .Count(u => u.Kind != UsageKind.Mock && _old.FindType(u.TypeName) != null);

    [Fact]
    public void A_hand_written_fake_in_a_test_assembly_counts()
    {
        var fakes = Path.Combine(_bin, "fakes");
        Compiler.Compile(fakes, "App.Fakes", "1.0.0.0", new[] { (Path.Combine(_root, "repo", "tests", "FakeOther.cs"), FakeTests), ("Uses.cs", "public class UsesXunit { [Xunit.Fact] public void T() { } }") }, _lib1, _xunit);

        var ping = Change(Analyze(), "IOther.Ping()");

        var d = Assert.Single(ping.TestDoubles);
        Assert.Equal(("Acme.IOther", "tests/FakeOther.cs"), (d.TypeName, d.Location.File));
    }

    [Fact]
    public void Implementing_an_interface_outside_test_code_is_not_a_fake()
    {
        var other = Path.Combine(_bin, "prod");
        Compiler.Compile(other, "App.Impl", "1.0.0.0", new[] { (Path.Combine(_root, "repo", "src", "RealOther.cs"), FakeTests) }, _lib1);

        Assert.Empty(Change(Analyze(Path.Combine(_bin, "app"), other), "IOther.Ping()").TestDoubles);
    }

    [Fact]
    public void Coverage_report_says_the_covered_line_may_have_run_a_mock()
    {
        var p = Analyze();
        var report = new BlastRadiusReport { Packages = { p } };
        var cobertura = Path.Combine(_root, "coverage.xml");
        File.WriteAllText(cobertura, """
            <coverage><sources><source>/ci/repo/</source></sources><packages><package><classes>
            <class filename="src/Service.cs"><lines><line number="5" hits="2"/><line number="6" hits="1"/><line number="7" hits="0"/></lines></class>
            </classes></package></packages></coverage>
            """);
        LineCoverage.Read(new[] { cobertura }).Annotate(report);

        var md = MarkdownReport.Render(report, null);

        Assert.Contains("✅ 1 of 1<br>🎭 tests mock `IClient`: `tests/ServiceTests.cs:6` |", md);
        Assert.Contains("🎭 One changed API is on a type your tests replace with a mock or fake, so a passing test there may not have run Acme's own code.", md);
        Assert.DoesNotContain("🎭 tests mock `Helper`", md);
    }

    [Fact]
    public void Without_coverage_the_report_is_unchanged()
    {
        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { Analyze() } }, null);
        Assert.DoesNotContain("🎭", md);
    }
}
