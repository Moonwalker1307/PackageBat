using System.Diagnostics;
using System.Xml.Linq;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// Finds NuGet version changes between two git revisions by reading the
/// project files at both, so it works the same for Dependabot, Renovate and
/// hand-made PRs. Understands PackageReference, central package management
/// (PackageVersion in Directory.Packages.props) and packages.config, and
/// versions set through MSBuild properties (<c>Version="$(FooVersion)"</c>)
/// defined in the project file itself, a Directory.Build.props (or any
/// .props/.targets) above it, or elsewhere in the repository when every
/// definition there agrees. A change that only edits such a property counts as a
/// bump of every package whose version uses it. Packages the change adds are
/// listed after the bumps, marked Added. Each bump also lists the other project
/// files that still set a different version at the head revision (Elsewhere),
/// such as a project the PR didn't update.
/// </summary>
internal static class BumpDetector
{
    public static IReadOnlyList<PackageBump> Detect(string repoRoot, string baseRef, string headRef)
    {
        var files = Git(repoRoot, "diff", "--name-only", $"{baseRef}", headRef)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPackageFile)
            .ToList();
        var before = new Revision(repoRoot, baseRef);
        var after = new Revision(repoRoot, headRef);

        // A property the change edits moves every version that uses it, even in files the change didn't touch.
        var changedProperties = files
            .SelectMany(f => ChangedKeys(ReadProperties(before.Text(f)), ReadProperties(after.Text(f))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (changedProperties.Count > 0)
        {
            // ...and every property defined in terms of one: <FooVersion>$(FooMajor).4.0</FooVersion>.
            var definitions = before.PackageFiles.Union(after.PackageFiles)
                .SelectMany(f => ReadProperties(before.Text(f)).Concat(ReadProperties(after.Text(f))))
                .ToList();
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var (name, value) in definitions)
                    if (!changedProperties.Contains(name) && UsesAny(value, changedProperties))
                        grew |= changedProperties.Add(name);
            }
            var users = before.PackageFiles.Union(after.PackageFiles)
                .Where(f => UsesAny(before.Text(f), changedProperties) || UsesAny(after.Text(f), changedProperties));
            files = files.Union(users).ToList();
        }

