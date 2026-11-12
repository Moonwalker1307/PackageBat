using System.Diagnostics;
using System.Xml.Linq;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// Finds NuGet version changes between two git revisions by reading the
/// project files at both, so it works the same for Dependabot, Renovate and
/// hand-made PRs. Understands PackageReference, central package management
/// (PackageVersion in Directory.Packages.props) and packages.config.
/// </summary>
internal static class BumpDetector
{
    public static IReadOnlyList<PackageBump> Detect(string repoRoot, string baseRef, string headRef)
    {
        var files = Git(repoRoot, "diff", "--name-only", $"{baseRef}", headRef)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPackageFile)
            .ToList();

        var bumps = new Dictionary<string, PackageBump>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var before = ReadVersions(TryGit(repoRoot, "show", $"{baseRef}:{file}"));
            var after = ReadVersions(TryGit(repoRoot, "show", $"{headRef}:{file}"));
            foreach (var (id, to) in after)
            {
                if (!before.TryGetValue(id, out var from) || from == to) continue;
                bumps.TryAdd(id, new PackageBump { Ecosystem = "dotnet", Package = id, From = from, To = to, File = file });
            }
        }
        return bumps.Values.OrderBy(b => b.Package, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsPackageFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages.config", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Package id -> exact version, for every pinned version in one file. Ranges, wildcards and $(Properties) are skipped.</summary>
    internal static Dictionary<string, string> ReadVersions(string? xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return result;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var e in doc.Descendants())
        {
            string? id, version;
            switch (e.Name.LocalName)
            {
                case "PackageReference":
                case "PackageVersion":
                case "GlobalPackageReference":
                    id = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update");
                    version = (string?)e.Attribute("Version") ?? (string?)e.Attribute("VersionOverride")
                        ?? e.Elements().FirstOrDefault(c => c.Name.LocalName is "Version" or "VersionOverride")?.Value;
                    break;
                case "package":
                    id = (string?)e.Attribute("id");
                    version = (string?)e.Attribute("version");
                    break;
                default:
                    continue;
            }
            if (id == null || version == null) continue;
            version = version.Trim();
            if (version.StartsWith('[') && version.EndsWith(']') && !version.Contains(',')) version = version[1..^1];
            if (version.Length == 0 || version.IndexOfAny(new[] { '$', '*', ',', '(', ')', '[', ']' }) >= 0) continue;
            result[id] = version;
        }
        return result;
    }

    static string? TryGit(string repo, params string[] args)
    {
        try { return Git(repo, args); } catch (UsageException) { return null; }
    }

    static string Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UsageException("Could not start git.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new UsageException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");
        return stdout;
    }
}
