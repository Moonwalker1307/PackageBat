using System.Diagnostics;
using PackageBat.Ecosystems.DotNet;
using Xunit;

namespace PackageBat.Tests;

/// <summary>Versions set through MSBuild properties: <c>Version="$(FooVersion)"</c>.</summary>
public sealed class PropertyVersionTests : IDisposable
{
    readonly string _repo = Path.Combine(Path.GetTempPath(), "pb-props-" + Guid.NewGuid().ToString("N"));

    public PropertyVersionTests() => Directory.CreateDirectory(_repo);

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

    static string Properties(string body) => $"<Project><PropertyGroup>{body}</PropertyGroup></Project>";
    static string Project(string reference) => $"<Project><ItemGroup><PackageReference {reference} /></ItemGroup></Project>";

    void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-qm", message);
    }

    [Fact]
    public void A_bump_of_a_property_in_Directory_Build_props_bumps_the_package_that_uses_it()
    {
        Git("init", "-q");
        Write("Directory.Build.props", Properties("<FooVersion>1.0.0</FooVersion>"));
        Write("src/App/App.csproj", Project("Include=\"Foo\" Version=\"$(FooVersion)\""));
        Write("src/Lib/Lib.csproj", Project("Include=\"Bar\" Version=\"1.0.0\""));
        Commit("base");
        Write("Directory.Build.props", Properties("<FooVersion>2.0.0</FooVersion>"));
        Commit("bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("Foo", "1.0.0", "2.0.0", "src/App/App.csproj"), (bump.Package, bump.From, bump.To, bump.File));
        Assert.Null(bump.Elsewhere);
    }

    [Fact]
    public void Central_versions_can_use_properties_from_a_shared_versions_file()
    {
        Git("init", "-q");
        Write("eng/Versions.props", Properties("<FooMajor>1</FooMajor><FooVersion>$(FooMajor).4.0</FooVersion>"));
        Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Foo\" Version=\"$(FooVersion)\" /></ItemGroup></Project>");
        Write("App.csproj", Project("Include=\"Foo\""));
        Commit("base");
        Write("eng/Versions.props", Properties("<FooMajor>2</FooMajor><FooVersion>$(FooMajor).4.0</FooVersion>"));
        Commit("bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("Foo", "1.4.0", "2.4.0", "Directory.Packages.props"), (bump.Package, bump.From, bump.To, bump.File));
    }

    [Fact]
    public void A_project_sees_the_nearest_definition_and_the_others_are_listed_elsewhere()
    {
        Git("init", "-q");
        Write("Directory.Build.props", Properties("<FooVersion>1.0.0</FooVersion>"));
        Write("legacy/Directory.Build.props", Properties("<FooVersion>0.9.0</FooVersion>"));
        Write("src/App.csproj", Project("Include=\"Foo\" Version=\"$(FooVersion)\""));
        Write("legacy/Old.csproj", Project("Include=\"Foo\" Version=\"$(FooVersion)\""));
        Commit("base");
        Write("Directory.Build.props", Properties("<FooVersion>2.0.0</FooVersion>"));
        Commit("bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("1.0.0", "2.0.0", "src/App.csproj"), (bump.From, bump.To, bump.File));
        Assert.Equal(("legacy/Old.csproj", "0.9.0"), Assert.Single(bump.Elsewhere!) is var e ? (e.File, e.Version) : default);
    }

    [Fact]
    public void A_property_set_in_the_project_itself_wins()
    {
        Git("init", "-q");
        Write("Directory.Build.props", Properties("<FooVersion>1.0.0</FooVersion>"));
        Write("App.csproj", "<Project><PropertyGroup><FooVersion>1.5.0</FooVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Foo\" Version=\"$(FooVersion)\" /></ItemGroup></Project>");
        Commit("base");
        Write("App.csproj", "<Project><PropertyGroup><FooVersion>1.6.0</FooVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Foo\" Version=\"$(FooVersion)\" /></ItemGroup></Project>");
        Commit("bump");

        var bump = Assert.Single(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));

        Assert.Equal(("1.5.0", "1.6.0"), (bump.From, bump.To));
    }

    [Fact]
    public void Unrelated_property_changes_bump_nothing()
    {
        Git("init", "-q");
        Write("Directory.Build.props", Properties("<FooVersion>1.0.0</FooVersion><LangVersion>12</LangVersion>"));
        Write("App.csproj", Project("Include=\"Foo\" Version=\"$(FooVersion)\""));
        Commit("base");
        Write("Directory.Build.props", Properties("<FooVersion>1.0.0</FooVersion><LangVersion>13</LangVersion>"));
        Commit("unrelated");

        Assert.Empty(BumpDetector.Detect(_repo, "HEAD~1", "HEAD"));
    }

    [Fact]
    public void Reads_defaults_and_skips_build_dependent_conditions()
    {
        var props = BumpDetector.ReadProperties("""
            <Project>
              <PropertyGroup>
                <A>1.0.0</A>
                <B Condition="'$(B)' == ''">2.0.0</B>
                <C Condition="'$(Configuration)' == 'Debug'">3.0.0</C>
              </PropertyGroup>
              <PropertyGroup Condition="'$(TargetFramework)' == 'net8.0'">
                <D>4.0.0</D>
              </PropertyGroup>
            </Project>
            """);

        Assert.Equal("1.0.0", props["A"]);
        Assert.Equal("2.0.0", props["B"]);
        Assert.False(props.ContainsKey("C"));
        Assert.False(props.ContainsKey("D"));
    }

    [Theory]
    [InlineData("$(A)", "1.2.3")]
    [InlineData("$(Major).$(Minor).0", "4.5.0")]
    [InlineData("$(Nested)", "4.5.0")]
    [InlineData("$(Missing)", null)]
    [InlineData("$(Loop)", null)]
    [InlineData("$([System.String]::Copy('1.0'))", "$([System.String]::Copy('1.0'))")] // property functions are left alone, so the version is skipped later
    public void Expands_properties(string value, string? expected)
    {
        var props = new Dictionary<string, string> { ["A"] = "1.2.3", ["Major"] = "4", ["Minor"] = "5", ["Nested"] = "$(Major).$(Minor).0", ["Loop"] = "$(Loop)" };
        Assert.Equal(expected, BumpDetector.Expand(value, props.GetValueOrDefault));
    }
}
