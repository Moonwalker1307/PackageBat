using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// The NuGet dependency graph around a bump, read from the packages' .nuspec
/// files and the consumer's restore output. Two checks use it:
///
/// * Transitive bumps. A bumped package's new version can require a newer
///   version of one of its own dependencies, which then changes too even though
///   no project file mentions it. Those are analysed like any other bump.
/// * Missing run-time dependencies. Code can load an assembly by name
///   ("Namespace.Type, Assembly" into Type.GetType), which no metadata reference
///   or nuspec records. When a new version starts doing that and nothing in the
///   consumer's graph provides the assembly, it fails at run time only.
/// </summary>
internal sealed class DependencyGraph(NuGetFetcher fetcher, EcosystemOptions options, string? framework)
{
    /// <summary>How far down the graph to follow version changes.</summary>
    internal const int MaxDepth = 4;
    /// <summary>Stop following after this many transitive bumps (a whole-framework upgrade can touch hundreds).</summary>
    internal const int MaxTransitive = 200;
    /// <summary>Most packages to visit when collecting a package's dependency closure.</summary>
    internal const int MaxClosure = 500;

    readonly Dictionary<string, Task<IReadOnlyList<NuGetDependency>>> _deps = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string>? _restored;
    HashSet<string>? _consumerFiles;

    /// <summary>What the consumer restored before the change: package id -> version.</summary>
    internal Dictionary<string, string> Restored => _restored ??= ProjectAssets.Read(options.ConsumerPaths, options.Log);

