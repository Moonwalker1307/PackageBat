using System.Xml;
using PackageBat.Ecosystems;
using PackageBat.Model;

namespace PackageBat.Analysis;

/// <summary>
/// Line hit counts from one or more Cobertura XML coverage reports (coverlet's
/// "XPlat Code Coverage", ReportGenerator, coverage.py, istanbul, JaCoCo via a
/// converter...). Language-neutral: it only knows files and line numbers.
/// </summary>
public sealed class LineCoverage
{
    /// <summary>One covered source file: line number to hit count.</summary>
    sealed class FileHits(Dictionary<int, int> lines)
    {
        public readonly Dictionary<int, int> Lines = lines;
    }

    /// <summary>
    /// Per report: normalized path (forward slashes) to the file's hits. A file
    /// can sit under several aliases (see Parse). Reports are kept apart because
    /// each one comes from its own checkout, so the same file has a different
    /// absolute path in each.
    /// </summary>
    readonly List<Dictionary<string, FileHits>> _reports = new();
    readonly Dictionary<string, List<FileHits>> _lookupCache = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Sources { get; } = new();

    public static LineCoverage Read(IEnumerable<string> paths)
    {
        var all = new LineCoverage();
        foreach (var path in paths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                all._reports.AddRange(Parse(stream, path)._reports);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new UsageException($"Could not read coverage file {path}: {e.Message}");
            }
            all.Sources.Add(path);
        }
        return all;
    }

    /// <summary>
    /// Parses one Cobertura report. Class filenames are relative to one of the
    /// report's &lt;source&gt; roots (or absolute). Which root is not recorded when
    /// there are several, so the file is filed under every candidate path; a
    /// lookup that then matches more than one file gives up rather than guess.
    /// </summary>
    public static LineCoverage Parse(Stream xml, string name)
    {
        var settings = new XmlReaderSettings
        {
            // ReportGenerator and the original Cobertura write a DOCTYPE. Never fetch or expand it.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        var roots = new List<string>();
        var byName = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        try
        {
            using var r = XmlReader.Create(xml, settings);
            bool inSource = false;
            Dictionary<int, int>? current = null;
            int classDepth = -1;
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "source":
                            inSource = !r.IsEmptyElement;
                            break;
                        case "class":
                            var file = r.GetAttribute("filename");
                            if (string.IsNullOrWhiteSpace(file)) { current = null; break; }
                            file = Normalize(file);
                            if (!byName.TryGetValue(file, out current)) byName[file] = current = new();
                            classDepth = r.Depth;
                            if (r.IsEmptyElement) current = null;
                            break;
                        case "line" when current != null:
                            if (int.TryParse(r.GetAttribute("number"), out var line) && line > 0
                                && long.TryParse(r.GetAttribute("hits"), out var hits) && hits >= 0)
                            {
                                // The same line is listed under <class><lines> and again under <methods>: keep the larger.
                                var h = (int)Math.Min(hits, int.MaxValue);
                                current[line] = current.TryGetValue(line, out var old) ? Math.Max(old, h) : h;
                            }
                            break;
                    }
                }
                else if (r.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && inSource)
                {
                    if (!string.IsNullOrWhiteSpace(r.Value)) roots.Add(Normalize(r.Value.Trim()).TrimEnd('/'));
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (r.LocalName == "source") inSource = false;
                    else if (r.LocalName == "class" && r.Depth == classDepth) { current = null; classDepth = -1; }
                }
            }
        }
        catch (XmlException e)
        {
            throw new UsageException($"{name} is not a valid Cobertura coverage file: {e.Message}");
        }

        var files = new Dictionary<string, FileHits>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, lines) in byName)
        {
            var hits = new FileHits(lines);
            if (IsRooted(file) || roots.Count == 0) files[file] = hits;
            else foreach (var root in roots) files[root + "/" + file] = hits;
        }
        var result = new LineCoverage();
        result._reports.Add(files);
        return result;
    }

    /// <summary>
    /// How often tests ran this line of this file (a path relative to the
    /// repository root), added up over all reports. Null when no report covers
    /// the file, or the line isn't executable code as far as the coverage tool
    /// is concerned.
    /// </summary>
    public int? Hits(string file, int line)
    {
        long? total = null;
        foreach (var hits in Find(file))
            if (hits.Lines.TryGetValue(line, out var n)) total = (total ?? 0) + n;
        return total is { } t ? (int)Math.Min(t, int.MaxValue) : null;
    }

    /// <summary>The file in each report that is this one, skipping reports where the path is ambiguous.</summary>
    List<FileHits> Find(string file)
    {
        file = Normalize(file);
        if (_lookupCache.TryGetValue(file, out var cached)) return cached;
        var found = new List<FileHits>();
        foreach (var report in _reports)
        {
            // Coverage is usually produced in another checkout (another machine, /_/ for
            // deterministic builds, a Windows drive), so match on the trailing path.
            var matches = report
                .Where(f => SameFileBySuffix(f.Key, file))
                .Select(f => f.Value)
                .Distinct()
                .Take(2)
                .ToList();
            if (matches.Count == 1) found.Add(matches[0]);
        }
        return _lookupCache[file] = found;
    }

    static bool SameFileBySuffix(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var (longer, shorter) = a.Length >= b.Length ? (a, b) : (b, a);
        return longer.EndsWith("/" + shorter, StringComparison.OrdinalIgnoreCase);
    }

    static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        while (path.StartsWith("./")) path = path[2..];
        return path;
    }

    static bool IsRooted(string path) =>
        path.StartsWith('/') || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));

    /// <summary>Sets <see cref="SourceLocation.TestHits"/> on every affected location and summarizes.</summary>
    public CoverageSummary Annotate(BlastRadiusReport report)
    {
        var summary = new CoverageSummary { Files = Sources.ToList() };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in report.Packages)
        foreach (var u in p.Used)
        foreach (var l in u.Locations)
        {
            l.TestHits = l.File != null && l.Line is { } line ? Hits(l.File, line) : null;
            if (!seen.Add(l.File != null ? $"{l.File}:{l.Line}" : $"{l.Module}|{l.Symbol}")) continue;
            summary.CallSites++;
            if (l.TestHits is null) summary.Unknown++;
            else if (l.TestHits > 0) summary.Covered++;
            else summary.NotCovered++;
        }
        report.Coverage = summary;
        return summary;
    }
}
