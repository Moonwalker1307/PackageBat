using PackageBat.Analysis;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>Joining usages with changes, on hand-built language-neutral data.</summary>
public class UsageMatcherTests
{
    static ApiMember M(string type, string name, ApiMemberKind kind = ApiMemberKind.Method, bool isVirtual = false, string ret = "void") => new()
    {
        Key = $"{name}():{ret}", Name = name, DeclaringType = type, Display = $"{type}.{name}()", Kind = kind, IsVirtual = isVirtual, ReturnType = ret,
    };

    static ApiType T(string name, params ApiMember[] ms) => new() { Name = name, Display = name, IsInheritable = true, Members = ms.ToList() };

    static ApiSurface Api(string version, params ApiType[] ts) => new() { Package = "Acme", Version = version, Types = ts.ToList() };

    static Usage Use(string type, ApiMember? member, UsageKind kind, string? file = "a.cs", int? line = 1, string? symbol = null) => new()
    {
        TypeName = type, MemberKey = member?.Key, Kind = kind, Location = new SourceLocation { File = file, Line = line, Module = "App", Symbol = symbol },
    };

    static PackageReport Analyze(ApiSurface oldApi, ApiSurface newApi, params Usage[] usages) =>
        UsageMatcher.Analyze(oldApi, newApi, new UsageSet { Usages = usages.ToList() });

    [Fact]
    public void Counts_references_and_distinct_apis_in_the_package_only()
    {
        var run = M("Acme.A", "Run");
        var api = Api("1", T("Acme.A", run));
        var r = Analyze(api, Api("2", T("Acme.A", M("Acme.A", "Run"))),
            Use("Acme.A", run, UsageKind.Member, line: 1),
            Use("Acme.A", run, UsageKind.Member, line: 2),
            Use("Acme.A", null, UsageKind.Type),
            Use("Other.B", null, UsageKind.Type),
            Use("Acme.A", M("Acme.A", "NotInPackage"), UsageKind.Member));
        Assert.Equal(3, r.ReferenceCount);
        Assert.Equal(2, r.UsedApiCount);
        Assert.Empty(r.Used);
        Assert.Equal(("Acme", "1", "2"), (r.Package, r.FromVersion, r.ToVersion));
    }

    [Theory]
    [InlineData("net8.0", "net6.0", "net8.0")]
    [InlineData(null, "net6.0", "net6.0")]
    public void Framework_prefers_the_new_version(string? newFw, string? oldFw, string expected)
    {
        var oldApi = Api("1"); oldApi.Framework = oldFw;
        var newApi = Api("2"); newApi.Framework = newFw;
        Assert.Equal(expected, Analyze(oldApi, newApi).Framework);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Overrides_count_only_for_virtual_members(bool isVirtual, int expected)
    {
        var onError = M("Acme.A", "OnError", isVirtual: isVirtual);
        var r = Analyze(Api("1", T("Acme.A", onError)), Api("2", T("Acme.A")), Use("Acme.A", onError, UsageKind.Override));
        Assert.Equal(expected, r.Used.Count);
        Assert.Equal(expected, r.ReferenceCount);
    }

    [Fact]
    public void Member_use_on_a_newly_obsolete_type_reports_the_type_with_the_members_used()
    {
        var send = M("Acme.A", "Send");
        var getX = M("Acme.A", "get_X", ApiMemberKind.Property);
        var ctor = M("Acme.A", ".ctor", ApiMemberKind.Constructor);
        var field = M("Acme.A", "Count", ApiMemberKind.Field);
        var newType = T("Acme.A", send, getX, ctor, field);
        newType.Deprecated = new Deprecation();
        var r = Analyze(Api("1", T("Acme.A", send, getX, ctor, field)), Api("2", newType),
            Use("Acme.A", send, UsageKind.Member), Use("Acme.A", getX, UsageKind.Member),
            Use("Acme.A", ctor, UsageKind.Member), Use("Acme.A", field, UsageKind.Member));
        var u = Assert.Single(r.Used);
        Assert.Equal("type-deprecated:Acme.A", u.Change.Id);
        Assert.Equal(new[] { "Count", "Send", "X", "constructor" }, u.MembersUsed);
    }

    [Fact]
    public void Type_use_on_a_newly_obsolete_type_is_reported()
    {
        var newType = T("Acme.A");
        newType.Deprecated = new Deprecation();
        var u = Assert.Single(Analyze(Api("1", T("Acme.A")), Api("2", newType), Use("Acme.A", null, UsageKind.Type)).Used);
        Assert.Equal("type-deprecated:Acme.A", u.Change.Id);
        Assert.Empty(u.MembersUsed);
    }

    [Fact]
    public void Member_level_changes_do_not_list_members_used()
    {
        var send = M("Acme.A", "Send");
        var u = Assert.Single(Analyze(Api("1", T("Acme.A", send)), Api("2", T("Acme.A")), Use("Acme.A", send, UsageKind.Member)).Used);
        Assert.Equal("member-removed:Acme.A::Send():void", u.Change.Id);
        Assert.Empty(u.MembersUsed);
    }

    [Fact]
    public void Locations_are_deduplicated_and_sorted_with_unknown_files_last()
    {
        var send = M("Acme.A", "Send");
        var u = Assert.Single(Analyze(Api("1", T("Acme.A", send)), Api("2", T("Acme.A")),
            Use("Acme.A", send, UsageKind.Member, file: null, line: null, symbol: "App.Z"),
            Use("Acme.A", send, UsageKind.Member, "b.cs", 20),
            Use("Acme.A", send, UsageKind.Member, "b.cs", 3),
            Use("Acme.A", send, UsageKind.Member, "a.cs", null),
            Use("Acme.A", send, UsageKind.Member, "b.cs", 20),
            Use("Acme.A", send, UsageKind.Member, file: null, line: null, symbol: "App.Z"),
            Use("Acme.A", send, UsageKind.Member, file: null, line: 7, symbol: "App.Y")).Used);
        Assert.Equal(new[] { "a.cs:", "b.cs:3", "b.cs:20", ":App.Z", ":App.Y" },
            u.Locations.Select(l => l.File != null ? $"{l.File}:{l.Line}" : $":{l.Symbol}"));
    }

    [Fact]
    public void Used_changes_are_ranked_by_severity_then_reach_then_name()
    {
        var a = M("Acme.A", "A"); var b = M("Acme.A", "B"); var c = M("Acme.A", "C"); var w = M("Acme.A", "W");
        var newW = M("Acme.A", "W"); newW.Deprecated = new Deprecation();
        var r = Analyze(Api("1", T("Acme.A", a, b, c, w)), Api("2", T("Acme.A", newW)),
            Use("Acme.A", w, UsageKind.Member, line: 1), Use("Acme.A", w, UsageKind.Member, line: 2), Use("Acme.A", w, UsageKind.Member, line: 3),
            Use("Acme.A", c, UsageKind.Member, line: 1),
            Use("Acme.A", b, UsageKind.Member, line: 1),
            Use("Acme.A", a, UsageKind.Member, line: 1), Use("Acme.A", a, UsageKind.Member, line: 2));
        Assert.Equal(new[] { "A", "B", "C", "W" }, r.Used.Select(u => u.Change.Subject.Split('.')[^1].TrimEnd('(', ')')));
    }
}
