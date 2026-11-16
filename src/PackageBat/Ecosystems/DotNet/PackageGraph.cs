using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>A package dependency from a .nuspec: the id and the lowest version it allows (what NuGet restores).</summary>
internal sealed record NuGetDependency(string Id, string MinVersion);

/// <summary>Reads the dependencies a .nuspec declares for one target framework.</summary>
internal static class Nuspec
{
    /// <summary>
    /// The dependencies of the group NuGet would pick for <paramref name="targetFramework"/>
    /// (the newest compatible one; a group without a framework is the fallback). Null
    /// framework means the newest .NET build, as in FrameworkSelector. Malformed XML gives
    /// an empty list: a package we can't read has no dependencies to follow.
    /// </summary>
    public static IReadOnlyList<NuGetDependency> Dependencies(string? nuspecXml, string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(nuspecXml)) return Array.Empty<NuGetDependency>();
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(nuspecXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return Array.Empty<NuGetDependency>(); }

        var deps = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "dependencies");
        if (deps == null) return Array.Empty<NuGetDependency>();

        var groups = deps.Elements().Where(e => e.Name.LocalName == "group").ToList();
        IEnumerable<XElement> chosen;
        if (groups.Count == 0)
        {
            chosen = deps.Elements();
        }
        else
        {
            var best = groups
                .Select(g => (Group: g, Score: (string?)g.Attribute("targetFramework") is { Length: > 0 } tf
                    ? FrameworkSelector.Score(ShortFramework(tf), targetFramework)
                    : 1))
                .Where(g => g.Score > 0)
                .OrderByDescending(g => g.Score)
                .FirstOrDefault();
            if (best.Group == null) return Array.Empty<NuGetDependency>();
            chosen = best.Group.Elements();
        }

        var result = new List<NuGetDependency>();
        foreach (var d in chosen)
        {
            if (d.Name.LocalName != "dependency") continue;
            var id = ((string?)d.Attribute("id"))?.Trim();
            if (string.IsNullOrEmpty(id) || MinVersion((string?)d.Attribute("version")) is not { } min) continue;
            if (result.All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                result.Add(new NuGetDependency(id, min));
        }
        return result;
    }

    /// <summary>"1.2" -> "1.2.0", "[1.2, 2.0)" -> "1.2.0", "(,2.0]" -> null (no lower bound).</summary>
    internal static string? MinVersion(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var v = range.Trim();
        if (v.StartsWith('[') || v.StartsWith('('))
        {
            var comma = v.IndexOf(',');
            v = comma >= 0 ? v[1..comma] : v.Trim('[', ']', '(', ')');
        }
        v = v.Trim();
        if (v.Length == 0 || v.IndexOfAny(new[] { '*', '$', ' ', ')', ']' }) >= 0) return null;
        return NuGetFetcher.NormalizeVersion(v);
    }

    /// <summary>".NETFramework4.6.2" -> "net462", ".NETStandard2.0" -> "netstandard2.0", "net8.0" stays.</summary>
    internal static string ShortFramework(string tf)
    {
        tf = tf.Trim();
        static string Digits(string v) => v.Replace(".", "");
        if (tf.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase)) return "net" + Digits(tf[".NETFramework".Length..]);
        if (tf.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase)) return "netstandard" + tf[".NETStandard".Length..];
        if (tf.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase)) return "netcoreapp" + tf[".NETCoreApp".Length..];
        return tf.ToLowerInvariant();
    }
}

/// <summary>Orders NuGet version strings: numeric parts by value, a release after its prereleases.</summary>
internal static class NuGetVersion
{
    public static int Compare(string a, string b)
    {
        a = NuGetFetcher.NormalizeVersion(a);
        b = NuGetFetcher.NormalizeVersion(b);
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        var c = CompareParts(coreA.Split('.'), coreB.Split('.'), padWithZero: true);
        if (c != 0) return c;
        if (preA == null || preB == null) return preA == null ? (preB == null ? 0 : 1) : -1;
        return CompareParts(preA.Split('.'), preB.Split('.'), padWithZero: false);
    }

    public static bool IsPrerelease(string v) => NuGetFetcher.NormalizeVersion(v).Contains('-');

    static (string Core, string? Pre) Split(string v)
    {
        var dash = v.IndexOf('-');
        return dash >= 0 ? (v[..dash], v[(dash + 1)..]) : (v, null);
    }

    static int CompareParts(string[] a, string[] b, bool padWithZero)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length || i >= b.Length)
            {
                if (padWithZero) { var rest = i >= a.Length ? b[i] : a[i]; if (long.TryParse(rest, out var n) && n == 0) continue; }
                return i >= a.Length ? -1 : 1;
            }
            var na = long.TryParse(a[i], out var x);
            var nb = long.TryParse(b[i], out var y);
            int c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>
/// The package versions the consumer actually restored, from the
/// obj/project.assets.json files its build left behind. This is the real
/// dependency graph (with NuGet's conflict resolution done), so it is the
/// best "before" version for a package the consumer gets transitively.
/// </summary>
internal static class ProjectAssets
{
    const long MaxFileSize = 256L * 1024 * 1024;

    /// <summary>Package id -> highest restored version across every project found.</summary>
    public static Dictionary<string, string> Read(IReadOnlyList<string> consumerPaths, TextWriter log)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Find(consumerPaths))
        {
            try
            {
                if (new FileInfo(file).Length > MaxFileSize) continue;
                using var s = File.OpenRead(file);
                using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { MaxDepth = 64 });
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("libraries", out var libs) || libs.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var lib in libs.EnumerateObject())
                {
                    if (lib.Value.ValueKind != JsonValueKind.Object || !lib.Value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "package")
                        continue;
                    var slash = lib.Name.IndexOf('/');
                    if (slash <= 0 || slash == lib.Name.Length - 1) continue;
                    var (id, version) = (lib.Name[..slash], lib.Name[(slash + 1)..]);
                    if (!result.TryGetValue(id, out var have) || NuGetVersion.Compare(version, have) > 0)
                        result[id] = version;
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"warning: could not read {file} ({e.Message}).");
            }
        }
        return result;
    }

    static IEnumerable<string> Find(IReadOnlyList<string> consumerPaths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sep = Path.DirectorySeparatorChar;
        foreach (var p in consumerPaths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "project.assets.json", SearchOption.AllDirectories).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files)
                    if (f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}.git{sep}") && !f.Contains($"{sep}node_modules{sep}") && seen.Add(f))
                        yield return f;
            }
            else if (File.Exists(p))
            {
                // A built assembly: bin/<Configuration>/<tfm>/App.dll sits a few levels below its project.
                var dir = Path.GetDirectoryName(Path.GetFullPath(p));
                for (int i = 0; i < 5 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                {
                    var f = Path.Combine(dir, "obj", "project.assets.json");
                    if (File.Exists(f)) { if (seen.Add(f)) yield return f; break; }
                }
            }
        }
    }
}
