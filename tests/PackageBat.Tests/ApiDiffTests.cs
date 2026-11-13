using PackageBat.Analysis;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// Severity classification of API changes, on hand-built language-neutral
/// surfaces. These pin down the rules that decide "breaking" vs "recompile"
/// vs "warning", which drive both the report and --fail-on.
/// </summary>
public class ApiDiffTests
{
    const string T = "Acme.Client";

    static ApiParameter P(string type, string? name = null, bool optional = false, bool isParams = false) =>
        new() { Type = type, Name = name, HasDefault = optional, IsParams = isParams };

    static ApiMember M(string name, string ret = "void", bool isStatic = false, Deprecation? deprecated = null, params ApiParameter[] ps) => new()
    {
        Key = $"{name}({string.Join(",", ps.Select(p => p.Type))}){(isStatic ? " static" : "")}:{ret}",
        Name = name,
        DeclaringType = T,
        Display = $"Client.{name}({string.Join(", ", ps.Select(p => p.Type))})",
        Kind = ApiMemberKind.Method,
        IsStatic = isStatic,
        ReturnType = ret,
        Parameters = ps.ToList(),
        Deprecated = deprecated,
    };

    static ApiType Type(string name = T, params ApiMember[] members) => new()
    {
        Name = name,
        Display = name[(name.LastIndexOf('.') + 1)..],
        Kind = ApiTypeKind.Class,
        IsInheritable = true,
        Members = members.Select(m => { m.DeclaringType = name; return m; }).ToList(),
    };

    static ApiSurface Api(params ApiType[] types) => new() { Package = "Acme", Types = types.ToList() };

    /// <summary>Diff one old member against new overloads of the same name.</summary>
    static ApiChange Single(ApiMember oldMember, params ApiMember[] newMembers)
    {
        var diff = ApiDiff.Compute(Api(Type(T, oldMember)), Api(Type(T, newMembers)));
        return Assert.Single(diff.Changes);
    }

    [Fact]
    public void Added_optional_parameter_is_binary_only()
    {
        var c = Single(M("Get", "string", ps: P("string")), M("Get", "string", ps: new[] { P("string"), P("int", optional: true) }));
        Assert.Equal(ChangeKind.SignatureChanged, c.Kind);
        Assert.Equal(Severity.BinaryOnly, c.Severity);
        Assert.StartsWith("Signature changed, but existing calls still compile", c.Description);
    }

