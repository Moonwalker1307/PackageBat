using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

public class NuspecTests
{
    const string Grouped = """
        <?xml version="1.0"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>X</id>
            <dependencies>
              <group targetFramework=".NETFramework4.6.2"><dependency id="Fx.Only" version="1.0.0" /></group>
              <group targetFramework=".NETStandard2.0"><dependency id="Std" version="2.0" /></group>
              <group targetFramework="net8.0"><dependency id="Core" version="8.0.0" /><dependency id="Ranged" version="[1.2, 2.0)" /></group>
              <group targetFramework="net10.0"><dependency id="Core" version="16.4.0" /><dependency id="NoLower" version="(, 2.0]" /></group>
            </dependencies>
          </metadata>
        </package>
        """;

    static string[] Deps(string xml, string? tfm) => Nuspec.Dependencies(xml, tfm).Select(d => $"{d.Id} {d.MinVersion}").ToArray();

    [Theory]
    [InlineData("net10.0", "Core 16.4.0")]
    [InlineData("net9.0", "Core 8.0.0|Ranged 1.2.0")]
    [InlineData(null, "Core 16.4.0")]
    [InlineData("net48", "Fx.Only 1.0.0")]
    [InlineData("netstandard2.1", "Std 2.0.0")]
    [InlineData("net6.0", "Std 2.0.0")]
    public void Picks_the_group_nuget_would(string? tfm, string expected) =>
        Assert.Equal(expected.Split('|'), Deps(Grouped, tfm));

    [Fact]
    public void Group_without_a_framework_is_the_fallback() =>
        Assert.Equal(new[] { "Any 1.0.0" }, Deps("""
            <package><metadata><dependencies>
              <group><dependency id="Any" version="1.0" /></group>
              <group targetFramework="net11.0"><dependency id="Newer" version="1.0" /></group>
            </dependencies></metadata></package>
            """, "net8.0"));

