using PackageBat.Model;

namespace PackageBat.Analysis;

/// <summary>Compares two ApiSurfaces. Language-neutral.</summary>
public static class ApiDiff
{
    public sealed record Result(List<ApiChange> Changes, int AddedCount);

    public static Result Compute(ApiSurface oldApi, ApiSurface newApi)
    {
        var changes = new Dictionary<string, ApiChange>(StringComparer.Ordinal);
        void Add(ApiChange? c) { if (c != null) changes.TryAdd(c.Id, c); }

        // Go through TypeIndex, not Types: if a package defines a type name twice
        // (two assemblies), every lookup uses the first one, so the diff must too.
        int added = newApi.TypeIndex.Values.Count(t => oldApi.FindType(t.Name) == null);

        foreach (var oldType in oldApi.TypeIndex.Values)
        {
            var newType = newApi.FindType(oldType.Name);
            if (newType == null)
            {
                Add(TypeRemoved(oldType, newApi));
                continue;
            }

            Add(TypeDeprecated(oldType, newType));
            Add(TypeSealed(oldType, newType));
            foreach (var c in AbstractMembersAdded(oldApi, newApi, oldType.Name)) Add(c);

            foreach (var oldMember in oldType.Members)
            {
                var newMember = newApi.Resolve(oldType.Name, oldMember.Key);
                Add(newMember == null ? MemberMissing(oldMember, newApi) : MemberDeprecated(oldMember, newMember));
            }

            // Members the type only inherited from a base in the package: if the base
            // still has them but this type no longer reaches them (its base type or
            // interfaces changed), code calling them through this type breaks.
            foreach (var baseType in oldApi.Hierarchy(oldType.Name).Skip(1))
                foreach (var inherited in baseType.Members)
                    Add(InheritedMemberChange(oldApi, newApi, oldType.Name, inherited));

            added += newType.Members.Count(m => oldApi.Resolve(oldType.Name, m.Key) == null);
        }

        var ordered = changes.Values
            .OrderBy(c => c.Severity).ThenBy(c => c.TypeName, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal)
            .ToList();
        return new Result(ordered, added);
    }

    // The factories below are shared with UsageMatcher so a change found from a
    // call site gets the same Id as the one found by the full diff.

    public static ApiChange? TypeRemoved(ApiType oldType, ApiSurface newApi)
    {
        if (newApi.IsForwarded(oldType.Name))
            return null; // moved to a dependency; we cannot see it, so say nothing rather than cry wolf

        var simple = SimpleName(oldType.Name);
        var moved = newApi.Types.Where(t => SimpleName(t.Name) == simple).Select(Qualified).Take(3).ToList();
        return new ApiChange
        {
            Id = $"type-removed:{oldType.Name}",
            Kind = ChangeKind.TypeRemoved,
            Severity = Severity.Breaking,
            TypeName = oldType.Name,
            Subject = Qualified(oldType),
            Description = moved.Count > 0 ? "Type moved or was replaced" : "Type removed",
            Suggestions = moved,
        };
    }

    public static ApiChange? TypeDeprecated(ApiType oldType, ApiType newType)
    {
        if (newType.Deprecated == null || oldType.Deprecated != null)
            return null;
        return new ApiChange
        {
            Id = $"type-deprecated:{oldType.Name}",
            Kind = ChangeKind.Deprecated,
            Severity = newType.Deprecated.IsError ? Severity.Breaking : Severity.Warning,
            TypeName = oldType.Name,
            Subject = Qualified(oldType),
            Description = Deprecated(newType.Deprecated),
        };
    }

    public static ApiChange? TypeSealed(ApiType oldType, ApiType newType)
    {
        if (!oldType.IsInheritable || newType.IsInheritable)
            return null;
        return new ApiChange
        {
            Id = $"type-sealed:{oldType.Name}",
            Kind = ChangeKind.TypeSealed,
            Severity = Severity.Breaking,
            TypeName = oldType.Name,
            Subject = Qualified(oldType),
            Description = newType.IsSealed ? "Type is now sealed, so it can no longer be derived from" : "Type can no longer be derived from",
        };
    }

