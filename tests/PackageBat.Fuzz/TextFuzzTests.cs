using CsCheck;
using PackageBat.Ecosystems.DotNet;
using Xunit;
using Xunit.Abstractions;

namespace PackageBat.Fuzz;

/// <summary>Small parsers fed by repository files and package folders: project files, versions, framework names.</summary>
[Trait("Category", "Fuzz")]
public sealed class TextFuzzTests(ITestOutputHelper output)
{
    static readonly Gen<string> Junk = Gen.String[Gen.Char["0123456789.-+[]()*,$ abcnetrvsdoflxwi\t\né١"], 0, 24];

    static readonly string[] ProjectSnippets =
    {
        "<Project>", "</Project>", "<ItemGroup>", "</ItemGroup>", "<PackageReference Include=\"A\" Version=\"1.0.0\" />",
        "<PackageVersion Include=\"B\" Version=\"[2.0]\" />", "<PackageReference Update=\"C\"><Version>3.0</Version></PackageReference>",
        "<package id=\"D\" version=\"4.0\" />", "<packages>", "</packages>", "<Version>", "</Version>", "<!--", "-->",
        "<![CDATA[", "]]>", "<?xml version=\"1.0\"?>", "<!DOCTYPE x [<!ENTITY e \"&e;&e;\">]>", "&e;", "&amp;", "&#0;",
        "<PackageReference Include=\"\" Version=\"\" />", "<PackageReference Include=\"E\" VersionOverride=\" [1.0] \" />",
        "<x:PackageReference xmlns:x=\"urn:x\" Include=\"F\" Version=\"1\" />", "\u0000", "<", ">", "\"", "=",
    };

    [Fact]
    public void Project_file_reader_never_throws() =>
        Fuzz.Crashes("read-versions", output,
            Gen.Frequency((3, Gen.OneOfConst(ProjectSnippets).Array[0, 14].Select(a => string.Concat(a))), (1, Junk)),
            xml => BumpDetector.ReadVersions(xml), _ => false, System.Text.Encoding.UTF8.GetBytes, ".xml");

    [Fact]
    public void Version_normalizer_never_throws() =>
        Fuzz.Crashes("normalize-version", output, Junk, v => NuGetFetcher.NormalizeVersion(v),
            _ => false, System.Text.Encoding.UTF8.GetBytes, ".txt");

    /// <summary>Things that look like the versions people write in project files.</summary>
    static readonly Gen<string> VersionLike =
        Gen.Select(Gen.String[Gen.Char["0123456789"], 1, 4].Array[1, 5], Gen.OneOfConst("", "-beta", "-rc.1", "-alpha.2.3", "-preview.7.24405.3"),
            Gen.OneOfConst("", "+sha.abc", "+1"), Gen.OneOfConst(("", ""), ("[", "]"), (" ", "\t")),
            (parts, pre, meta, wrap) => wrap.Item1 + string.Join('.', parts) + pre + meta + wrap.Item2);

    [Fact]
    public void Normalizing_a_version_twice_changes_nothing() =>
        Fuzz.Property(VersionLike, v =>
        {
            var once = NuGetFetcher.NormalizeVersion(v);
            Assert.Equal(once, NuGetFetcher.NormalizeVersion(once));
            Assert.DoesNotContain("+", once);
        });

    static readonly Gen<string> Tfm = Gen.Frequency(
        (3, Gen.Select(Gen.OneOfConst("net", "netcoreapp", "netstandard", "net4", "net"), Gen.String[Gen.Char["0123456789."], 0, 12],
            Gen.OneOfConst("", "-windows", "-android31.0", "-ios", "-"), (a, b, c) => a + b + c)),
        (1, Junk));

    [Fact]
    public void Framework_scoring_never_throws() =>
        Fuzz.Crashes("framework-score", output, Gen.Select(Tfm, Tfm.Null()), t =>
        {
            FrameworkSelector.Score(t.Item1, t.Item2);
            FrameworkSelector.FromTargetFrameworkAttribute($".NETCoreApp,Version=v{t.Item1}");
            FrameworkSelector.FromTargetFrameworkAttribute($".NETFramework,Version=v{t.Item1}");
            FrameworkSelector.FromTargetFrameworkAttribute(t.Item1);
        }, _ => false, t => System.Text.Encoding.UTF8.GetBytes($"{t.Item1}\n{t.Item2}"), ".txt");

    [Fact]
    public void Short_names_never_throw() =>
        Fuzz.Crashes("short-name", output, Gen.String[Gen.Char["Ab.+`1<>,[] "], 0, 20],
            n => MetadataNames.ShortName(n), _ => false, System.Text.Encoding.UTF8.GetBytes, ".txt");
}
