using PackageBat.Analysis;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>Bugs found by the fuzz tests (tests/PackageBat.Fuzz), kept as plain examples.</summary>
public class FuzzRegressionTests
{
    static ApiMember Method(string type, string name) => new()
    {
        Key = name + "()", Name = name, DeclaringType = type, Display = $"{type}.{name}()", Kind = ApiMemberKind.Method, ReturnType = "System.Void",
    };

    static ApiType Type(string name, string? baseType, params ApiMember[] members) => new()
    {
        Name = name, Display = name, Module = "Pkg", BaseType = baseType, Members = members.ToList(),
    };

    [Fact]
    public void Member_lost_when_the_base_type_changes_is_reported_on_the_derived_type()
    {
        // v1: Derived : Base, Base.Run(). v2: Derived : Other, Base.Run() still exists.
        var v1 = new ApiSurface { Types = { Type("N.Base", null, Method("N.Base", "Run")), Type("N.Derived", "N.Base"), Type("N.Other", null) } };
        var v2 = new ApiSurface { Types = { Type("N.Base", null, Method("N.Base", "Run")), Type("N.Derived", "N.Other"), Type("N.Other", null) } };
        var usages = new UsageSet { Usages = { new Usage { TypeName = "N.Derived", MemberKey = "Run()", Kind = UsageKind.Member, Location = new() { File = "a.cs", Line = 1 } } } };

        var report = UsageMatcher.Analyze(v1, v2, usages);

        var change = Assert.Single(report.AllChanges);
        Assert.Equal("member-removed:N.Derived::Run()", change.Id);
        Assert.Equal("N.Derived", change.TypeName);
        Assert.Contains("no longer derives from", change.Description);
        Assert.Equal(change.Id, Assert.Single(report.Used).Change.Id);
    }

    [Fact]
    public void Member_lost_when_the_base_type_is_removed_is_reported_on_the_derived_type()
    {
        var v1 = new ApiSurface { Types = { Type("N.Base", null, Method("N.Base", "Run")), Type("N.Derived", "N.Base") } };
        var v2 = new ApiSurface { Types = { Type("N.Derived", null) } };
        var usages = new UsageSet { Usages = { new Usage { TypeName = "N.Derived", MemberKey = "Run()", Kind = UsageKind.Member, Location = new() { File = "a.cs", Line = 1 } } } };

        var report = UsageMatcher.Analyze(v1, v2, usages);

        var used = Assert.Single(report.Used).Change;
        Assert.Equal("member-removed:N.Derived::Run()", used.Id);
        Assert.Contains("which was removed", used.Description);
        Assert.Contains(report.AllChanges, c => c.Id == used.Id);
    }

    [Fact]
    public void Abstract_member_brought_in_by_a_new_interface_is_in_the_full_change_list()
    {
        // v2: N.Base now implements N.IRun, which has an abstract Run(); deriving from N.Base breaks.
        var run = Method("N.IRun", "Run");
        run.IsAbstract = true;
        var v1 = new ApiSurface { Types = { Type("N.Base", null), Type("N.IRun", null, run) } };
        var v2 = new ApiSurface { Types = { Type("N.Base", null), Type("N.IRun", null, run) } };
        v2.Types[0].Interfaces.Add("N.IRun");
        var usages = new UsageSet { Usages = { new Usage { TypeName = "N.Base", Kind = UsageKind.Inherit, Location = new() { File = "a.cs", Line = 1 } } } };

        var report = UsageMatcher.Analyze(v1, v2, usages);

        var used = Assert.Single(report.Used).Change;
        Assert.Equal("abstract-added:N.Base::Run()", used.Id);
        Assert.Contains(report.AllChanges, c => c.Id == used.Id);
    }

    [Fact]
    public void Duplicate_type_names_compare_the_same_way_they_are_looked_up()
    {
        var api = new ApiSurface
        {
            Types =
            {
                new ApiType { Name = "N.A", IsInheritable = false },
                new ApiType { Name = "N.A", IsInheritable = true },
            },
        };
        Assert.Empty(ApiDiff.Compute(api, api).Changes);
    }

    [Theory]
    [InlineData("""{"types":[null]}""", "types[0] is null")]
    [InlineData("""{"types":[{"name":"N.A","members":[{"name":"M"}]}]}""", "types[0].members[0].key is missing")]
    [InlineData("""{"types":[{"display":"A"}]}""", "types[0].name is missing")]
    public void Malformed_api_json_is_rejected_with_a_message(string json, string expected)
    {
        var api = System.Text.Json.JsonSerializer.Deserialize<ApiSurface>(json, Program.Json)!;
        Assert.Equal(expected, Validation.Check(api));
    }

    [Fact]
    public void Missing_lists_in_api_json_are_treated_as_empty()
    {
        var api = System.Text.Json.JsonSerializer.Deserialize<ApiSurface>("""{"types":[{"name":"N.A","members":null,"interfaces":null}],"modules":null}""", Program.Json)!;
        Assert.Null(Validation.Check(api));
        Assert.Empty(ApiDiff.Compute(api, api).Changes);
    }
}
