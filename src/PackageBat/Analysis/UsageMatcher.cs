using PackageBat.Model;

namespace PackageBat.Analysis;

/// <summary>
/// Joins the consumer's usages with the API changes in a bump. Language-neutral:
/// it works only from ApiSurface (old and new) and UsageSet.
/// </summary>
public static class UsageMatcher
{
    public static PackageReport Analyze(ApiSurface oldApi, ApiSurface newApi, UsageSet usages)
    {
        var diff = ApiDiff.Compute(oldApi, newApi);
        var report = new PackageReport
        {
            Ecosystem = oldApi.Ecosystem,
            Package = oldApi.Package,
            FromVersion = oldApi.Version,
            ToVersion = newApi.Version,
            Framework = newApi.Framework ?? oldApi.Framework,
            AllChanges = diff.Changes,
            AddedApiCount = diff.AddedCount,
        };

        var hits = new Dictionary<string, (ApiChange Change, List<SourceLocation> Locs, SortedSet<string> Members)>(StringComparer.Ordinal);
        void Hit(ApiChange? c, SourceLocation loc, ApiMember? via = null)
        {
            if (c == null) return;
            if (!hits.TryGetValue(c.Id, out var h))
                hits[c.Id] = h = (c, new List<SourceLocation>(), new SortedSet<string>(StringComparer.Ordinal));
            h.Locs.Add(loc);
            if (via != null && c.MemberKey == null && via.Kind != ApiMemberKind.Field) h.Members.Add(via.Name.StartsWith("get_") || via.Name.StartsWith("set_") ? via.Name[4..] : via.Name == ".ctor" ? "constructor" : via.Name);
            else if (via != null && c.MemberKey == null) h.Members.Add(via.Name);
        }

        var usedApis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in usages.Usages)
        {
            var oldType = oldApi.FindType(u.TypeName);
            if (oldType == null)
                continue; // not this package's type (or not public in the old version)

            if (u.Kind == UsageKind.Override || u.Kind == UsageKind.Member)
            {
                if (u.MemberKey == null) continue;
                var oldMember = oldApi.Resolve(u.TypeName, u.MemberKey);
                if (oldMember == null || (u.Kind == UsageKind.Override && !oldMember.IsVirtual && !oldMember.IsAbstract))
                    continue; // inherited from outside the package, or not actually an override
                report.ReferenceCount++;
                usedApis.Add(oldMember.DeclaringType + "::" + oldMember.Key);

                if (newApi.FindType(u.TypeName) == null)
                {
                    Hit(ApiDiff.TypeRemoved(oldType, newApi), u.Location, oldMember);
                    continue;
                }
                var newMember = newApi.Resolve(u.TypeName, u.MemberKey);
                if (newMember == null && oldMember.DeclaringType != u.TypeName && newApi.IsForwarded(oldMember.DeclaringType))
                    continue; // inherited from a base that moved to a dependency, as in ApiDiff.LostInheritance
                var change = oldMember.DeclaringType == u.TypeName ? null : ApiDiff.InheritedMemberChange(oldApi, newApi, u.TypeName, oldMember);
                Hit(change ?? (newMember != null ? ApiDiff.MemberDeprecated(oldMember, newMember) : ApiDiff.MemberMissing(oldMember, newApi)), u.Location);
                if (newMember != null && newApi.FindType(oldMember.DeclaringType) is { } nt && oldApi.FindType(oldMember.DeclaringType) is { } ot)
                    Hit(ApiDiff.TypeDeprecated(ot, nt), u.Location, oldMember);
                continue;
            }

            report.ReferenceCount++;
            usedApis.Add(u.TypeName);
            var newType = newApi.FindType(u.TypeName);
            if (newType == null)
            {
                Hit(ApiDiff.TypeRemoved(oldType, newApi), u.Location);
                continue;
            }
            Hit(ApiDiff.TypeDeprecated(oldType, newType), u.Location);

            if (u.Kind == UsageKind.Inherit)
                Hit(ApiDiff.TypeSealed(oldType, newType), u.Location);

            if (u.Kind is UsageKind.Inherit or UsageKind.Implement)
            {
                // Abstract members can be added anywhere up the chain the consumer now has to satisfy.
                foreach (var c in ApiDiff.AbstractMembersAdded(oldApi, newApi, u.TypeName))
                    Hit(c, u.Location);
            }
        }

        report.UsedApiCount = usedApis.Count;
        report.Used = hits.Values
            .Select(h => new UsedChange { Change = h.Change, Locations = Dedupe(h.Locs), MembersUsed = h.Members.ToList() })
            .OrderBy(u => u.Change.Severity)
            .ThenByDescending(u => u.Locations.Count)
            .ThenBy(u => u.Change.Subject, StringComparer.Ordinal)
            .ToList();
        return report;
    }

    static List<SourceLocation> Dedupe(List<SourceLocation> locs)
    {
        // When a file is known, file+line identifies the spot. Otherwise fall back to module+symbol.
        return locs
            .GroupBy(l => l.File != null ? $"{l.File}:{l.Line}" : $"{l.Module}|{l.Symbol}")
            .Select(g => g.First())
            .OrderBy(l => l.File ?? "￿", StringComparer.Ordinal).ThenBy(l => l.Line ?? 0)
            .ToList();
    }
}
