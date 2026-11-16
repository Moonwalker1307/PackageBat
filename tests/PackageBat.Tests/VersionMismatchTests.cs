using System.Diagnostics;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>A change that bumps a package in some project files and leaves others on another version.</summary>
public sealed class VersionMismatchTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "pb-mismatch-" + Guid.NewGuid().ToString("N"));

    public VersionMismatchTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
    }

    void Write(string path, string xml)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, xml);
    }

    static string Project(params string[] references) =>
        $"<Project><ItemGroup>{string.Concat(references.Select(r => $"<PackageReference {r} />"))}</ItemGroup></Project>";

    [Fact]
    public void Lists_project_files_the_change_left_on_another_version()
    {
        Git("init", "-q");
        Write("src/App/App.csproj", Project("Include=\"Foo\" Version=\"1.0.0\""));
        Write("src/Worker/Worker.csproj", Project("Include=\"Foo\" Version=\"1.0.0\"", "Include=\"Bar\" Version=\"1.0.0\""));
        Write("tools/Tools.csproj", Project("Include=\"Foo\" Version=\"1.5.0\""));
        Write("src/Other/Other.csproj", Project("Include=\"Bar\" Version=\"1.0.0\""));
        Git("add", "-A"); Git("commit", "-qm", "base");
        Write("src/App/App.csproj", Project("Include=\"Foo\" Version=\"2.0.0\""));
        Git("commit", "-qam", "bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("Foo", "1.0.0", "2.0.0", "src/App/App.csproj"), (bump.Package, bump.From, bump.To, bump.File));
        Assert.Equal(new[] { ("src/Worker/Worker.csproj", "1.0.0"), ("tools/Tools.csproj", "1.5.0") }, bump.Elsewhere!.Select(e => (e.File, e.Version)));
    }

    [Fact]
    public void Finds_a_version_override_left_behind_under_central_package_management()
    {
        Git("init", "-q");
        Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Foo\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Write("App.csproj", Project("Include=\"Foo\""));
        Write("Legacy.csproj", Project("Include=\"Foo\" VersionOverride=\"1.0.0\""));
        Git("add", "-A"); Git("commit", "-qm", "base");
        Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Foo\" Version=\"2.0.0\" /></ItemGroup></Project>");
        Git("commit", "-qam", "bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("Legacy.csproj", "1.0.0"), Assert.Single(bump.Elsewhere!) is var e ? (e.File, e.Version) : default);
    }

    [Fact]
    public void A_bump_every_file_agrees_on_has_nothing_elsewhere()
    {
        Git("init", "-q");
        Write("A.csproj", Project("Include=\"Foo\" Version=\"1.0.0\""));
        Write("B.csproj", Project("Include=\"Foo\" Version=\"1.0.0\""));
        Git("add", "-A"); Git("commit", "-qm", "base");
        Write("A.csproj", Project("Include=\"Foo\" Version=\"2.0.0\""));
        Write("B.csproj", Project("Include=\"Foo\" Version=\"2.0.0\""));
        Git("commit", "-qam", "bump");

        Assert.Null(Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD")).Elsewhere);
    }

    [Fact]
    public void Two_files_bumped_to_different_versions_report_the_other_one()
    {
        Git("init", "-q");
        Write("A.csproj", Project("Include=\"Foo\" Version=\"1.0.0\""));
        Write("B.csproj", Project("Include=\"Foo\" Version=\"1.0.0\""));
        Git("add", "-A"); Git("commit", "-qm", "base");
        Write("A.csproj", Project("Include=\"Foo\" Version=\"2.0.0\""));
        Write("B.csproj", Project("Include=\"Foo\" Version=\"1.1.0\""));
        Git("commit", "-qam", "bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));
        Assert.Equal("2.0.0", bump.To);
        Assert.Equal(("B.csproj", "1.1.0"), Assert.Single(bump.Elsewhere!) is var e ? (e.File, e.Version) : default);
    }

    [Fact]
    public void Becomes_a_warning_with_the_files_and_their_versions()
    {
        var c = Program.VersionMismatch(new PackageBump
        {
            Package = "Foo", From = "1.0.0", To = "2.0.0",
            Elsewhere = new() { new() { File = "src/Worker/Worker.csproj", Version = "1.0.0" }, new() { File = "tools/Tools.csproj", Version = "1.5.0" } },
        })!;

        Assert.Equal((ChangeKind.VersionMismatch, Severity.Warning, "version-mismatch:Foo"), (c.Kind, c.Severity, c.Id));
        Assert.Equal("src/Worker/Worker.csproj, tools/Tools.csproj", c.Subject);
        Assert.Equal("2 other files still set Foo to a version other than 2.0.0: src/Worker/Worker.csproj (1.0.0), tools/Tools.csproj (1.5.0)", c.Description);
        Assert.Null(Program.VersionMismatch(new PackageBump { Package = "Foo", From = "1.0.0", To = "2.0.0" }));
        Assert.Null(Program.VersionMismatch(new PackageBump { Package = "Foo", From = "1.0.0", To = "2.0.0", Elsewhere = new() }));
    }

    [Fact]
    public void Is_shown_above_the_api_part_of_the_report()
    {
        var change = Program.VersionMismatch(new PackageBump { Package = "Foo", From = "1.0.0", To = "2.0.0", Elsewhere = new() { new() { File = "B.csproj", Version = "1.0.0" } } })!;
        var p = new PackageReport { Package = "Foo", FromVersion = "1.0.0", ToVersion = "2.0.0", ReferenceCount = 2, UsedApiCount = 1 };
        p.Used.Add(new UsedChange { Change = change });
        p.AllChanges.Add(change);

        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { p } }, null);

        Assert.Contains("⚠️ **Not every project moves to 2.0.0:** 1 other file still sets Foo to a version other than 2.0.0: B.csproj (1.0.0).", md);
        Assert.Contains("The API changes don't touch any of the 1 API your code uses (2 references).", md);
        Assert.DoesNotContain("Looks safe", md);
        Assert.DoesNotContain("Other API changes", md);
    }

    [Fact]
    public void Bumps_json_checks_elsewhere_entries()
    {
        Assert.Null(Validation.Check(new List<PackageBump> { new() { Package = "Foo", From = "1", To = "2", Elsewhere = new() { new() { File = "a", Version = "1" } } } }));
        Assert.NotNull(Validation.Check(new List<PackageBump> { new() { Package = "Foo", From = "1", To = "2", Elsewhere = new() { null! } } }));
        Assert.NotNull(Validation.Check(new List<PackageBump> { new() { Package = "Foo", From = "1", To = "2", Elsewhere = new() { new() { File = "", Version = "1" } } } }));
    }
}