    public async Task<IReadOnlyList<PackageBump>> TransitiveBumpsAsync(IReadOnlyList<PackageBump> bumps, CancellationToken ct)
    {
        var result = new List<PackageBump>();
        var known = new HashSet<string>(bumps.Select(b => b.Package), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(PackageBump Bump, int Depth)>(bumps.Where(b => b.Added != true && b.From.Length > 0).Select(b => (b, 0)));
        while (queue.Count > 0 && result.Count < MaxTransitive)
        {
            var (bump, depth) = queue.Dequeue();
            if (depth >= MaxDepth) continue;
            IReadOnlyList<NuGetDependency> before, after;
            try
            {
                before = await DependenciesAsync(bump.Package, bump.From, ct);
                after = await DependenciesAsync(bump.Package, bump.To, ct);
            }
            catch (Exception e) when (e is UsageException or HttpRequestException or IOException)
            {
                options.Log.WriteLine($"warning: could not read the dependencies of {bump.Package}: {e.Message}");
                continue;
            }
            foreach (var dep in after)
            {
                if (known.Contains(dep.Id)) continue;
                // The version the consumer really had: from its restore output when we have it
                // (another package may already have pulled in something newer), else the old minimum.
                var from = Restored.GetValueOrDefault(dep.Id) ?? before.FirstOrDefault(d => d.Id.Equals(dep.Id, StringComparison.OrdinalIgnoreCase))?.MinVersion;
                if (from == null || NuGetVersion.Compare(dep.MinVersion, from) <= 0) continue; // new to the graph, or unchanged
                known.Add(dep.Id);
                var t = new PackageBump { Ecosystem = "dotnet", Package = dep.Id, From = from, To = dep.MinVersion, Via = bump.Package };
                result.Add(t);
                options.Log.WriteLine($"{dep.Id} {from} -> {dep.MinVersion}: required by {bump.Package} {bump.To}");
                if (result.Count >= MaxTransitive)
                {
                    options.Log.WriteLine($"warning: stopped following dependency changes after {MaxTransitive} packages.");
                    break;
                }
                queue.Enqueue((t, depth + 1));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<ApiChange>> MissingRuntimeDependenciesAsync(PackageBump bump, IReadOnlyCollection<string> consumerPackages, CancellationToken ct)
    {
        if (bump.Added == true || bump.From.Length == 0) return Array.Empty<ApiChange>();
        var newSelection = FrameworkSelector.Select(await FolderAsync(bump.Package, bump.To, ct), framework, implementation: true);
        if (newSelection == null) return Array.Empty<ApiChange>();
        var found = LateBoundScanner.Scan(newSelection.Assemblies);
        if (found.Count == 0) return Array.Empty<ApiChange>();

        // Only what is new in this version: a name the old version already loaded has either always
        // worked for this consumer or always failed, and either way the bump didn't cause it.
        var oldSelection = FrameworkSelector.Select(await FolderAsync(bump.Package, bump.From, ct), framework, implementation: true);
        if (oldSelection == null) return Array.Empty<ApiChange>(); // nothing to compare with
        var oldNames = LateBoundScanner.Scan(oldSelection.Assemblies).Select(n => n.AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var own = newSelection.Assemblies.Select(AssemblyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = found
            .Where(n => !oldNames.Contains(n.AssemblyName) && !own.Contains(n.AssemblyName) && !IsPlatform(n.AssemblyName)
                && !consumerPackages.Contains(n.AssemblyName, StringComparer.OrdinalIgnoreCase)
                && !Restored.ContainsKey(n.AssemblyName) && !ConsumerFiles.Contains(n.AssemblyName))
            .GroupBy(n => n.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0) return Array.Empty<ApiChange>();

        // Anything the new version's own dependencies bring is in the output too.
        var closure = await ClosureAsync(bump.Package, bump.To, ct);
        foreach (var id in closure.Keys) candidates.Remove(id);
        // Assemblies are usually named after their package; for the rest, look inside the
        // dependencies that share the first part of the name (Foo.Core ships Foo.Abstractions.dll).
        foreach (var (id, version) in closure)
        {
            if (candidates.Count == 0) break;
            if (!candidates.Keys.Any(c => FirstSegment(c).Equals(FirstSegment(id), StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (FrameworkSelector.Select(await FolderAsync(id, version, ct), framework, implementation: true) is { } sel)
                    foreach (var dll in sel.Assemblies) candidates.Remove(AssemblyName(dll));
            }
            catch (Exception e) when (e is UsageException or HttpRequestException or IOException or BadImageFormatException) { }
        }

        var result = new List<ApiChange>();
        foreach (var (assembly, names) in candidates.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var first = names.OrderBy(n => n.Loader == null).ThenBy(n => n.Literal, StringComparer.Ordinal).First();
            var suggestions = new List<string>();
            try
            {
                if (await SuggestedVersionAsync(assembly, bump.To, ct) is { } v) suggestions.Add($"{assembly} {v}");
            }
            catch (Exception e) when (e is UsageException or HttpRequestException or IOException) { }
            result.Add(new ApiChange
            {
                Id = $"runtime:{assembly}",
                Kind = ChangeKind.RuntimeDependencyMissing,
                Severity = Severity.Warning,
                TypeName = first.TypeName ?? assembly,
                Subject = assembly,
                Description = first.Loader != null
                    ? $"{first.Module}.dll passes \"{first.Literal}\" to {first.Loader}"
                    : $"{first.Module}.dll contains the type name \"{first.Literal}\"",
                Suggestions = suggestions,
            });
        }
        return result;
    }

    /// <summary>A package with the same id as the missing assembly, preferring the bump's own version (packages released together).</summary>
    async Task<string?> SuggestedVersionAsync(string id, string version, CancellationToken ct)
    {
        var versions = await fetcher.GetVersionsAsync(id, ct);
        if (versions.Count == 0) return null;
        var same = versions.FirstOrDefault(v => NuGetVersion.Compare(v, version) == 0);
        if (same != null) return same;
        var stable = versions.Where(v => !NuGetVersion.IsPrerelease(v)).ToList();
        return (stable.Count > 0 ? stable : versions.ToList()).Aggregate((a, b) => NuGetVersion.Compare(a, b) >= 0 ? a : b);
    }

    /// <summary>Every package the given version brings in, id -> lowest version allowed.</summary>
    internal async Task<Dictionary<string, string>> ClosureAsync(string package, string version, CancellationToken ct)
    {
        var closure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, string Version)>();
        queue.Enqueue((package, version));
        var visited = 0;
        while (queue.Count > 0 && visited++ < MaxClosure)
        {
            var (id, v) = queue.Dequeue();
            IReadOnlyList<NuGetDependency> deps;
            try { deps = await DependenciesAsync(id, v, ct); }
            catch (Exception e) when (e is UsageException or HttpRequestException or IOException) { continue; }
            foreach (var d in deps)
                if (closure.TryAdd(d.Id, d.MinVersion))
                    queue.Enqueue((d.Id, d.MinVersion));
        }
        return closure;
    }

    internal Task<IReadOnlyList<NuGetDependency>> DependenciesAsync(string id, string version, CancellationToken ct)
    {
        var key = $"{id}/{version}";
        lock (_deps)
        {
            if (!_deps.TryGetValue(key, out var task))
                _deps[key] = task = Load();
            return task;
        }

        async Task<IReadOnlyList<NuGetDependency>> Load()
        {
            string? xml;
            if (Directory.Exists(version)) // a local package folder, as GetApiAsync accepts
                xml = NuGetFetcher.FindNuspec(version) is { } f ? await File.ReadAllTextAsync(f, ct) : null;
            else
                xml = await fetcher.GetNuspecAsync(id, version, ct);
            return Nuspec.Dependencies(xml, framework);
        }
    }

    Task<string> FolderAsync(string id, string version, CancellationToken ct) =>
        Directory.Exists(version) ? Task.FromResult(version) : fetcher.GetPackageFolderAsync(id, version, ct);

    /// <summary>File names (without .dll) of every assembly in the consumer's build output.</summary>
    HashSet<string> ConsumerFiles => _consumerFiles ??= ReadConsumerFiles(options.ConsumerPaths);

    static HashSet<string> ReadConsumerFiles(IReadOnlyList<string> paths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sep = Path.DirectorySeparatorChar;
        foreach (var p in paths)
        {
            var dir = Directory.Exists(p) ? p : File.Exists(p) ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
            if (dir == null) continue;
            try
            {
                var option = Directory.Exists(p) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll", option))
                    if (!f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}.git{sep}"))
                        names.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return names;
    }

    /// <summary>The assembly's own name, falling back to the file name.</summary>
    static string AssemblyName(string dll)
    {
        try
        {
            using var s = File.OpenRead(dll);
            using var pe = new PEReader(s);
            if (pe.HasMetadata && pe.GetMetadataReader() is { IsAssembly: true } r)
                return r.GetString(r.GetAssemblyDefinition().Name);
        }
        catch (Exception e) when (e is IOException || MetadataNames.IsMalformed(e)) { }
        return Path.GetFileNameWithoutExtension(dll);
    }

    static string FirstSegment(string name) => name.Split('.')[0];

    static readonly HashSet<string> RuntimeAssemblies = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(Path.GetFileNameWithoutExtension)
        .OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assemblies that come with .NET itself rather than from a package.</summary>
    internal static bool IsPlatform(string name) =>
        name is "mscorlib" or "netstandard" or "System" or "WindowsBase" or "Microsoft.CSharp"
        || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Presentation", StringComparison.Ordinal)
        || name.StartsWith("Windows.", StringComparison.Ordinal)
        || RuntimeAssemblies.Contains(name);
}
