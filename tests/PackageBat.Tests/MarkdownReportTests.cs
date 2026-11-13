using PackageBat.Model;
using PackageBat.Report;
using Xunit;

namespace PackageBat.Tests;

/// <summary>The Markdown is what people see on the pull request, so pin it down exactly.</summary>
public class MarkdownReportTests
{
    const string LinkBase = "https://gh/o/r/blob/sha/";

    static SourceLocation L(string? file, int? line = null, string? symbol = null) =>
        new() { File = file, Line = line, Module = "App", Symbol = symbol };

    static ApiChange C(string id, Severity s, string subject, string description, ChangeKind kind = ChangeKind.SignatureChanged, params string[] suggestions) =>
        new() { Id = id, Severity = s, Subject = subject, Description = description, Kind = kind, Suggestions = suggestions.ToList() };

    static UsedChange U(ApiChange c, IEnumerable<SourceLocation> locations, params string[] members) =>
        new() { Change = c, Locations = locations.ToList(), MembersUsed = members.ToList() };

    static PackageReport Pkg(params UsedChange[] used)
    {
        var p = new PackageReport { Package = "Acme", FromVersion = "1.0.0", ToVersion = "2.0.0", ReferenceCount = 3, UsedApiCount = 2 };
        p.Used.AddRange(used);
        p.AllChanges.AddRange(used.Select(u => u.Change));
        return p;
    }

    static string Render(PackageReport p, string? linkBase = LinkBase, int scanned = 1) =>
        MarkdownReport.Render(new BlastRadiusReport { Packages = { p }, Scanned = Enumerable.Range(0, scanned).Select(i => $"{i}.dll").ToList() }, linkBase)
            .ReplaceLineEndings("\n");

    static readonly ApiChange Breaking = C("b", Severity.Breaking, "Client.Send(string)", "Signature changed");

    [Fact]
    public void Full_report_matches_exactly()
    {
        var breaking = C("b", Severity.Breaking, "Client.Send(string)", "Signature changed", ChangeKind.SignatureChanged,
            "Client.Send(byte[])", "Client.Send(Stream)", "Client.Send(Memory)", "Client.Send(Span)");
        var moved = C("m", Severity.Breaking, "Acme.Legacy", "Type moved or was replaced", ChangeKind.TypeRemoved, "Acme.Old.Legacy", "Acme.New.Legacy");
        var bin = C("o", Severity.BinaryOnly, "Client.Get(string)", "Recompile");
        var warn = C("w", Severity.Warning, "Acme.Old`Thing", "Deprecated: a|b");
        var p = Pkg(
            U(breaking, new[] { L("src/App.cs", 10), L("src/App.cs", 20), L("src/My Dir/B.cs", 3), L("src/C.cs"), L(null, null, "App.Run") }),
            U(moved, new[] { L("src/App.cs", 30) }, "A", "B", "C", "D", "E", "F"),
            U(bin, new[] { L("/abs/path.cs", 4) }),
            U(warn, new[] { L("src/App.cs", 40) }, "X"));
        p.AddedApiCount = 7;
        p.Notes.Add("Note one");
        for (int i = 0; i < 42; i++)
            p.AllChanges.Add(C($"u{i}", i == 0 ? Severity.BinaryOnly : Severity.Breaking, $"U{i:00}", "Removed", ChangeKind.MemberRemoved));

        const string head = """
            <!-- packagebat-report -->
            ### 🦇 Acme 1.0.0 → 2.0.0

            ❌ **This bump changes 4 APIs you use, in 4 files.** (2 breaking, 1 fixed by recompiling, 1 deprecated)

            | | API | Change | Where you use it |
            |---|---|---|---|
            | ❌ | `Client.Send(string)` | Signature changed. Now: Client.Send(byte[]); Client.Send(Stream); Client.Send(Memory) | [src/App.cs:10](https://gh/o/r/blob/sha/src/App.cs#L10)<br>[src/App.cs:20](https://gh/o/r/blob/sha/src/App.cs#L20)<br>[src/My Dir/B.cs:3](https://gh/o/r/blob/sha/src/My%20Dir/B.cs#L3)<br>+2 more |
            | ❌ | `Acme.Legacy` | Type moved or was replaced. See Acme.Old.Legacy, Acme.New.Legacy (you use A, B, C, D, E, …) | [src/App.cs:30](https://gh/o/r/blob/sha/src/App.cs#L30) |
            | 🔁 | `Client.Get(string)` | Recompile | `/abs/path.cs:4` |
            | ⚠️ | `Acme.Old'Thing` | Deprecated: a\|b (you use X) | [src/App.cs:40](https://gh/o/r/blob/sha/src/App.cs#L40) |

            <details><summary>All locations</summary>

            **`Client.Send(string)`**
            - [src/App.cs:10](https://gh/o/r/blob/sha/src/App.cs#L10)
            - [src/App.cs:20](https://gh/o/r/blob/sha/src/App.cs#L20)
            - [src/My Dir/B.cs:3](https://gh/o/r/blob/sha/src/My%20Dir/B.cs#L3)
            - [src/C.cs](https://gh/o/r/blob/sha/src/C.cs)
            - `App.Run` (no source info)

            </details>


            <details><summary>Other API changes in this bump that your code doesn't touch (42 changed or removed, 7 added)</summary>


            """;
        var listed = string.Concat(Enumerable.Range(0, 40).Select(i => $"- {(i == 0 ? "🔁" : "❌")} `U{i:00}`: Removed\n"));
        const string tail = """
            - …and 2 more (see the JSON report)

            </details>
            > Note one

            <sub>🦇 PackageBat compared the public API of each package version and matched it against 2 compiled assemblies of this repo. It reports changes to APIs your code references; behaviour changes behind an unchanged signature are not detected.</sub>

            """;

        Assert.Equal(head + listed + tail, Render(p, scanned: 2));
    }