    [Fact]
    public void Added_required_parameter_is_breaking()
    {
        var c = Single(M("Get", ps: P("string")), M("Get", ps: new[] { P("string"), P("int", optional: false) }));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Optional_then_required_parameter_is_breaking()
    {
        var c = Single(M("Get", ps: P("string")), M("Get", ps: new[] { P("string"), P("int", optional: true), P("int") }));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Added_params_array_is_binary_only()
    {
        var c = Single(M("Log", ps: P("string")), M("Log", ps: new[] { P("string"), P("object[]", isParams: true) }));
        Assert.Equal(Severity.BinaryOnly, c.Severity);
    }

    [Fact]
    public void Optional_parameter_plus_return_type_change_is_breaking()
    {
        var c = Single(M("Get", "string", ps: P("string")), M("Get", "object", ps: new[] { P("string"), P("int", optional: true) }));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Optional_parameter_plus_becoming_static_is_breaking()
    {
        var c = Single(M("Get", ps: P("string")), M("Get", isStatic: true, ps: new[] { P("string"), P("int", optional: true) }));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Return_type_change_is_breaking_and_explained()
    {
        var c = Single(M("Count", "int"), M("Count", "long"));
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("Signature changed (the type changed from int to long)", c.Description);
    }

    [Theory]
    [InlineData(false, true, "it is now static")]
    [InlineData(true, false, "it is no longer static")]
    public void Static_change_is_breaking_and_explained(bool wasStatic, bool isStatic, string why)
    {
        var c = Single(M("Run", isStatic: wasStatic), M("Run", isStatic: isStatic));
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal($"Signature changed ({why})", c.Description);
    }

    [Fact]
    public void Parameter_type_change_names_the_parameter()
    {
        var c = Single(M("Send", ps: P("string", "body")), M("Send", ps: P("byte[]", "body")));
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("Signature changed (parameter body changed from string to byte[])", c.Description);
        Assert.Equal(new[] { "Client.Send(byte[])" }, c.Suggestions);
    }

    [Fact]
    public void Unnamed_parameter_is_numbered_from_one()
    {
        var c = Single(M("Send", ps: new[] { P("int"), P("string") }), M("Send", ps: new[] { P("int"), P("byte[]") }));
        Assert.Equal("Signature changed (parameter 2 changed from string to byte[])", c.Description);
    }

    [Fact]
    public void Explanation_lists_at_most_three_differences()
    {
        var c = Single(M("F", "int", ps: new[] { P("int", "a"), P("int", "b"), P("int", "c") }),
                       M("F", "long", ps: new[] { P("long", "a"), P("long", "b"), P("long", "c") }));
        Assert.Equal("Signature changed (the type changed from int to long; parameter a changed from int to long; parameter b changed from int to long)", c.Description);
    }

    [Fact]
    public void Explanation_prefers_the_overload_with_the_same_staticness()
    {
        var c = Single(M("F", ps: P("int", "x")),
                       M("F", isStatic: true, ps: P("int", "x")),
                       M("F", ps: P("long", "x")));
        Assert.Equal("Signature changed (parameter x changed from int to long)", c.Description);
    }

    [Fact]
    public void Explanation_falls_back_to_an_overload_with_the_same_parameter_count()
    {
        var c = Single(M("F", ps: P("int", "x")), M("F", isStatic: true, ps: P("int", "x")));
        Assert.Equal("Signature changed (it is now static)", c.Description);
    }

    [Fact]
    public void No_explanation_when_no_overload_has_the_same_parameter_count()
    {
        var c = Single(M("F", ps: P("int")), M("F", ps: new[] { P("int"), P("int") }));
        Assert.Equal("Signature changed", c.Description);
    }

    [Fact]
    public void Params_array_absorbing_old_arguments_is_binary_only()
    {
        var c = Single(M("Log", ps: new[] { P("string"), P("string") }), M("Log", ps: P("string[]", isParams: true)));
        Assert.Equal(Severity.BinaryOnly, c.Severity);
    }

    [Fact]
    public void Params_array_of_another_type_is_breaking()
    {
        var c = Single(M("Log", ps: P("int")), M("Log", ps: P("string[]", isParams: true)));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Same_type_array_that_is_not_params_is_breaking()
    {
        var c = Single(M("Log", ps: P("string")), M("Log", ps: P("string[]")));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Params_array_that_is_not_last_is_breaking()
    {
        var c = Single(M("Log", ps: P("string")), M("Log", ps: new[] { P("string[]", isParams: true), P("int", optional: true) }));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void Removed_parameter_is_breaking()
    {
        var c = Single(M("Get", ps: new[] { P("string"), P("int") }), M("Get", ps: P("string")));
        Assert.Equal(Severity.Breaking, c.Severity);
    }

    [Fact]
    public void One_compatible_overload_is_enough_for_binary_only()
    {
        var c = Single(M("Get", ps: P("string")),
                       M("Get", ps: P("int")),
                       M("Get", ps: new[] { P("string"), P("int", optional: true) }));
        Assert.Equal(Severity.BinaryOnly, c.Severity);
    }

    [Fact]
    public void Member_with_no_same_name_candidate_is_removed()
    {
        var c = Single(M("Gone"), M("Other"));
        Assert.Equal(ChangeKind.MemberRemoved, c.Kind);
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("Removed", c.Description);
        Assert.StartsWith("member-removed:Acme.Client::Gone(", c.Id);
    }

    [Fact]
    public void Newly_obsolete_member_is_a_warning()
    {
        var c = Single(M("Retry"), M("Retry", deprecated: new Deprecation { Message = "Use a policy" }));
        Assert.Equal(ChangeKind.Deprecated, c.Kind);
        Assert.Equal(Severity.Warning, c.Severity);
        Assert.Equal("Deprecated: Use a policy", c.Description);
    }

    [Fact]
    public void Obsolete_as_error_is_breaking()
    {
        var c = Single(M("Retry"), M("Retry", deprecated: new Deprecation { Message = "Gone soon", IsError = true }));
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("Marked obsolete as an error: Gone soon", c.Description);
    }

    [Fact]
    public void Obsolete_without_message_says_just_deprecated()
    {
        var c = Single(M("Retry"), M("Retry", deprecated: new Deprecation()));
        Assert.Equal("Deprecated", c.Description);
    }

    [Fact]
    public void Already_obsolete_member_is_not_reported_again()
    {
        var dep = new Deprecation { Message = "old" };
        var diff = ApiDiff.Compute(Api(Type(T, M("Retry", deprecated: dep))), Api(Type(T, M("Retry", deprecated: dep))));
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void Newly_obsolete_type_is_reported_with_its_severity()
    {
        var oldT = Type(T);
        var newT = Type(T);
        newT.Deprecated = new Deprecation { IsError = true };
        var c = Assert.Single(ApiDiff.Compute(Api(oldT), Api(newT)).Changes);
        Assert.Equal("type-deprecated:Acme.Client", c.Id);
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("Marked obsolete as an error", c.Description);
    }

    [Theory]
    [InlineData(true, "Type is now sealed, so it can no longer be derived from")]
    [InlineData(false, "Type can no longer be derived from")]
    public void Type_that_can_no_longer_be_derived_from_is_breaking(bool sealedNow, string description)
    {
        var newT = Type(T);
        newT.IsInheritable = false;
        newT.IsSealed = sealedNow;
        var c = Assert.Single(ApiDiff.Compute(Api(Type(T)), Api(newT)).Changes);
        Assert.Equal(ChangeKind.TypeSealed, c.Kind);
        Assert.Equal(description, c.Description);
    }

    [Fact]
    public void Removed_type_without_replacement()
    {
        var c = Assert.Single(ApiDiff.Compute(Api(Type("Acme.Legacy")), Api(Type("Acme.Other"))).Changes);
        Assert.Equal("Type removed", c.Description);
        Assert.Equal("Acme.Legacy", c.Subject);
        Assert.Empty(c.Suggestions);
    }

    [Fact]
    public void Moved_type_suggests_its_new_namespace()
    {
        var c = Assert.Single(ApiDiff.Compute(Api(Type("Acme.Legacy")), Api(Type("Acme.Old.Legacy"))).Changes);
        Assert.Equal("Type moved or was replaced", c.Description);
        Assert.Equal(new[] { "Acme.Old.Legacy" }, c.Suggestions);
    }

    [Fact]
    public void Forwarded_type_is_not_reported()
    {
        var newApi = Api();
        newApi.ForwardedTypes.Add("Acme.Legacy");
        Assert.Empty(ApiDiff.Compute(Api(Type("Acme.Legacy")), newApi).Changes);
    }

    [Fact]
    public void Abstract_member_added_to_interface_breaks_implementers()
    {
        var oldT = Type("Acme.IHandler");
        var added = M("Close");
        added.IsAbstract = true;
        var newT = Type("Acme.IHandler", added);
        newT.Kind = ApiTypeKind.Interface;
        var c = Assert.Single(ApiDiff.Compute(Api(oldT), Api(newT)).Changes);
        Assert.Equal(ChangeKind.AbstractMemberAdded, c.Kind);
        Assert.Equal(Severity.Breaking, c.Severity);
        Assert.Equal("New interface member that implementers must provide", c.Description);
    }

    [Fact]
    public void Abstract_member_added_to_class_breaks_derived_classes()
    {
        var added = M("Close");
        added.IsAbstract = true;
        var c = Assert.Single(ApiDiff.Compute(Api(Type(T)), Api(Type(T, added))).Changes);
        Assert.Equal("New abstract member that derived classes must override", c.Description);
    }

    [Fact]
    public void Added_non_abstract_member_is_only_counted()
    {
        var diff = ApiDiff.Compute(Api(Type(T, M("A"))), Api(Type(T, M("A"), M("B")), Type("Acme.New")));
        Assert.Empty(diff.Changes);
        Assert.Equal(2, diff.AddedCount); // one member + one type
    }

    [Fact]
    public void Removed_members_are_not_counted_as_added()
    {
        var diff = ApiDiff.Compute(Api(Type(T, M("A"), M("B"))), Api(Type(T, M("A"))));
        Assert.Equal(0, diff.AddedCount);
    }

    [Fact]
    public void Changes_are_ordered_by_severity_then_type_then_subject()
    {
        var oldApi = Api(Type("Acme.B", M("Warn"), M("Gone")), Type("Acme.A", M("Zed"), M("Alpha")));
        var newApi = Api(Type("Acme.B", M("Warn", deprecated: new Deprecation())), Type("Acme.A"));
        var ids = ApiDiff.Compute(oldApi, newApi).Changes.Select(c => c.Id.Split('(')[0]).ToList();
        Assert.Equal(new[] { "member-removed:Acme.A::Alpha", "member-removed:Acme.A::Zed", "member-removed:Acme.B::Gone", "member-deprecated:Acme.B::Warn" }, ids);
    }
}
