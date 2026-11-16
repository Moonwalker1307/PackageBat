using System.IO.Compression;
using System.Text.Json;
using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>Reading and comparing the licence a .nuspec declares.</summary>
public sealed class NuspecLicenseTests
{
    static string Nuspec(string license) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>Foo</id><version>1.0.0</version>{license}</metadata>
        </package>
        """;

    static ApiChange? Compare(string oldLicense, string newLicense) =>
        NuspecLicense.Compare("Foo", NuspecLicense.Read(Nuspec(oldLicense)), NuspecLicense.Read(Nuspec(newLicense)));

    [Fact]
    public void Reads_expression_file_url_and_acceptance()
    {
        var expr = NuspecLicense.Read(Nuspec("""<license type="expression">MIT OR Apache-2.0</license><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>"""))!;
        Assert.Equal(("MIT OR Apache-2.0", null, null, false), (expr.Expression, expr.File, expr.Url, expr.RequireAcceptance));

        var file = NuspecLicense.Read(Nuspec("""<license type="file">docs\LICENSE.txt</license><requireLicenseAcceptance>true</requireLicenseAcceptance>"""))!;
        Assert.Equal(("docs/LICENSE.txt", true), (file.File, file.RequireAcceptance));

        Assert.Equal("https://example.com/eula", NuspecLicense.Read(Nuspec("<licenseUrl>https://example.com/eula</licenseUrl>"))!.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<package><metadata><license")]
    [InlineData("<package/>")]
    public void Unreadable_manifests_have_no_licence_to_compare(string xml)
    {
        Assert.Null(NuspecLicense.Read(xml));
        Assert.Null(NuspecLicense.Compare("Foo", NuspecLicense.Read(xml), NuspecLicense.Read(Nuspec("""<license type="expression">MIT</license>"""))));
    }

    [Fact]
    public void A_different_expression_is_a_warning()
    {
        var c = Compare("""<license type="expression">MIT</license>""", """<license type="expression">BUSL-1.1</license>""")!;
        Assert.Equal((ChangeKind.LicenseChanged, Severity.Warning), (c.Kind, c.Severity));
        Assert.Equal("MIT → BUSL-1.1", c.Subject);
        Assert.Equal("The licence changed from MIT to BUSL-1.1", c.Description);
        Assert.Equal("license:Foo", c.Id);
    }

    [Fact]
    public void Expression_to_file_is_a_change()
    {
        var c = Compare("""<license type="expression">MIT</license>""", """<license type="file">LICENSE.txt</license>""")!;
        Assert.Equal("MIT → the licence file LICENSE.txt", c.Subject);
    }

    [Theory]
    [InlineData("""<license type="expression">MIT</license>""", """<license type="expression">mit</license>""")]
    [InlineData("""<license type="expression">MIT OR  Apache-2.0</license>""", """<license type="expression">MIT OR Apache-2.0</license>""")]
    [InlineData("<licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>", """<license type="expression">MIT</license>""")]
    [InlineData("<licenseUrl>https://opensource.org/licenses/MIT</licenseUrl>", """<license type="expression">MIT</license><licenseUrl>https://aka.ms/deprecateLicenseUrl</licenseUrl>""")]
    [InlineData("<licenseUrl>https://example.com/eula</licenseUrl>", "<licenseUrl>https://example.com/eula/</licenseUrl>")]
    [InlineData("", "")]
    public void Same_licence_written_differently_is_no_change(string oldLicense, string newLicense) =>
        Assert.Null(Compare(oldLicense, newLicense));

    [Fact]
    public void A_url_that_doesnt_name_the_same_licence_is_a_change()
    {
        var c = Compare("<licenseUrl>https://github.com/foo/foo/blob/main/LICENSE</licenseUrl>", """<license type="expression">MIT</license>""")!;
        Assert.Equal("https://github.com/foo/foo/blob/main/LICENSE → MIT", c.Subject);
        Assert.NotNull(Compare("", """<license type="expression">MIT</license>"""));
    }

