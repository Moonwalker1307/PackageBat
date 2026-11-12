using System.Text.RegularExpressions;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>Picks the assemblies in a NuGet package that a project targeting a given framework would compile against.</summary>
internal static partial class FrameworkSelector
{
    public sealed record Selection(string Framework, IReadOnlyList<string> Assemblies);

    public static Selection? Select(string packageFolder, string? targetFramework)
    {
        // Reference assemblies (ref/) are what the compiler sees; prefer them over lib/.
        foreach (var root in new[] { "ref", "lib" })
        {
            var dir = Path.Combine(packageFolder, root);
            if (!Directory.Exists(dir)) continue;

            var candidates = Directory.GetDirectories(dir)
                .Select(d => (Folder: d, Tfm: Path.GetFileName(d).ToLowerInvariant()))
                .Where(c => Directory.EnumerateFiles(c.Folder, "*.dll").Any())
                .ToList();
            var looseDlls = Directory.GetFiles(dir, "*.dll");

            var best = candidates
                .Select(c => (c.Folder, c.Tfm, Score: Score(c.Tfm, targetFramework)))
                .Where(c => c.Score > 0)
                .OrderByDescending(c => c.Score)
                .FirstOrDefault();

            if (best.Folder != null)
                return new Selection(best.Tfm, Directory.GetFiles(best.Folder, "*.dll"));
            if (looseDlls.Length > 0)
                return new Selection("any", looseDlls);
        }
        return null;
    }

    /// <summary>
    /// Higher is better, 0 means incompatible. Without a target framework we
    /// take the newest .NET (Core) build, which is what most consumers compile against.
    /// </summary>
    internal static int Score(string candidate, string? target)
    {
        var c = Parse(candidate);
        if (c == null) return 0;
        var t = target == null ? new Tfm(Family.Net, 99.0, null) : Parse(target.ToLowerInvariant());
        if (t == null) return 0;
        if (c.Platform != null && c.Platform != t.Platform) return 0;

        int platformBonus = c.Platform != null ? 5 : 0;
        switch (t.Family)
        {
            case Family.Net:
                if (c.Family == Family.Net && c.Version <= t.Version) return 3000 + (int)(c.Version * 10) + platformBonus;
                if (c.Family == Family.NetStandard && c.Version <= 2.1) return 2000 + (int)(c.Version * 10);
                return 0;
            case Family.NetFramework:
                if (c.Family == Family.NetFramework && c.Version <= t.Version) return 3000 + (int)(c.Version * 100);
                if (c.Family == Family.NetStandard && c.Version <= 2.0 && t.Version >= 4.61) return 2000 + (int)(c.Version * 10);
                return 0;
            case Family.NetStandard:
                return c.Family == Family.NetStandard && c.Version <= t.Version ? 2000 + (int)(c.Version * 10) : 0;
        }
        return 0;
    }

    enum Family { Net, NetFramework, NetStandard }
    sealed record Tfm(Family Family, double Version, string? Platform);

    static Tfm? Parse(string tfm)
    {
        var m = NetRegex().Match(tfm);
        if (m.Success)
            return new Tfm(Family.Net, double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[3].Success ? m.Groups[3].Value : null);
        m = StandardRegex().Match(tfm);
        if (m.Success)
            return new Tfm(Family.NetStandard, double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), null);
        m = FrameworkRegex().Match(tfm);
        if (m.Success)
        {
            // net461 -> 4.61, net48 -> 4.8, net472 -> 4.72
            var digits = m.Groups[1].Value;
            var v = double.Parse(digits[0] + "." + digits[1..], System.Globalization.CultureInfo.InvariantCulture);
            return new Tfm(Family.NetFramework, v, null);
        }
        return null;
    }

    [GeneratedRegex(@"^(net|netcoreapp)(\d+\.\d+)(?:-([a-z]+)[\d.]*)?$")]
    private static partial Regex NetRegex();
    [GeneratedRegex(@"^netstandard(\d+\.\d+)$")]
    private static partial Regex StandardRegex();
    [GeneratedRegex(@"^net(\d{2,3})$")]
    private static partial Regex FrameworkRegex();

    /// <summary>".NETCoreApp,Version=v8.0" -> "net8.0"; ".NETFramework,Version=v4.7.2" -> "net472".</summary>
    public static string? FromTargetFrameworkAttribute(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var m = Regex.Match(value, @"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+(?:\.\d+)*)");
        if (!m.Success) return null;
        var ver = m.Groups[2].Value;
        return m.Groups[1].Value switch
        {
            "NETCoreApp" => (double.Parse(ver.Split('.')[0]) >= 5 ? "net" : "netcoreapp") + string.Join('.', ver.Split('.').Take(2)),
            "NETStandard" => "netstandard" + string.Join('.', ver.Split('.').Take(2)),
            _ => "net" + ver.Replace(".", ""),
        };
    }
}
