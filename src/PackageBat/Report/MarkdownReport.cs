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

        foreach (var p in report.Packages)
            RenderPackage(sb, p, linkBase);

        sb.AppendLine($"<sub>🦇 PackageBat compared the public API of each package version and matched it against {Plural(report.Scanned.Count, "compiled assembly", "compiled assemblies")} of this repo. It reports changes to APIs your code references; behaviour changes behind an unchanged signature are not detected.</sub>");
        return sb.ToString();
    }

    static void RenderPackage(StringBuilder sb, PackageReport p, string? linkBase)
    {
        sb.AppendLine($"### 🦇 {p.Package} {p.FromVersion} → {p.ToVersion}");
        sb.AppendLine();

        var breaking = p.Used.Where(u => u.Change.Severity == Severity.Breaking).ToList();
        var files = p.Used.SelectMany(u => u.Locations).Select(l => l.File).Where(f => f != null).Distinct().Count();

        if (p.Used.Count == 0)
        {
            sb.AppendLine(p.ReferenceCount == 0
                ? "**Nothing to worry about:** none of your compiled code references this package directly."
                : $"**Looks safe:** this bump doesn't change any of the {Plural(p.UsedApiCount, "API")} your code uses ({Plural(p.ReferenceCount, "reference")}).");
        }
        else
        {
            var head = $"This bump changes {Plural(p.Used.Count, "API")} you use, in {Plural(files, "file")}.";
            var parts = new List<string>();
            if (breaking.Count > 0) parts.Add($"{breaking.Count} breaking");
            var bin = p.Used.Count(u => u.Change.Severity == Severity.BinaryOnly);
            if (bin > 0) parts.Add($"{bin} fixed by recompiling");
            var warn = p.Used.Count(u => u.Change.Severity == Severity.Warning);
            if (warn > 0) parts.Add($"{warn} deprecated");
            sb.AppendLine($"{(breaking.Count > 0 ? "❌" : "⚠️")} **{head}** ({string.Join(", ", parts)})");
            sb.AppendLine();
            sb.AppendLine("| | API | Change | Where you use it |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var u in p.Used)
            {
                var where = u.Locations.Take(MaxLinksInTable).Select(l => Link(l, linkBase)).ToList();
                if (u.Locations.Count > MaxLinksInTable) where.Add($"+{u.Locations.Count - MaxLinksInTable} more");
                sb.AppendLine($"| {Icon(u.Change.Severity)} | `{Escape(u.Change.Subject)}` | {Escape(Describe(u.Change))}{(u.MembersUsed.Count > 0 ? Escape($" (you use {string.Join(", ", u.MembersUsed.Take(5))}{(u.MembersUsed.Count > 5 ? ", …" : "")})") : "")} | {string.Join("<br>", where)} |");
            }
            sb.AppendLine();

            var detailed = p.Used.Where(u => u.Locations.Count > MaxLinksInTable).ToList();
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

    static string Describe(ApiChange c)
    {
        var text = c.Description;
        if (c.Suggestions.Count > 0)
            text += c.Kind == ChangeKind.TypeRemoved ? $". See {string.Join(", ", c.Suggestions)}" : $". Now: {string.Join("; ", c.Suggestions.Take(3))}";
        return text;
    }

    static string Link(SourceLocation l, string? linkBase)
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
