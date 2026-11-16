using System.IO.Compression;
using System.Text.Json;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// Gets a package's assemblies for a target framework: from the local NuGet
/// cache when CI already restored it, otherwise from a NuGet v3 feed.
/// </summary>
internal sealed class NuGetFetcher
{
    public const string DefaultSource = "https://api.nuget.org/v3/index.json";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    readonly string _source;
    readonly TextWriter _log;
    string? _baseAddress;

    public NuGetFetcher(string? source, TextWriter log)
    {
        _source = source ?? DefaultSource;
        _log = log;
    }

    public static string GlobalPackagesFolder =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    static string CacheRoot => Path.Combine(Path.GetTempPath(), "packagebat", "packages");

    /// <summary>Returns the extracted package folder.</summary>
    public async Task<string> GetPackageFolderAsync(string id, string version, CancellationToken ct)
    {
        var lowerId = id.ToLowerInvariant();
        var lowerVer = NormalizeVersion(version).ToLowerInvariant();

        var restored = Path.Combine(GlobalPackagesFolder, lowerId, lowerVer);
        if (Directory.Exists(Path.Combine(restored, "lib")) || Directory.Exists(Path.Combine(restored, "ref")))
            return restored;

        var target = Path.Combine(CacheRoot, lowerId, lowerVer);
        if (Directory.Exists(target))
            return target;

        _log.WriteLine($"Downloading {id} {version}");
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tmp);
        await using (var s = await OpenAsync($"{lowerId}/{lowerVer}/{lowerId}.{lowerVer}.nupkg", $"{id} {version}", ct)
            ?? throw new UsageException($"Could not download {id} {version} from {_source} (not found)."))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                // Only assemblies and the manifest matter; also keeps us clear of odd paths in the archive.
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !(entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !entry.FullName.Contains('/'))) continue;
                var dest = Path.GetFullPath(Path.Combine(tmp, Uri.UnescapeDataString(entry.FullName)));
                if (!dest.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        try { Directory.Move(tmp, target); }
        catch (IOException) when (Directory.Exists(target)) { Directory.Delete(tmp, true); }
        return target;
    }

    /// <summary>
    /// The package's .nuspec, or null when the feed doesn't have that version.
    /// Read from the local NuGet cache or an extracted download when there is one,
    /// otherwise fetched on its own (feeds serve it next to the .nupkg).
    /// </summary>
    public async Task<string?> GetNuspecAsync(string id, string version, CancellationToken ct)
    {
        var lowerId = id.ToLowerInvariant();
        var lowerVer = NormalizeVersion(version).ToLowerInvariant();
        foreach (var folder in new[] { Path.Combine(GlobalPackagesFolder, lowerId, lowerVer), Path.Combine(CacheRoot, lowerId, lowerVer) })
            if (FindNuspec(folder) is { } local)
                return await File.ReadAllTextAsync(local, ct);

        var target = Path.Combine(CacheRoot, "nuspec", lowerId, lowerVer + ".nuspec");
        if (File.Exists(target)) return await File.ReadAllTextAsync(target, ct);
        await using var s = await OpenAsync($"{lowerId}/{lowerVer}/{lowerId}.nuspec", $"the {id} {version} manifest", ct);
        if (s == null) return null;
        using var reader = new StreamReader(s);
        var text = await reader.ReadToEndAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tmp, text, ct);
        File.Move(tmp, target, overwrite: true);
        return text;
    }

    /// <summary>The .nuspec at the top of an extracted package folder, if any.</summary>
    public static string? FindNuspec(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault() : null;

    /// <summary>Every version of a package the feed lists, or an empty list when it has no such package.</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, CancellationToken ct)
    {
        await using var s = await OpenAsync($"{id.ToLowerInvariant()}/index.json", $"the versions of {id}", ct);
        if (s == null) return Array.Empty<string>();
        try
        {
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return versions.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>Opens a file from the feed's flat container (an HTTP feed or a local folder); null when it isn't there.</summary>
    async Task<Stream?> OpenAsync(string relative, string what, CancellationToken ct)
    {
        var baseAddress = await GetBaseAddressAsync(ct);
        if (Directory.Exists(baseAddress))
        {
            var path = Path.Combine(baseAddress, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
        var response = await Http.GetAsync($"{baseAddress.TrimEnd('/')}/{relative}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { response.Dispose(); return null; }
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new UsageException($"Could not download {what} from {_source} ({code}).");
        }
        // The content stream keeps the response alive; disposing the stream releases it.
        return await response.Content.ReadAsStreamAsync(ct);
    }

    async Task<string> GetBaseAddressAsync(CancellationToken ct)
    {
        if (_baseAddress != null) return _baseAddress;
        if (Directory.Exists(_source)) return _baseAddress = _source; // a local folder laid out like a flat container
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(_source, ct));
        foreach (var res in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = res.GetProperty("@type").GetString() ?? "";
            if (type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal))
                return _baseAddress = res.GetProperty("@id").GetString()!;
        }
        throw new UsageException($"{_source} has no PackageBaseAddress resource.");
    }

    /// <summary>"[1.2.3]" -> "1.2.3", "1.2" -> "1.2.0", drops build metadata.</summary>
    public static string NormalizeVersion(string v)
    {
        v = v.Trim().Trim('[', ']', '(', ')');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        var core = dash >= 0 ? v[..dash] : v;
        var rest = dash >= 0 ? v[dash..] : "";
        var parts = core.Split('.').ToList();
        while (parts.Count < 3) parts.Add("0");
        if (parts.Count == 4 && int.TryParse(parts[3], out var revision) && revision == 0) parts.RemoveAt(3); // "1.0.0.00" too
        return string.Join('.', parts.Select(p => int.TryParse(p, out var n) ? n.ToString() : p)) + rest;
    }
}
