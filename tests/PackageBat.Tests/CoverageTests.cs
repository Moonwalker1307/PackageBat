using System.Text;
using PackageBat.Analysis;
using PackageBat.Ecosystems;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

public class CoverageTests
{
    /// <summary>The shape coverlet's "XPlat Code Coverage" writes.</summary>
    const string Coverlet = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="1" version="1.9" timestamp="1700000000" lines-covered="2" lines-valid="4">
          <sources>
            <source>/home/runner/work/app/app/</source>
          </sources>
          <packages>
            <package name="App" line-rate="0.5">
              <classes>
                <class name="App.Program" filename="src/App/Program.cs" line-rate="0.5">
                  <methods>
                    <method name="Main" signature="()" line-rate="0.5">
                      <lines>
                        <line number="10" hits="3" branch="false" />
                        <line number="11" hits="0" branch="false" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="3" branch="false" />
                    <line number="11" hits="0" branch="false" />
                    <line number="12" hits="1" branch="true" condition-coverage="50% (1/2)" />
                  </lines>
                </class>
                <class name="App.Program/&lt;&gt;c" filename="src/App/Program.cs" line-rate="1">
                  <methods />
                  <lines>
                    <line number="20" hits="7" branch="false" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    static LineCoverage Parse(string xml) => LineCoverage.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "test.xml");

    static string Cobertura(string sources, params (string File, int Line, int Hits)[] lines) =>
        $"<coverage><sources>{sources}</sources><packages><package><classes>" +
        string.Concat(lines.GroupBy(l => l.File).Select(g =>
            $"<class filename=\"{g.Key}\"><lines>{string.Concat(g.Select(l => $"<line number=\"{l.Line}\" hits=\"{l.Hits}\"/>"))}</lines></class>")) +
        "</classes></package></packages></coverage>";

    [Fact]
    public void Reads_coverlet_line_hits()
    {
        var c = Parse(Coverlet);
        Assert.Equal(3, c.Hits("src/App/Program.cs", 10));   // listed twice (class and method), not added up
        Assert.Equal(0, c.Hits("src/App/Program.cs", 11));
        Assert.Equal(1, c.Hits("src/App/Program.cs", 12));
        Assert.Equal(7, c.Hits("src/App/Program.cs", 20));   // compiler-generated class in the same file
        Assert.Null(c.Hits("src/App/Program.cs", 13));       // not executable code
        Assert.Null(c.Hits("src/App/Other.cs", 10));         // not in the coverage at all
    }

    [Theory]
    [InlineData("src/App/Program.cs")]
    [InlineData("./src/App/Program.cs")]
    [InlineData("src\\App\\Program.cs")]
    [InlineData("SRC/app/program.cs")]
    [InlineData("App/Program.cs")]       // usage path shorter than the coverage path
    public void Matches_paths_from_another_checkout(string file) =>
        Assert.Equal(3, Parse(Coverlet).Hits(file, 10));

    [Theory]
    [InlineData("<source>C:\\agent\\_work\\1\\s\\</source>", "src\\App\\Program.cs")]
    [InlineData("", "/_/src/App/Program.cs")]
    [InlineData("", "C:\\code\\app\\src\\App\\Program.cs")]
    [InlineData("<source>/</source>", "home/me/app/src/App/Program.cs")]
    [InlineData("", "Program.cs")]       // coverage path shorter than the usage path
    public void Matches_windows_deterministic_and_absolute_paths(string sources, string coverageFile) =>
        Assert.Equal(5, Parse(Cobertura(sources, (coverageFile, 4, 5))).Hits("src/App/Program.cs", 4));

    [Fact]
    public void Does_not_match_a_different_file_with_a_similar_name()
    {
        var c = Parse(Cobertura("", ("src/MyProgram.cs", 1, 1), ("src/App/Program.cs.bak", 1, 1)));
        Assert.Null(c.Hits("src/Program.cs", 1));
    }

    [Fact]
    public void Gives_up_when_the_path_matches_more_than_one_file()
    {
        // With two roots, Cobertura doesn't say which one a relative filename belongs to.
        var c = Parse(Cobertura("<source>/r/src/</source><source>/r/tests/</source>", ("Util.cs", 3, 2), ("App/Util.cs", 3, 9)));
        Assert.Equal(9, c.Hits("src/App/Util.cs", 3));
        Assert.Equal(9, c.Hits("tests/App/Util.cs", 3)); // may really be under src/, but only one file matches either way
        Assert.Null(c.Hits("Util.cs", 3));               // both files end in Util.cs
        Assert.Equal(2, c.Hits("src/Util.cs", 3));
    }

    [Fact]
    public void Adds_up_hits_across_reports()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packagebat-coverage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var a = Path.Combine(dir, "a.xml");
            var b = Path.Combine(dir, "b.xml");
            File.WriteAllText(a, Coverlet);
            File.WriteAllText(b, Cobertura("<source>D:\\other\\</source>", ("src\\App\\Program.cs", 10, 2), ("src\\App\\Program.cs", 11, 4), ("src\\Lib.cs", 1, 0)));
            var c = LineCoverage.Read(new[] { a, b });
            Assert.Equal(5, c.Hits("src/App/Program.cs", 10));
            Assert.Equal(4, c.Hits("src/App/Program.cs", 11));
            Assert.Equal(7, c.Hits("src/App/Program.cs", 20));
            Assert.Equal(0, c.Hits("src/Lib.cs", 1));
            Assert.Equal(new[] { a, b }, c.Sources);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Hit_counts_saturate()
    {
        var c = Parse(Cobertura("", ("a.cs", 1, 0)).Replace("hits=\"0\"", "hits=\"99999999999\""));
        Assert.Equal(int.MaxValue, c.Hits("a.cs", 1));
    }

    [Theory]
    [InlineData("<line number=\"x\" hits=\"1\"/>")]
    [InlineData("<line number=\"0\" hits=\"1\"/>")]
    [InlineData("<line number=\"3\" hits=\"-1\"/>")]
    [InlineData("<line number=\"3\"/>")]
    public void Skips_lines_it_cannot_read(string line)
    {
        var c = Parse($"<coverage><packages><package><classes><class filename=\"a.cs\"><lines>{line}<line number=\"4\" hits=\"2\"/></lines></class></classes></package></packages></coverage>");
        Assert.Null(c.Hits("a.cs", 3));
        Assert.Equal(2, c.Hits("a.cs", 4));
    }

    [Fact]
    public void Lines_outside_a_class_are_ignored()
    {
        var c = Parse("<coverage><class filename=\"a.cs\"/><lines><line number=\"1\" hits=\"1\"/></lines><class filename=\"\"><line number=\"2\" hits=\"1\"/></class></coverage>");
        Assert.Null(c.Hits("a.cs", 1));
        Assert.Null(c.Hits("a.cs", 2));
    }

    [Fact]
    public void Ignores_the_doctype_and_never_expands_entities()
    {
        var c = Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
            <coverage><packages><package><classes><class filename="a.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages></coverage>
            """);
        Assert.Equal(1, c.Hits("a.cs", 1));

        Assert.Throws<UsageException>(() => Parse("""<!DOCTYPE x [<!ENTITY e "&e;&e;">]><coverage>&e;</coverage>"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<coverage><class filename=\"a.cs\">")]
    public void Malformed_xml_is_a_usage_error(string xml) =>
        Assert.Contains("test.xml is not a valid Cobertura coverage file", Assert.Throws<UsageException>(() => Parse(xml)).Message);

    [Fact]
    public void Missing_file_is_a_usage_error() =>
        Assert.StartsWith("Could not read coverage file", Assert.Throws<UsageException>(() => LineCoverage.Read(new[] { "/nonexistent/coverage.xml" })).Message);

    [Fact]
    public void Annotate_marks_locations_and_counts_distinct_call_sites()
    {
        static SourceLocation L(string? file, int? line) => new() { File = file, Line = line, Module = "App", Symbol = "S" };
        var report = new BlastRadiusReport
        {
            Packages =
            {
                new PackageReport { Used = {
                    new UsedChange { Locations = { L("src/App/Program.cs", 10), L("src/App/Program.cs", 11), L(null, null) } },
                    new UsedChange { Locations = { L("src/App/Program.cs", 10), L("src/App/Program.cs", 13) } },
                } },
                new PackageReport { Used = { new UsedChange { Locations = { L("src/App/Program.cs", 20), L("src/App/Program.cs", null) } } } },
            },
        };
        var c = Parse(Coverlet);
        var summary = c.Annotate(report);

        Assert.Same(summary, report.Coverage);
        Assert.Equal((6, 2, 1, 3), (summary.CallSites, summary.Covered, summary.NotCovered, summary.Unknown));
        var locs = report.Packages.SelectMany(p => p.Used).SelectMany(u => u.Locations).Select(l => l.TestHits);
        Assert.Equal(new int?[] { 3, 0, null, 3, null, 7, null }, locs);
    }
}
