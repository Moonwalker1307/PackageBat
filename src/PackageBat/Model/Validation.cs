namespace PackageBat.Model;

/// <summary>
/// Checks JSON documents in the language-neutral format before they are used.
/// System.Text.Json happily produces nulls for missing or null values, even in
/// properties the model treats as required, so a hand-written or buggy
/// extractor's file would otherwise fail deep inside the diff. Missing lists are
/// treated as empty; missing names and keys are errors.
/// </summary>
public static class Validation
{
    /// <summary>Returns the first problem found, or null when the document is usable.</summary>
    public static string? Check(ApiSurface api)
    {
        api.Ecosystem ??= "";
        api.Package ??= "";
        api.Version ??= "";
        api.Modules = NoNulls(api.Modules);
        api.ForwardedTypes = NoNulls(api.ForwardedTypes);
        api.Types ??= new();
        for (int i = 0; i < api.Types.Count; i++)
        {
            var t = api.Types[i];
            var at = $"types[{i}]";
            if (t == null) return $"{at} is null";
            if (string.IsNullOrEmpty(t.Name)) return $"{at}.name is missing";
            t.Display ??= t.Name;
            t.Module ??= "";
            t.Interfaces = NoNulls(t.Interfaces);
            t.Members ??= new();
            for (int j = 0; j < t.Members.Count; j++)
            {
                var m = t.Members[j];
                var am = $"{at}.members[{j}]";
                if (m == null) return $"{am} is null";
                if (string.IsNullOrEmpty(m.Key)) return $"{am}.key is missing";
                m.Name ??= "";
                m.DeclaringType = string.IsNullOrEmpty(m.DeclaringType) ? t.Name : m.DeclaringType;
                m.Display ??= m.Key;
                m.ReturnType ??= "";
                m.Parameters ??= new();
                for (int k = 0; k < m.Parameters.Count; k++)
                {
                    if (m.Parameters[k] is not { } p) return $"{am}.parameters[{k}] is null";
                    p.Type ??= "";
                }
            }
        }
        return null;
    }

    /// <inheritdoc cref="Check(ApiSurface)"/>
    public static string? Check(UsageSet usages)
    {
        usages.Ecosystem ??= "";
        usages.Scanned = NoNulls(usages.Scanned);
        usages.Usages ??= new();
        for (int i = 0; i < usages.Usages.Count; i++)
        {
            var u = usages.Usages[i];
            if (u == null) return $"usages[{i}] is null";
            if (string.IsNullOrEmpty(u.TypeName)) return $"usages[{i}].typeName is missing";
            u.Location ??= new SourceLocation();
            u.Location.Module ??= "";
            u.Location.TestHits = null; // only ever set from --coverage, never taken from a usages file
        }
        return null;
    }

    /// <inheritdoc cref="Check(ApiSurface)"/>
    public static string? Check(List<PackageBump> bumps)
    {
        for (int i = 0; i < bumps.Count; i++)
        {
            var b = bumps[i];
            if (b == null) return $"[{i}] is null";
            if (string.IsNullOrEmpty(b.Package) || string.IsNullOrEmpty(b.From) || string.IsNullOrEmpty(b.To))
                return $"[{i}] needs package, from and to";
            b.Ecosystem ??= "";
        }
        return null;
    }

    static List<string> NoNulls(List<string>? list) => list == null ? new() : list.Where(s => s != null).ToList();
}
