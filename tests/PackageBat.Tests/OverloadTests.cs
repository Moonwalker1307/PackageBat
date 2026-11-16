using PackageBat.Analysis;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// A new overload breaks source without changing the member the consumer's
/// binary binds to. PackageBat can't tell which calls go ambiguous, but it can
/// point at every call to a method that gained one.
/// </summary>
public sealed class OverloadTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "packagebat-tests", Guid.NewGuid().ToString("N"));

    const string V1 = """
        namespace Acme
        {
            public class Client
            {
                public void Send(string body) { }
                public void Close() { }
            }
        }
        """;

    const string V2 = """
        namespace Acme
        {
            public class Client
            {
                public void Send(string body) { }
                public void Send(System.Uri target) { }
                public void Close() { }
                public void Close(bool force) { }
            }
        }
        """;

    const string App = """
        namespace App
        {
            public static class Program
            {
                public static void Run(Acme.Client c)
                {
                    c.Send(null);
                    c.Close();
                }
            }
        }
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Warns_about_the_call_that_becomes_ambiguous_and_nothing_else()
    {
        var v1 = Compiler.Compile(Path.Combine(_root, "v1"), "Acme.Lib", "1.0.0.0", new[] { ("Lib.cs", V1) });
        var v2 = Compiler.Compile(Path.Combine(_root, "v2"), "Acme.Lib", "2.0.0.0", new[] { ("Lib.cs", V2) });
        var app = Path.Combine(_root, "repo", "src", "App.cs");
        Compiler.Compile(Path.Combine(_root, "repo", "bin"), "Consumer", "1.0.0.0", new[] { (app, App) }, v1);

        // The compiler agrees: Send(null) no longer compiles against v2.
        var error = Assert.Throws<InvalidOperationException>(() => Compiler.Compile(Path.Combine(_root, "after"), "Consumer", "1.0.0.0", new[] { (app, App) }, v2));
        Assert.Contains("CS0121", error.Message);

        var oldApi = ApiExtractor.Extract(new[] { v1 }, "Acme.Lib", "1.0.0", "net8.0");
        var newApi = ApiExtractor.Extract(new[] { v2 }, "Acme.Lib", "2.0.0", "net8.0");
        var report = UsageMatcher.Analyze(oldApi, newApi, UsageScanner.Scan(new[] { Path.Combine(_root, "repo", "bin") }, oldApi, Path.Combine(_root, "repo"), TextWriter.Null));

        // Close(bool) needs an argument, so Close() can't be affected.
        var used = Assert.Single(report.Used);
        Assert.Equal((ChangeKind.OverloadAdded, Severity.Warning, "Client.Send(string)"), (used.Change.Kind, used.Change.Severity, used.Change.Subject));
        Assert.Equal(("src/App.cs", 7), (used.Locations[0].File, used.Locations[0].Line));
        Assert.Contains("Client.Send(Uri)", used.Change.Suggestions);

        var markdown = MarkdownReport.Render(new BlastRadiusReport { Packages = { report } }, null);
        Assert.Contains("1 with new overloads to recheck", markdown);
        Assert.Contains("New: Client.Send(Uri)", markdown);
    }
}
