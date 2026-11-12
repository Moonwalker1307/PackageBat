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

        var baseAddress = await GetBaseAddressAsync(ct);
        var url = $"{baseAddress.TrimEnd('/')}/{lowerId}/{lowerVer}/{lowerId}.{lowerVer}.nupkg";
        _log.WriteLine($"Downloading {id} {version}");
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new UsageException($"Could not download {id} {version} from {_source} ({(int)response.StatusCode}).");

        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(tmp);
        await using (var s = await response.Content.ReadAsStreamAsync(ct))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                // Only assemblies matter; also keeps us clear of odd paths in the archive.
                if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
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
        if (parts.Count == 4 && parts[3] == "0") parts.RemoveAt(3);
        return string.Join('.', parts.Select(p => int.TryParse(p, out var n) ? n.ToString() : p)) + rest;
    }
}
