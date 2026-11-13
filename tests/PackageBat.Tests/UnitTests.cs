using PackageBat.Ecosystems.DotNet;
using Xunit;

namespace PackageBat.Tests;

public class FrameworkSelectorTests
{
    [Theory]
    [InlineData("net8.0", "net6.0", "netstandard2.0", "net6.0")]
    [InlineData("net6.0", "net8.0", "netstandard2.0", "netstandard2.0")]
    [InlineData("net48", "net6.0", "net462", "net462")]
    [InlineData("net48", "net6.0", "netstandard2.0", "netstandard2.0")]
    [InlineData(null, "net6.0", "net8.0", "net8.0")]
    public void Picks_the_build_the_consumer_compiles_against(string? target, string a, string b, string expected)
    {
        var best = new[] { a, b }.OrderByDescending(c => FrameworkSelector.Score(c, target)).First();
        Assert.Equal(expected, best);
    }

    [Fact]
    public void Platform_specific_builds_need_a_matching_target() =>
        Assert.Equal(0, FrameworkSelector.Score("net8.0-windows7.0", "net8.0"));

    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v3.1", "netcoreapp3.1")]
    [InlineData(".NETFramework,Version=v4.7.2", "net472")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData(".NETCoreApp,Version=v\u0668.0", null)] // non-ASCII digits (found by fuzzing)
    public void Reads_target_framework_attribute(string value, string? expected) =>
        Assert.Equal(expected, FrameworkSelector.FromTargetFrameworkAttribute(value));
}

public class NuGetTests
{
    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("[13.0.3]", "13.0.3")]
    [InlineData("2.0.0-beta4.22272.1", "2.0.0-beta4.22272.1")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("2.2.1.00", "2.2.1")] // found by fuzzing
    [InlineData("1.02.3+sha.abc", "1.2.3")]
    public void Normalizes_versions_like_nuget(string input, string expected) =>
        Assert.Equal(expected, NuGetFetcher.NormalizeVersion(input));
}

public class BumpDetectorTests
{
    [Fact]
    public void Reads_package_references_central_versions_and_packages_config()
    {
        var csproj = BumpDetector.ReadVersions("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog"><Version>[3.1.1]</Version></PackageReference>
                <PackageReference Include="Floating" Version="1.*" />
                <PackageReference Include="FromProperty" Version="$(FooVersion)" />
                <PackageVersion Include="Polly" Version="8.4.0" />
              </ItemGroup>
            </Project>
            """);
        Assert.Equal("13.0.3", csproj["newtonsoft.json"]);
        Assert.Equal("3.1.1", csproj["Serilog"]);
        Assert.Equal("8.4.0", csproj["Polly"]);
        Assert.False(csproj.ContainsKey("Floating"));
        Assert.False(csproj.ContainsKey("FromProperty"));

        var config = BumpDetector.ReadVersions("""<packages><package id="Moq" version="4.20.70" targetFramework="net48" /></packages>""");
        Assert.Equal("4.20.70", config["Moq"]);
    }
}