    [Fact]
    public void Licence_files_are_compared_by_text()
    {
        NuspecLicense File(string path, string? text) => NuspecLicense.Read(Nuspec($"""<license type="file">{path}</license>"""))! with { Text = text };

        Assert.Null(NuspecLicense.Compare("Foo", File("LICENSE.txt", "Free.\r\nForever.\r\n"), File("LICENSE.md", "Free.\nForever.")));
        var c = NuspecLicense.Compare("Foo", File("LICENSE.txt", "Free."), File("LICENSE.txt", "Pay us."))!;
        Assert.Equal("LICENSE.txt changed", c.Subject);
        Assert.Equal("The text of the licence file LICENSE.txt changed", c.Description);
        // Texts unknown: same path counts as unchanged, a new path is reported.
        Assert.Null(NuspecLicense.Compare("Foo", File("LICENSE.txt", null), File("LICENSE.txt", null)));
        Assert.Contains("moved from LICENSE.txt to EULA.txt", NuspecLicense.Compare("Foo", File("LICENSE.txt", null), File("EULA.txt", null))!.Description);
    }

    [Fact]
    public void Newly_requiring_acceptance_is_reported_with_the_licence()
    {
        var c = Compare("""<license type="expression">MIT</license>""", """<license type="expression">MIT</license><requireLicenseAcceptance>true</requireLicenseAcceptance>""")!;
        Assert.Equal("The package now requires accepting its licence on install", c.Description);
        Assert.Null(Compare("""<license type="expression">MIT</license><requireLicenseAcceptance>true</requireLicenseAcceptance>""", """<license type="expression">MIT</license>"""));
    }
}

/// <summary>
/// End to end, offline: a local feed where a library and a dependency it
/// updates change their licences between versions.
/// </summary>
public sealed class LicenseChangeTests : IDisposable
{
    readonly string _p = "PbL" + Guid.NewGuid().ToString("N")[..8];
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-license-" + Guid.NewGuid().ToString("N"));
    readonly string _feed, _consumer, _build;
    readonly string _lib1;

    string Lib => _p + ".Lib";
    string Dep => _p + ".Dep";

    const string MitLicense = """<license type="expression">MIT</license>""";
    const string FileLicense = """<license type="file">LICENSE.txt</license>""";

    public LicenseChangeTests()
    {
        _feed = Path.Combine(_dir, "feed");
        _consumer = Path.Combine(_dir, "app");
        _build = Path.Combine(_dir, "build");
        _lib1 = Compiler.Compile(Path.Combine(_build, "lib1"), Lib, "1.0.0.0", new[] { ("Lib.cs", "namespace NS { public static class Api { public static int Get() => 1; } }") });
        Compiler.Compile(_consumer, "App", "1.0.0.0", new[] { (Path.Combine(_dir, "src", "App.cs"), "public static class App { public static int Run() => NS.Api.Get(); }") }, _lib1);
    }