    [Fact]
    public void Ungrouped_dependencies_apply_to_every_framework() =>
        Assert.Equal(new[] { "A 1.0.0", "B 2.1.0" }, Deps("""<package><metadata><dependencies><dependency id="A" version="1.0" /><dependency id="B" version="[2.1]" /><dependency id="A" version="9" /><dependency version="1" /></dependencies></metadata></package>""", "net8.0"));

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<package/>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"&e;&e;\">]><package>&e;</package>")]
    [InlineData("<package><metadata><dependencies><group targetFramework=\"net99.0\"><dependency id=\"A\" version=\"1\"/></group></dependencies></metadata></package>")]
    public void Nothing_to_follow(string xml) => Assert.Empty(Deps(xml, "net8.0"));

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("[1.0]", "1.0.0")]
    [InlineData("[1.2.3, 2.0.0)", "1.2.3")]
    [InlineData("(1.0,)", "1.0.0")]
    [InlineData(" 2.0.0-beta.1 ", "2.0.0-beta.1")]
    [InlineData("(,2.0]", null)]
    [InlineData("1.*", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Min_version(string? range, string? expected) => Assert.Equal(expected, Nuspec.MinVersion(range));

    [Theory]
    [InlineData(".NETFramework4.6.2", "net462")]
    [InlineData(".NETFramework4.8", "net48")]
    [InlineData(".NETStandard2.0", "netstandard2.0")]
    [InlineData(".NETCoreApp3.1", "netcoreapp3.1")]
    [InlineData("net10.0", "net10.0")]
    [InlineData("NET8.0-Windows7.0", "net8.0-windows7.0")]
    public void Short_framework(string tf, string expected) => Assert.Equal(expected, Nuspec.ShortFramework(tf));
}

public class NuGetVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0", "1.0.0.0", 0)]
    [InlineData("10.0.15", "10.0.12", 1)]
    [InlineData("16.4.0", "8.0.0", 1)]
    [InlineData("2.0.0-beta", "2.0.0", -1)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", -1)]
    [InlineData("2.0.0-alpha", "2.0.0-Beta", -1)]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1.x", -1)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    [InlineData("1.0.0+build", "1.0.0", 0)]
    public void Compares_like_nuget(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(NuGetVersion.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(NuGetVersion.Compare(b, a)));
    }
}

public class LateBoundScannerTests
{
    [Theory]
    [InlineData("Refit.RequestBuilderFactory, Refit.Reflection", "Refit.Reflection", "Refit.RequestBuilderFactory")]
    [InlineData("A.B+Nested, A.Plugins", "A.Plugins", "A.B+Nested")]
    [InlineData("A.List`1[[System.Int32, mscorlib]], A.Collections", "A.Collections", "A.List`1[[System.Int32, mscorlib]]")]
    [InlineData("A.Type, A.Asm, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "A.Asm", "A.Type")]
    [InlineData("  A.Type ,A ", "A", "A.Type")]
    [InlineData("Some.Assembly", "Some.Assembly", null)]
    public void Parses_assembly_qualified_names(string s, string assembly, string? type) =>
        Assert.Equal((assembly, type), LateBoundScanner.Parse(s));

    [Theory]
    [InlineData("Hello, World")]
    [InlineData("Done, retrying in 5s")]
    [InlineData("This interface needs the reflection request builder, which is not installed.")]
    [InlineData("A.Type, ")]
    [InlineData("A.Type, B, C")]
    [InlineData("Single")]
    [InlineData("1.2.3")]
    [InlineData("")]
    public void Ignores_other_strings(string s) => Assert.Null(LateBoundScanner.Parse(s));

    [Fact]
    public void Finds_names_and_what_loads_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-latebound-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dll = Compiler.Compile(dir, "Lib", "1.0.0.0", new[] { ("Lib.cs", """
                using System;
                using System.Reflection;
                public static class Loader
                {
                    const string Name = "Lib.Impl, Lib.Optional";
                    public static Type? ByConst() => Type.GetType(Name, throwOnError: false);
                    public static object? ByActivator() => Activator.CreateInstance(Type.GetType("Lib.Other, Lib.Other")!);
                    public static Assembly Bare() => Assembly.Load("Lib.Bare");
                    public static string Stored = "Lib.Stored, Lib.Stored";
                    public static string NotAnAssembly() => "Lib.Namespace" + "x";
                    public static string Message() => "Hello, World";
                }
                """) });

            var found = LateBoundScanner.Scan(new[] { dll }).OrderBy(n => n.AssemblyName).ToList();

            Assert.Equal(new[]
            {
                ("Lib.Bare", (string?)null, "Assembly.Load"),
                ("Lib.Optional", "Lib.Impl", "Type.GetType"),
                ("Lib.Other", "Lib.Other", "Type.GetType"),
                ("Lib.Stored", "Lib.Stored", null),
            }, found.Select(n => (n.AssemblyName, n.TypeName, n.Loader)));
            Assert.All(found, n => Assert.Equal("Lib", n.Module));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("System.Private.CoreLib", true)]
    [InlineData("mscorlib", true)]
    [InlineData("netstandard", true)]
    [InlineData("System", true)]
    [InlineData("Microsoft.Win32.Registry", true)]
    [InlineData("Microsoft.CSharp", true)]
    [InlineData("Refit.Reflection", false)]
    [InlineData("Microsoft.Extensions.Http", false)]
    [InlineData("Systemic.Thing", false)]
    public void Platform_assemblies(string name, bool platform) => Assert.Equal(platform, DependencyGraph.IsPlatform(name));
}

public class RuntimeReportTests
{
    static PackageReport Refitish(string? via = null, params UsedChange[] used)
    {
        var p = new PackageReport { Package = "Core", FromVersion = "1.0.0", ToVersion = "2.0.0", Via = via, ReferenceCount = 2, UsedApiCount = 1 };
        p.Used.AddRange(used);
        p.AllChanges.AddRange(used.Select(u => u.Change));
        return p;
    }

    static UsedChange Missing(params string[] suggestions) => new()
    {
        Change = new ApiChange
        {
            Id = "runtime:Core.Engine", Kind = ChangeKind.RuntimeDependencyMissing, Severity = Severity.Warning,
            TypeName = "Core.Factory", Subject = "Core.Engine", Description = "Core.dll passes \"Core.Factory, Core.Engine\" to Type.GetType",
            Suggestions = suggestions.ToList(),
        },
    };

    [Fact]
    public void Missing_runtime_dependency_is_a_callout_not_a_table_row()
    {
        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { Refitish(null, Missing("Core.Engine 2.0.0")) } }, null).ReplaceLineEndings("\n");
        Assert.Contains("""
            ### 🦇 Core 1.0.0 → 2.0.0

            ⚠️ **Core 2.0.0 loads `Core.Engine` by name at run time, but nothing in your dependency graph provides it.** If your code uses this feature, add the `Core.Engine` package (2.0.0). The build won't tell you: it only fails when that code runs.
            <sub>Core.dll passes "Core.Factory, Core.Engine" to Type.GetType.</sub>

            The API changes don't touch any of the 1 API your code uses (2 references).
            """, md);
        Assert.DoesNotContain("| ⚠️ |", md);
        Assert.DoesNotContain("Looks safe", md);
    }

    [Fact]
    public void Without_a_matching_package_it_says_so()
    {
        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { Refitish(null, Missing()) } }, null);
        Assert.Contains("If your code uses this feature, add the package that provides it.", md);
    }

    [Fact]
    public void Transitive_package_names_what_brought_it_in()
    {
        var parent = new PackageReport { Package = "Client", FromVersion = "1.0.0", ToVersion = "2.0.0", ReferenceCount = 1, UsedApiCount = 1 };
        var md = MarkdownReport.Render(new BlastRadiusReport { Packages = { parent, Refitish("Client", Missing("Core.Engine 2.0.0")) } }, null).ReplaceLineEndings("\n");
        Assert.Contains("""
            **Looks safe:** this bump doesn't change any of the 1 API your code uses (1 reference).

            ⚠️ **Its new version also updates Core 1.0.0 → 2.0.0: see below.**
            """, md);
        Assert.Contains("""
            ### 🦇 Core 1.0.0 → 2.0.0 (via Client)

            Your projects don't set this package's version: the new Client needs Core 2.0.0, so it changes with that bump.
            """, md);
    }

    [Fact]
    public void Quiet_transitive_packages_are_one_line_each()
    {
        var quiet = Refitish("Client");
        var report = new BlastRadiusReport
        {
            Packages = { new PackageReport { Package = "Client", FromVersion = "1.0.0", ToVersion = "2.0.0" }, quiet },
            Unreferenced = { new PackageBump { Package = "Deep", From = "1.0", To = "1.1", Via = "Core" } },
        };
        var md = MarkdownReport.Render(report, null).ReplaceLineEndings("\n");
        Assert.DoesNotContain("### 🦇 Core", md);
        Assert.DoesNotContain("also updates", md);
        Assert.Contains("""
            <details><summary>2 dependencies of these packages also change version, with nothing to report</summary>

            - Core 1.0.0 → 2.0.0 (via Client): no changes to the 1 API your code uses
            - Deep 1.0 → 1.1 (via Core): your compiled code doesn't reference it

            </details>
            """, md);
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData(null, true, null)]
    [InlineData("", null, "[0] needs package, from and to")]
    [InlineData("", false, "[0] needs package, from and to")]
    [InlineData("1.0", null, null)]
    public void Added_packages_need_no_from(string? from, bool? added, string? problem) =>
        Assert.Equal(problem, Validation.Check(new List<PackageBump> { new() { Package = "A", From = from!, To = "1.0", Added = added } }));
}