    /// <summary>
    /// Abstract members that code deriving from (or implementing) <paramref name="typeName"/>
    /// must now provide: new abstract members on the type or anywhere up its new
    /// hierarchy, including bases and interfaces it did not have before.
    /// A member added to a base the type already had is reported once, on that base;
    /// one brought in by a new base or interface is reported on the type itself.
    /// </summary>
    public static IEnumerable<ApiChange> AbstractMembersAdded(ApiSurface oldApi, ApiSurface newApi, string typeName)
    {
        if (newApi.FindType(typeName) == null)
            yield break;
        var oldHierarchy = oldApi.Hierarchy(typeName).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var t in newApi.Hierarchy(typeName))
            foreach (var m in t.Members)
            {
                if (!m.IsAbstract || oldApi.Resolve(typeName, m.Key) != null || !ReferenceEquals(newApi.Resolve(typeName, m.Key), m))
                    continue;
                var owner = oldHierarchy.Contains(t.Name) && oldApi.Resolve(t.Name, m.Key) == null ? t.Name : typeName;
                yield return new ApiChange
                {
                    Id = $"abstract-added:{owner}::{m.Key}",
                    Kind = ChangeKind.AbstractMemberAdded,
                    Severity = Severity.Breaking,
                    TypeName = owner,
                    MemberKey = m.Key,
                    Subject = m.Display,
                    Description = t.Kind == ApiTypeKind.Interface
                        ? "New interface member that implementers must provide"
                        : "New abstract member that derived classes must override",
                };
            }
    }

    /// <summary>
    /// A change to a member that <paramref name="typeName"/> reached by inheritance in the old version,
    /// seen from <paramref name="typeName"/>: it is gone, or it now resolves to a deprecated declaration
    /// that its old declaring type doesn't have. Null when nothing changed for this type specifically.
    /// </summary>
    public static ApiChange? InheritedMemberChange(ApiSurface oldApi, ApiSurface newApi, string typeName, ApiMember inherited)
    {
        if (newApi.Resolve(typeName, inherited.Key) is not { } now)
            return LostInheritance(oldApi, newApi, typeName, inherited);
        if (!ReferenceEquals(oldApi.Resolve(typeName, inherited.Key), inherited)
            || ReferenceEquals(newApi.Resolve(inherited.DeclaringType, inherited.Key), now))
            return null; // same declaration as from the base: reported there
        return MemberDeprecated(inherited, now, typeName);
    }

    /// <summary>
    /// <paramref name="inherited"/> is declared on a base of <paramref name="typeName"/> and still
    /// exists there in the new version, but <paramref name="typeName"/> no longer reaches it.
    /// </summary>
    public static ApiChange? LostInheritance(ApiSurface oldApi, ApiSurface newApi, string typeName, ApiMember inherited)
    {
        if (inherited.Kind == ApiMemberKind.Constructor
            || !ReferenceEquals(oldApi.Resolve(typeName, inherited.Key), inherited) // hidden by a nearer declaration
            || newApi.FindType(typeName) == null
            || newApi.Resolve(typeName, inherited.Key) != null
            || newApi.IsForwarded(inherited.DeclaringType)) // the base moved to a dependency; we can't see it
            return null;
        if (newApi.FindType(inherited.DeclaringType) != null && newApi.Resolve(inherited.DeclaringType, inherited.Key) == null)
            return null; // removed from the base itself: reported there
        return MemberMissing(inherited, newApi, typeName);
    }

    /// <param name="viaType">
    /// The type the member was reached through, when that is not its declaring type
    /// and the member is only missing from that type (see <see cref="LostInheritance"/>).
    /// </param>
    public static ApiChange MemberMissing(ApiMember oldMember, ApiSurface newApi, string? viaType = null)
    {
        var typeName = viaType ?? oldMember.DeclaringType;
        var candidates = (oldMember.Kind == ApiMemberKind.Constructor
                ? newApi.FindType(typeName) is { } own ? new[] { own } : Array.Empty<ApiType>()
                : newApi.Hierarchy(typeName))
            .SelectMany(t => t.Members)
            .Where(m => m.Name == oldMember.Name)
            .ToList();

        var change = new ApiChange
        {
            TypeName = typeName,
            MemberKey = oldMember.Key,
            Subject = oldMember.Display,
            Suggestions = candidates.Select(c => c.Display).Distinct().Take(5).ToList(),
        };

        if (candidates.Count == 0)
        {
            change.Id = $"member-removed:{typeName}::{oldMember.Key}";
            change.Kind = ChangeKind.MemberRemoved;
            change.Severity = Severity.Breaking;
            change.Description = viaType == null || viaType == oldMember.DeclaringType
                ? "Removed"
                : $"No longer available on {SimpleName(viaType)}: it was inherited from {SimpleName(oldMember.DeclaringType)}, " +
                  (newApi.FindType(oldMember.DeclaringType) == null ? "which was removed" : $"which {SimpleName(viaType)} no longer derives from");
        }
        else
        {
            change.Id = $"signature-changed:{typeName}::{oldMember.Key}";
            change.Kind = ChangeKind.SignatureChanged;
            bool sourceCompatible = candidates.Any(c => OnlyAddsOptionalParameters(oldMember, c));
            change.Severity = sourceCompatible ? Severity.BinaryOnly : Severity.Breaking;
            change.Description = sourceCompatible
                ? "Signature changed, but existing calls still compile (new optional or params parameters), so recompiling is enough. Check the new parameters mean what you expect"
                : "Signature changed" + ExplainDifference(oldMember, candidates);
        }
        return change;
    }

    /// <param name="viaType">
    /// The type the member was reached through, when in the new version it resolves
    /// there to a different declaration than on its old declaring type (for example
    /// the type now redeclares it), so the deprecation belongs to that type.
    /// </param>
    public static ApiChange? MemberDeprecated(ApiMember oldMember, ApiMember newMember, string? viaType = null)
    {
        if (newMember.Deprecated == null || oldMember.Deprecated != null)
            return null;
        var typeName = viaType ?? oldMember.DeclaringType;
        return new ApiChange
        {
            Id = $"member-deprecated:{typeName}::{oldMember.Key}",
            Kind = ChangeKind.Deprecated,
            Severity = newMember.Deprecated.IsError ? Severity.Breaking : Severity.Warning,
            TypeName = typeName,
            MemberKey = oldMember.Key,
            Subject = oldMember.Display,
            Description = Deprecated(newMember.Deprecated),
        };
    }

    /// <summary>
    /// True when every call that compiled against the old member still compiles
    /// against the new one: same leading parameters, and anything extra is
    /// optional or a params array (which may also absorb a trailing argument of
    /// its element type).
    /// </summary>
    static bool OnlyAddsOptionalParameters(ApiMember oldM, ApiMember newM)
    {
        if (oldM.IsStatic != newM.IsStatic || oldM.ReturnType != newM.ReturnType)
            return false;
        int i = 0;
        for (; i < oldM.Parameters.Count && i < newM.Parameters.Count; i++)
        {
            var o = oldM.Parameters[i];
            var n = newM.Parameters[i];
            if (o.Type == n.Type) continue;
            // params T[] at the end soaks up the remaining old arguments of type T
            if (n.IsParams && i == newM.Parameters.Count - 1 && oldM.Parameters.Skip(i).All(p => n.Type == p.Type + "[]"))
                return true;
            return false;
        }
        if (i < oldM.Parameters.Count) return false;
        if (i == newM.Parameters.Count) return false; // identical: not a change
        return newM.Parameters.Skip(i).All(p => p.HasDefault || p.IsParams);
    }

    /// <summary>
    /// When the old and new signatures look the same to a person (a type moved
    /// namespace, a member became static, a return type changed), say what differs.
    /// </summary>
    static string ExplainDifference(ApiMember oldM, List<ApiMember> candidates)
    {
        var best = candidates.FirstOrDefault(c => c.Parameters.Count == oldM.Parameters.Count && c.IsStatic == oldM.IsStatic)
            ?? candidates.FirstOrDefault(c => c.Parameters.Count == oldM.Parameters.Count);
        if (best == null) return "";
        var diffs = new List<string>();
        if (best.IsStatic != oldM.IsStatic) diffs.Add(best.IsStatic ? "it is now static" : "it is no longer static");
        if (best.ReturnType != oldM.ReturnType) diffs.Add($"the type changed from {oldM.ReturnType} to {best.ReturnType}");
        for (int i = 0; i < oldM.Parameters.Count; i++)
            if (oldM.Parameters[i].Type != best.Parameters[i].Type)
                diffs.Add($"parameter {oldM.Parameters[i].Name ?? (i + 1).ToString()} changed from {oldM.Parameters[i].Type} to {best.Parameters[i].Type}");
        return diffs.Count == 0 ? "" : " (" + string.Join("; ", diffs.Take(3)) + ")";
    }

    static string Qualified(ApiType t)
    {
        var ns = t.Name.Split('+')[0];
        var dot = ns.LastIndexOf('.');
        return dot >= 0 ? ns[..(dot + 1)] + t.Display : t.Display;
    }

    static string Deprecated(Deprecation d) =>
        (d.IsError ? "Marked obsolete as an error" : "Deprecated") + (string.IsNullOrWhiteSpace(d.Message) ? "" : $": {d.Message}");

    static string SimpleName(string fullName)
    {
        var lt = fullName.IndexOf('<');
        var core = lt >= 0 ? fullName[..lt] : fullName;
        var cut = Math.Max(core.LastIndexOf('.'), core.LastIndexOf('+'));
        return cut >= 0 ? fullName[(cut + 1)..] : fullName;
    }
}