        var bumps = new Dictionary<string, PackageBump>(StringComparer.OrdinalIgnoreCase);
        var added = new Dictionary<string, PackageBump>(StringComparer.OrdinalIgnoreCase);
        var hadBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var from = before.Versions(file);
            var to = after.Versions(file);
            hadBefore.UnionWith(from.Keys);
            foreach (var (id, version) in to)
            {
                if (!from.TryGetValue(id, out var old))
                {
                    added.TryAdd(id, new PackageBump { Ecosystem = "dotnet", Package = id, From = "", To = version, File = file, Added = true });
                    continue;
                }
                if (old == version) continue;
                bumps.TryAdd(id, new PackageBump { Ecosystem = "dotnet", Package = id, From = old, To = version, File = file });
            }
        }
        if (bumps.Count > 0) AddElsewhere(after, bumps.Values);

        // A package that only moved between files (say into Directory.Packages.props) wasn't added.
        return bumps.Values
            .Concat(added.Values.Where(a => !hadBefore.Contains(a.Package) && !bumps.ContainsKey(a.Package)))
            .OrderBy(b => b.Added == true).ThenBy(b => b.Package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// For each bump, the project files at the head revision (changed or not)
    /// that set the package to a version other than the one it's bumped to.
    /// </summary>
    static void AddElsewhere(Revision head, IEnumerable<PackageBump> bumps)
    {
        var all = head.PackageFiles.Order(StringComparer.Ordinal).ToList();
        foreach (var bump in bumps)
            foreach (var file in all)
                if (head.Versions(file).TryGetValue(bump.Package, out var v) && !string.Equals(v, bump.To, StringComparison.OrdinalIgnoreCase))
                    (bump.Elsewhere ??= new()).Add(new PinnedVersion { File = file, Version = v });
    }

    static IEnumerable<string> ChangedKeys(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y);

    static bool UsesAny(string? xml, HashSet<string> properties) =>
        xml != null && xml.Contains("$(", StringComparison.Ordinal)
        && PropertyReference.Matches(xml).Any(m => properties.Contains(m.Groups[1].Value));

    static bool IsPackageFile(string path) => IsProjectFile(path) || IsImportFile(path)
        || Path.GetFileName(path).Equals("packages.config", StringComparison.OrdinalIgnoreCase);

    static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files that MSBuild imports into projects: where shared properties (and central versions) live.</summary>
    static bool IsImportFile(string path) =>
        path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase);

    /// <summary>Package id -> exact version, for every pinned version in one file. Ranges, wildcards and unresolved $(Properties) are skipped.</summary>
    internal static Dictionary<string, string> ReadVersions(string? xml) => ReadVersions(xml, null);

    /// <param name="property">Value of an MSBuild property, or null when it isn't known. Versions that use an unknown property are skipped.</param>
    internal static Dictionary<string, string> ReadVersions(string? xml, Func<string, string?>? property)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Parse(xml) is not { } doc) return result;

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
            if (property != null) version = Expand(version, property);
            if (version == null) continue;
            version = version.Trim();
            if (version.StartsWith('[') && version.EndsWith(']') && !version.Contains(',')) version = version[1..^1];
            if (version.Length == 0 || version.IndexOfAny(new[] { '$', '*', ',', '(', ')', '[', ']' }) >= 0) continue;
            result[id] = version;
        }
        return result;
    }

    /// <summary>
    /// The MSBuild properties one file sets: every element under a PropertyGroup.
    /// A conditional definition counts only as a default (<c>Condition="'$(Foo)' == ''"</c>), and only
    /// when the file has no unconditional one; any other condition depends on the build, so it is skipped.
    /// </summary>
    internal static Dictionary<string, string> ReadProperties(string? xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Parse(xml) is not { } doc) return result;
        foreach (var group in doc.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            var groupCondition = (string?)group.Attribute("Condition");
            foreach (var p in group.Elements())
            {
                var name = p.Name.LocalName;
                if (p.HasElements) continue;
                var condition = (string?)p.Attribute("Condition") ?? groupCondition;
                if (string.IsNullOrWhiteSpace(condition)) result[name] = p.Value.Trim();
                else if (IsDefaultCondition(condition, name)) defaults.TryAdd(name, p.Value.Trim());
            }
        }
        foreach (var (name, value) in defaults) result.TryAdd(name, value);
        return result;
    }

    static bool IsDefaultCondition(string condition, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(condition,
            @"^\s*'\$\(" + System.Text.RegularExpressions.Regex.Escape(name) + @"\)'\s*==\s*''\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static readonly System.Text.RegularExpressions.Regex PropertyReference =
        new(@"\$\(\s*([A-Za-z_][A-Za-z0-9_\-]*)\s*\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Replaces each $(Name) with its value, following properties that refer to others. Null when one isn't known.</summary>
    internal static string? Expand(string value, Func<string, string?> property, int depth = 0)
    {
        if (!value.Contains("$(", StringComparison.Ordinal)) return value;
        if (depth >= 8) return null; // a cycle, or deeper than anything real
        bool unknown = false;
        var expanded = PropertyReference.Replace(value, m =>
        {
            var v = property(m.Groups[1].Value);
            if (v == null || Expand(v, property, depth + 1) is not { } inner) { unknown = true; return m.Value; }
            return inner;
        });
        return unknown ? null : expanded;
    }

    static XDocument? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>The package files at one git revision, read once each.</summary>
    sealed class Revision(string repo, string rev)
    {
        readonly Dictionary<string, string?> _text = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, string>> _properties = new(StringComparer.Ordinal);
        readonly Dictionary<string, Dictionary<string, string>> _versions = new(StringComparer.Ordinal);
        List<string>? _packageFiles;

        public List<string> PackageFiles => _packageFiles ??= (TryGit(repo, "ls-tree", "-r", "--name-only", rev) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsPackageFile)
            .ToList();

        public string? Text(string file)
        {
            if (!_text.TryGetValue(file, out var t)) _text[file] = t = TryGit(repo, "show", $"{rev}:{file}");
            return t;
        }

        Dictionary<string, string> Properties(string file)
        {
            if (!_properties.TryGetValue(file, out var p)) _properties[file] = p = ReadProperties(Text(file));
            return p;
        }

        public Dictionary<string, string> Versions(string file)
        {
            if (!_versions.TryGetValue(file, out var v))
            {
                var text = Text(file);
                _versions[file] = v = text != null && text.Contains("$(", StringComparison.Ordinal)
                    ? ReadVersions(text, name => Lookup(file, name))
                    : ReadVersions(text);
            }
            return v;
        }

        /// <summary>
        /// A property as <paramref name="file"/> sees it: the file's own definition, else the nearest
        /// .props/.targets in its folder or a parent folder (where Directory.Build.props and
        /// Directory.Packages.props sit), else any other .props/.targets in the repository, when
        /// all of them agree (a shared versions file imported by hand).
        /// </summary>
        string? Lookup(string file, string name)
        {
            if (Properties(file).TryGetValue(name, out var own)) return own;
            var imports = PackageFiles.Where(f => IsImportFile(f) && f != file).ToList();
            for (var dir = Folder(file); dir != null; dir = dir.Length == 0 ? null : Folder(dir))
                foreach (var f in imports.Where(f => Folder(f) == dir).Order(StringComparer.Ordinal))
                    if (Properties(f).TryGetValue(name, out var v)) return v;
            var values = imports.Select(f => Properties(f).GetValueOrDefault(name)).OfType<string>().Distinct(StringComparer.Ordinal).Take(2).ToList();
            return values.Count == 1 ? values[0] : null;
        }

        static string Folder(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path[..slash];
        }
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
