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

        int added = newApi.Types.Count(t => oldApi.FindType(t.Name) == null);

        foreach (var oldType in oldApi.Types)
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

    /// <summary>New abstract members on an interface or abstract class break everyone who implements or derives from it.</summary>
    public static IEnumerable<ApiChange> AbstractMembersAdded(ApiSurface oldApi, ApiSurface newApi, string typeName)
    {
        if (newApi.FindType(typeName) is not { } newType)
            yield break;
        foreach (var m in newType.Members)
        {
            if (!m.IsAbstract || oldApi.Resolve(typeName, m.Key) != null)
                continue;
            yield return new ApiChange
            {
                Id = $"abstract-added:{typeName}::{m.Key}",
                Kind = ChangeKind.AbstractMemberAdded,
                Severity = Severity.Breaking,
                TypeName = typeName,
                MemberKey = m.Key,
                Subject = m.Display,
                Description = newType.Kind == ApiTypeKind.Interface
                    ? "New interface member that implementers must provide"
                    : "New abstract member that derived classes must override",
            };
        }
    }

    public static ApiChange MemberMissing(ApiMember oldMember, ApiSurface newApi)
    {
        var candidates = (oldMember.Kind == ApiMemberKind.Constructor
                ? newApi.FindType(oldMember.DeclaringType) is { } own ? new[] { own } : Array.Empty<ApiType>()
                : newApi.Hierarchy(oldMember.DeclaringType))
            .SelectMany(t => t.Members)
            .Where(m => m.Name == oldMember.Name)
            .ToList();

        var change = new ApiChange
        {
            TypeName = oldMember.DeclaringType,
            MemberKey = oldMember.Key,
            Subject = oldMember.Display,
            Suggestions = candidates.Select(c => c.Display).Distinct().Take(5).ToList(),
        };

        if (candidates.Count == 0)
        {
            change.Id = $"member-removed:{oldMember.DeclaringType}::{oldMember.Key}";
            change.Kind = ChangeKind.MemberRemoved;
            change.Severity = Severity.Breaking;
            change.Description = "Removed";
        }
        else
        {
            change.Id = $"signature-changed:{oldMember.DeclaringType}::{oldMember.Key}";
            change.Kind = ChangeKind.SignatureChanged;
            bool sourceCompatible = candidates.Any(c => OnlyAddsOptionalParameters(oldMember, c));
            change.Severity = sourceCompatible ? Severity.BinaryOnly : Severity.Breaking;
            change.Description = sourceCompatible
                ? "Signature changed, but existing calls still compile (new optional or params parameters), so recompiling is enough. Check the new parameters mean what you expect"
                : "Signature changed" + ExplainDifference(oldMember, candidates);
        }
        return change;
    }

    public static ApiChange? MemberDeprecated(ApiMember oldMember, ApiMember newMember)
    {
        if (newMember.Deprecated == null || oldMember.Deprecated != null)
            return null;
        return new ApiChange
        {
            Id = $"member-deprecated:{oldMember.DeclaringType}::{oldMember.Key}",
            Kind = ChangeKind.Deprecated,
            Severity = newMember.Deprecated.IsError ? Severity.Breaking : Severity.Warning,
            TypeName = oldMember.DeclaringType,
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