    [Fact]
    public void No_bumps()
    {
        var md = MarkdownReport.Render(new BlastRadiusReport(), LinkBase).ReplaceLineEndings("\n");
        Assert.Equal("<!-- packagebat-report -->\n### 🦇 PackageBat\n\nNo dependency version changes found in this PR.\n", md);
    }

    [Fact]
    public void Package_not_referenced()
    {
        var p = Pkg();
        p.ReferenceCount = 0;
        var md = Render(p);
        Assert.Contains("**Nothing to worry about:** none of your compiled code references this package directly.", md);
        Assert.DoesNotContain("<details>", md);
        Assert.Contains("against 1 compiled assembly of this repo", md);
    }

    [Theory]
    [InlineData(1, 1, "1 API your code uses (1 reference)")]
    [InlineData(2, 3, "2 APIs your code uses (3 references)")]
    public void Package_used_but_unaffected(int apis, int refs, string text)
    {
        var p = Pkg();
        p.UsedApiCount = apis;
        p.ReferenceCount = refs;
        Assert.Contains($"**Looks safe:** this bump doesn't change any of the {text}.", Render(p));
    }

    [Fact]
    public void Only_additions_get_their_own_section()
    {
        var p = Pkg();
        p.AddedApiCount = 1;
        var md = Render(p);
        Assert.Contains("(0 changed or removed, 1 added)</summary>\n\nOnly additions.\n", md);
    }

    [Fact]
    public void Unused_changes_are_listed_up_to_the_limit_without_overflow_line()
    {
        var p = Pkg();
        for (int i = 0; i < 40; i++) p.AllChanges.Add(C($"u{i}", Severity.Breaking, $"U{i}", "Removed"));
        var md = Render(p);
        Assert.Contains("- ❌ `U39`: Removed", md);
        Assert.DoesNotContain("…and", md);
        Assert.DoesNotContain("Only additions.", md);
    }

    [Fact]
    public void Used_changes_are_not_repeated_in_the_unused_list()
    {
        var md = Render(Pkg(U(Breaking, new[] { L("a.cs", 1) })));
        Assert.DoesNotContain("Other API changes", md);
    }

    [Fact]
    public void Without_breaking_changes_the_header_is_a_warning()
    {
        var md = Render(Pkg(
            U(C("o", Severity.BinaryOnly, "A", "x"), new[] { L("a.cs", 1) }),
            U(C("w", Severity.Warning, "B", "y"), new[] { L("a.cs", 2) })));
        Assert.Contains("⚠️ **This bump changes 2 APIs you use, in 1 file.** (1 fixed by recompiling, 1 deprecated)", md);
    }

    [Fact]
    public void Only_breaking_changes_lists_only_breaking()
    {
        var md = Render(Pkg(U(Breaking, new[] { L("a.cs", 1) })));
        Assert.Contains("❌ **This bump changes 1 API you use, in 1 file.** (1 breaking)\n", md);
    }

    [Fact]
    public void Three_locations_fit_in_the_table()
    {
        var md = Render(Pkg(U(Breaking, new[] { L("a.cs", 1), L("a.cs", 2), L("a.cs", 3) }, "A", "B", "C", "D", "E")));
        Assert.DoesNotContain("more", md);
        Assert.DoesNotContain("All locations", md);
        Assert.Contains("(you use A, B, C, D, E) |", md);
    }

    [Fact]
    public void Without_link_base_locations_are_plain_text()
    {
        var md = Render(Pkg(U(Breaking, new[] { L("src/a.cs", 1), L("src/b.cs") })), linkBase: null);
        Assert.Contains("| `src/a.cs:1`<br>`src/b.cs` |", md);
        Assert.DoesNotContain("](", md);
    }

    [Fact]
    public void Location_without_file_falls_back_to_module()
    {
        var md = Render(Pkg(U(Breaking, new[] { L(null) })));
        Assert.Contains("| `App` (no source info) |", md);
        Assert.Contains("in 0 files.", md);
    }
}
