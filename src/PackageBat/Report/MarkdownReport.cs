using System.Text;
using PackageBat.Model;

namespace PackageBat.Report;

/// <summary>Renders the report as GitHub-flavoured Markdown for a PR comment or job summary.</summary>
public static class MarkdownReport
{
    public const string Marker = "<!-- packagebat-report -->";
    const int MaxLinksInTable = 3;
    const int MaxUnusedListed = 40;

    /// <param name="linkBase">e.g. https://github.com/owner/repo/blob/&lt;sha&gt;; file paths are appended.</param>
    public static string Render(BlastRadiusReport report, string? linkBase)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Marker);

        if (report.Packages.Count == 0)
        {
            sb.AppendLine("### 🦇 PackageBat");
            sb.AppendLine();
            sb.AppendLine("No dependency version changes found in this PR.");
            return sb.ToString();
        }

        var coverage = report.Coverage != null;
        // A dependency of a dependency with nothing to report gets one line, not a section.
        static bool Quiet(PackageReport p) => p.Via != null && p.Used.Count == 0 && p.Notes.Count == 0;
        foreach (var p in report.Packages.Where(p => !Quiet(p)))
        {
            var loud = report.Packages.Where(d => d.Via != null && d.Via.Equals(p.Package, StringComparison.OrdinalIgnoreCase) && !Quiet(d)).ToList();
            RenderPackage(sb, p, linkBase, coverage, loud);
        }

        var quiet = report.Packages.Where(Quiet).ToList();
        if (quiet.Count + report.Unreferenced.Count > 0)
        {
            sb.AppendLine($"<details><summary>{Plural(quiet.Count + report.Unreferenced.Count, "dependency", "dependencies")} of these packages also change version, with nothing to report</summary>");
            sb.AppendLine();
            foreach (var p in quiet)
                sb.AppendLine($"- {Escape(p.Package)} {Escape(p.FromVersion)} → {Escape(p.ToVersion)} (via {Escape(p.Via!)}): {(p.ReferenceCount == 0 ? "your compiled code doesn't reference it" : $"no changes to the {Plural(p.UsedApiCount, "API")} your code uses")}");
            foreach (var b in report.Unreferenced)
                sb.AppendLine($"- {Escape(b.Package)} {Escape(b.From)} → {Escape(b.To)} (via {Escape(b.Via ?? "?")}): your compiled code doesn't reference it");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        var tests = report.Coverage is { } c
            ? $" Test coverage is from {Plural(c.Files.Count, "coverage file")}: a covered line ran during the tests, which doesn't prove a test checks what it does."
            : "";
        sb.AppendLine($"<sub>🦇 PackageBat compared the public API of each package version and matched it against {Plural(report.Scanned.Count, "compiled assembly", "compiled assemblies")} of this repo. It reports changes to APIs your code references; behaviour changes behind an unchanged signature are not detected.{tests}</sub>");
        return sb.ToString();
    }

    static void RenderPackage(StringBuilder sb, PackageReport p, string? linkBase, bool coverage, IReadOnlyList<PackageReport> dependencies)
    {
        sb.AppendLine($"### 🦇 {p.Package} {p.FromVersion} → {p.ToVersion}{(p.Via != null ? $" (via {p.Via})" : "")}");
        sb.AppendLine();
        if (p.Via != null)
        {
            sb.AppendLine($"Your projects don't set this package's version: the new {p.Via} needs {p.Package} {p.ToVersion}, so it changes with that bump.");
            sb.AppendLine();
        }

        if (p.Used.FirstOrDefault(u => u.Change.Kind == ChangeKind.LicenseChanged)?.Change is { } license)
        {
            sb.AppendLine($"⚠️ **The licence changed: {Escape(license.Subject)}.** It applies to the whole package, whatever your code uses, so check that the new terms still allow how you use and ship it.");
            sb.AppendLine($"<sub>{Escape(license.Description)}.</sub>");
            sb.AppendLine();
        }

        if (p.Used.FirstOrDefault(u => u.Change.Kind == ChangeKind.VersionMismatch)?.Change is { } mismatch)
        {
            sb.AppendLine($"⚠️ **Not every project moves to {Escape(p.ToVersion)}:** {Escape(mismatch.Description)}. Projects that end up in one app get only one version at run time, so code built against the other one can fail there.");
            sb.AppendLine();
        }

        var runtime = p.Used.Where(u => u.Change.Kind == ChangeKind.RuntimeDependencyMissing).ToList();
        foreach (var r in runtime)
        {
            var c = r.Change;
            var add = c.Suggestions.FirstOrDefault()?.Split(' ', 2) is [var id, var version]
                ? $"If your code uses this feature, add the `{Escape(id)}` package ({Escape(version)})."
                : "If your code uses this feature, add the package that provides it.";
            sb.AppendLine($"⚠️ **{p.Package} {p.ToVersion} loads `{Escape(c.Subject)}` by name at run time, but nothing in your dependency graph provides it.** {add} The build won't tell you: it only fails when that code runs.");
            sb.AppendLine($"<sub>{Escape(c.Description)}.</sub>");
            sb.AppendLine();
        }

        var apiUsed = p.Used.Where(u => !u.Change.Kind.IsPackageLevel()).ToList();
        var other = p.Used.Count - apiUsed.Count; // findings above that aren't about the API
        var breaking = apiUsed.Where(u => u.Change.Severity == Severity.Breaking).ToList();
        var files = apiUsed.SelectMany(u => u.Locations).Select(l => l.File).Where(f => f != null).Distinct().Count();

        if (apiUsed.Count == 0)
        {
            sb.AppendLine(p.ReferenceCount == 0
                ? other > 0 ? "None of your compiled code references this package directly." : "**Nothing to worry about:** none of your compiled code references this package directly."
                : other > 0 ? $"The API changes don't touch any of the {Plural(p.UsedApiCount, "API")} your code uses ({Plural(p.ReferenceCount, "reference")})."
                : $"**Looks safe:** this bump doesn't change any of the {Plural(p.UsedApiCount, "API")} your code uses ({Plural(p.ReferenceCount, "reference")}).");
        }
        else
        {
            var head = $"This bump changes {Plural(apiUsed.Count, "API")} you use, in {Plural(files, "file")}.";
            var parts = new List<string>();
            if (breaking.Count > 0) parts.Add($"{breaking.Count} breaking");
            var bin = apiUsed.Count(u => u.Change.Severity == Severity.BinaryOnly);
            if (bin > 0) parts.Add($"{bin} fixed by recompiling");
            var warn = apiUsed.Count(u => u.Change.Severity == Severity.Warning);
            if (warn > 0) parts.Add($"{warn} deprecated");
            sb.AppendLine($"{(breaking.Count > 0 ? "❌" : "⚠️")} **{head}** ({string.Join(", ", parts)})");
            sb.AppendLine();
            if (coverage)
            {
                sb.AppendLine(CoverageLine(p));
                sb.AppendLine();
            }
            sb.AppendLine(coverage ? "| | API | Change | Where you use it | Run by tests |" : "| | API | Change | Where you use it |");
            sb.AppendLine(coverage ? "|---|---|---|---|---|" : "|---|---|---|---|");
            foreach (var u in apiUsed)
            {
                var where = u.Locations.Take(MaxLinksInTable).Select(l => Link(l, linkBase)).ToList();
                if (u.Locations.Count > MaxLinksInTable) where.Add($"+{u.Locations.Count - MaxLinksInTable} more");
                sb.AppendLine($"| {Icon(u.Change.Severity)} | `{Escape(u.Change.Subject)}` | {Escape(Describe(u.Change))}{(u.MembersUsed.Count > 0 ? Escape($" (you use {string.Join(", ", u.MembersUsed.Take(5))}{(u.MembersUsed.Count > 5 ? ", …" : "")})") : "")} | {string.Join("<br>", where)} |{(coverage ? $" {TestsCell(u.Locations)} |" : "")}");
            }
            sb.AppendLine();

            var detailed = apiUsed.Where(u => u.Locations.Count > MaxLinksInTable).ToList();
            if (detailed.Count > 0)
            {
                sb.AppendLine("<details><summary>All locations</summary>");
                sb.AppendLine();
                foreach (var u in detailed)
                {
                    sb.AppendLine($"**`{Escape(u.Change.Subject)}`**");
                    foreach (var l in u.Locations) sb.AppendLine($"- {Link(l, linkBase)}");
                    sb.AppendLine();
                }
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        if (dependencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ **Its new version also updates {string.Join(", ", dependencies.Select(d => $"{d.Package} {d.FromVersion} → {d.ToVersion}"))}: see below.**");
        }

        var usedIds = p.Used.Select(u => u.Change.Id).ToHashSet();
        var unused = p.AllChanges.Where(c => !usedIds.Contains(c.Id)).ToList();
        if (unused.Count > 0 || p.AddedApiCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"<details><summary>Other API changes in this bump that your code doesn't touch ({unused.Count} changed or removed, {p.AddedApiCount} added)</summary>");
            sb.AppendLine();
            if (unused.Count == 0) sb.AppendLine("Only additions.");
            foreach (var c in unused.Take(MaxUnusedListed))
                sb.AppendLine($"- {Icon(c.Severity)} `{Escape(c.Subject)}`: {Escape(Describe(c))}");
            if (unused.Count > MaxUnusedListed) sb.AppendLine($"- …and {unused.Count - MaxUnusedListed} more (see the JSON report)");
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        foreach (var n in p.Notes) sb.AppendLine($"> {n}");
        sb.AppendLine();
    }

    static string CoverageLine(PackageReport p)
    {
        var sites = p.Used.SelectMany(u => u.Locations)
            .GroupBy(l => l.File != null ? $"{l.File}:{l.Line}" : $"{l.Module}|{l.Symbol}")
            .Select(g => g.First().TestHits)
            .ToList();
        int covered = sites.Count(h => h > 0), notCovered = sites.Count(h => h == 0), unknown = sites.Count(h => h == null);
        if (covered + notCovered == 0)
            return "🧪 **Test coverage unknown:** the coverage file doesn't include any of these lines. Check that it comes from this repository's tests.";
        var text = notCovered == 0
            ? $"🧪 **Your tests run {(covered == 1 ? "the affected call site" : $"all {covered} affected call sites")}{(unknown > 0 ? " with coverage data" : "")}.**"
            : covered > 0 ? $"🧪 **{notCovered} of {covered + notCovered} affected call sites {(notCovered == 1 ? "is" : "are")} never run by your tests.**"
            : notCovered == 1 ? "🧪 **The affected call site is never run by your tests.**"
            : $"🧪 **None of the {notCovered} affected call sites are run by your tests.**";
        if (unknown > 0) text += $" {Plural(unknown, "call site has", "call sites have")} no coverage data.";
        return text;
    }

    static string TestsCell(List<SourceLocation> locations)
    {
        int covered = locations.Count(l => l.TestHits > 0), known = locations.Count(l => l.TestHits != null), unknown = locations.Count - known;
        if (known == 0) return "no data";
        var icon = covered == known ? "✅" : covered == 0 ? "❌" : "⚠️";
        return $"{icon} {covered} of {known}{(unknown > 0 ? $" ({unknown} no data)" : "")}";
    }

    static string Describe(ApiChange c)
    {
        var text = c.Description;
        if (c.Suggestions.Count > 0)
            text += c.Kind == ChangeKind.TypeRemoved ? $". See {string.Join(", ", c.Suggestions)}" : $". Now: {string.Join("; ", c.Suggestions.Take(3))}";
        return text;
    }

    static string Link(SourceLocation l, string? linkBase) => LinkOnly(l, linkBase) + (l.TestHits == 0 ? " (untested)" : "");

    static string LinkOnly(SourceLocation l, string? linkBase)
    {
        if (l.File == null) return $"`{l.Symbol ?? l.Module}` (no source info)";
        var label = $"{l.File}{(l.Line is { } n ? $":{n}" : "")}";
        if (linkBase == null || Path.IsPathRooted(l.File)) return $"`{label}`";
        return $"[{label}]({linkBase.TrimEnd('/')}/{Uri.EscapeDataString(l.File).Replace("%2F", "/")}{(l.Line is { } line ? $"#L{line}" : "")})";
    }

    static string Icon(Severity s) => s switch
    {
        Severity.Breaking => "❌",
        Severity.BinaryOnly => "🔁",
        _ => "⚠️",
    };

    static string Escape(string s) => s.Replace("|", "\\|").Replace("`", "'").Replace("\n", " ");

    static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}