    /// <summary>Writes a .nupkg (lib/net8.0 + nuspec + optional LICENSE.txt) into a flat-container folder feed.</summary>
    void Publish(string id, string version, string license, string? licenseText = null, params (string Id, string Range)[] deps)
    {
        var ns = id == Lib ? "NS" : "Other";
        var dll = Compiler.Compile(Path.Combine(_build, id + version), id, version + ".0", new[] { ("Lib.cs", $"namespace {ns} {{ public static class Api {{ public static int Get() => 1; }} }}") });
        var lower = id.ToLowerInvariant();
        var dir = Path.Combine(_feed, lower, version);
        Directory.CreateDirectory(dir);
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id><version>{version}</version>{license}
                <dependencies><group targetFramework="net8.0">{string.Concat(deps.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />"))}</group></dependencies>
              </metadata>
            </package>
            """;
        File.WriteAllText(Path.Combine(dir, lower + ".nuspec"), nuspec);
        using (var zip = ZipFile.Open(Path.Combine(dir, $"{lower}.{version}.nupkg"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(dll, $"lib/net8.0/{Path.GetFileName(dll)}");
            using (var w = new StreamWriter(zip.CreateEntry(lower + ".nuspec").Open())) w.Write(nuspec);
            if (licenseText != null)
                using (var w = new StreamWriter(zip.CreateEntry("LICENSE.txt").Open())) w.Write(licenseText);
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

    async Task<(int Exit, BlastRadiusReport Report, string Markdown)> Analyze(params string[] extra)
    {
        var json = Path.Combine(_dir, "report.json");
        var md = Path.Combine(_dir, "report.md");
        var args = new List<string> { "analyze", "--consumer", _consumer, "--repo-root", _dir, "--source", _feed, "--json", json, "--markdown", md,
            "--package", Lib, "--from", "1.0.0", "--to", "2.0.0" };
        args.AddRange(extra);
        var exit = await Program.Main(args.ToArray());
        return (exit, JsonSerializer.Deserialize<BlastRadiusReport>(File.ReadAllText(json), Program.Json)!, File.ReadAllText(md));
    }

    [Fact]
    public async Task Reports_a_new_licence_even_though_no_used_api_changed()
    {
        Publish(Lib, "1.0.0", MitLicense);
        Publish(Lib, "2.0.0", FileLicense, "Commercial licence. Pay us.");

        var (exit, report, md) = await Analyze();

        Assert.Equal(Program.ExitOk, exit);
        var used = Assert.Single(Assert.Single(report.Packages).Used);
        Assert.Equal((ChangeKind.LicenseChanged, "MIT → the licence file LICENSE.txt"), (used.Change.Kind, used.Change.Subject));
        Assert.Contains("⚠️ **The licence changed: MIT → the licence file LICENSE.txt.**", md);
        Assert.DoesNotContain("Looks safe", md); // the licence still needs a look
    }

    [Fact]
    public async Task Fail_on_license_change_exits_with_the_threshold_code()
    {
        Publish(Lib, "1.0.0", MitLicense);
        Publish(Lib, "2.0.0", """<license type="expression">BUSL-1.1</license>""");

        Assert.Equal(Program.ExitThreshold, (await Analyze("--fail-on-license-change")).Exit);
        Assert.Equal(Program.ExitThreshold, (await Analyze("--fail-on", "warning")).Exit);
        Assert.Equal(Program.ExitOk, (await Analyze("--fail-on", "breaking")).Exit);
    }

    [Fact]
    public async Task Compares_licence_file_text_from_the_packages()
    {
        Publish(Lib, "1.0.0", FileLicense, "Free to use.\r\n");
        Publish(Lib, "2.0.0", FileLicense, "Free for non-commercial use only.\n");

        var (_, report, md) = await Analyze("--fail-on-license-change");

        var change = Assert.Single(report.Packages.Single().Used).Change;
        Assert.Equal("LICENSE.txt changed", change.Subject);
        Assert.Contains("The text of the licence file LICENSE.txt changed", md);
    }

    [Fact]
    public async Task Same_licence_file_text_is_quiet()
    {
        Publish(Lib, "1.0.0", FileLicense, "Free to use.\r\n");
        Publish(Lib, "2.0.0", FileLicense, "Free to use.\n");

        var (exit, report, md) = await Analyze("--fail-on-license-change");

        Assert.Equal(Program.ExitOk, exit);
        Assert.Empty(report.Packages.Single().Used);
        Assert.DoesNotContain("licence", md);
    }

    [Fact]
    public async Task A_dependency_the_code_doesnt_reference_is_kept_in_the_report_when_its_licence_changes()
    {
        Publish(Dep, "1.0.0", MitLicense);
        Publish(Dep, "2.0.0", """<license type="expression">LicenseRef-Commercial</license>""");
        Publish(Lib, "1.0.0", MitLicense, null, (Dep, "1.0.0"));
        Publish(Lib, "2.0.0", MitLicense, null, (Dep, "2.0.0"));

        var (exit, report, md) = await Analyze("--fail-on-license-change");

        Assert.Equal(Program.ExitThreshold, exit);
        var dep = Assert.Single(report.Packages, p => p.Package == Dep);
        Assert.Equal(Lib, dep.Via);
        Assert.Equal("MIT → LicenseRef-Commercial", Assert.Single(dep.Used).Change.Subject);
        Assert.Empty(report.Unreferenced);
        Assert.Contains($"### 🦇 {Dep} 1.0.0 → 2.0.0 (via {Lib})", md);
    }
}
