using System.Text.Json.Serialization;

namespace PackageBat.Model;

// Language-neutral description of a package's public API at one version.
// Ecosystem plugins (today: .NET) produce this; the diff, the usage matcher
// and the report only ever look at this model, never at language metadata.
//
// Identity rules every extractor must follow:
//  * ApiType.Name is the fully qualified type name, unique within the package.
//  * ApiMember.Key is unique within its type and must be produced the same way
//    by the extractor (library side) and the usage scanner (consumer side), so
//    the two can be joined by plain string equality.
//  * ParameterTypes / ReturnType use the same type-name format as ApiType.Name.

public sealed class ApiSurface
{
    public string Ecosystem { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Framework { get; set; }

    /// <summary>Binary/module names that belong to this package (assemblies, jars, modules).</summary>
    public List<string> Modules { get; set; } = new();

    public List<ApiType> Types { get; set; } = new();

    /// <summary>Types the package still exposes but now forwards to another package.</summary>
    public List<string> ForwardedTypes { get; set; } = new();

    [JsonIgnore] private Dictionary<string, ApiType>? _index;
    [JsonIgnore] private HashSet<string>? _forwarded;

    [JsonIgnore]
    public IReadOnlyDictionary<string, ApiType> TypeIndex =>
        _index ??= Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    public bool IsForwarded(string typeName) =>
        (_forwarded ??= new HashSet<string>(ForwardedTypes, StringComparer.Ordinal)).Contains(typeName);

    public ApiType? FindType(string name) => TypeIndex.TryGetValue(name, out var t) ? t : null;

    /// <summary>
    /// Finds a member the way a call site would bind to it: on the type itself,
    /// then up its base types and interfaces. A member that moved to a base type
    /// therefore still resolves and is not reported as removed.
    /// </summary>
    public ApiMember? Resolve(string typeName, string memberKey)
    {
        if (memberKey.StartsWith(".ctor(", StringComparison.Ordinal)) // constructors are not inherited
            return FindType(typeName)?.MemberIndex.GetValueOrDefault(memberKey);
        foreach (var t in Hierarchy(typeName))
            if (t.MemberIndex.TryGetValue(memberKey, out var m))
                return m;
        return null;
    }

    /// <summary>The type followed by every base type and interface that is part of this package.</summary>
    public IEnumerable<ApiType> Hierarchy(string typeName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(typeName);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name))
                continue;
            var t = FindType(name);
            if (t == null)
                continue;
            yield return t;
            if (t.BaseType is { } b) queue.Enqueue(b);
            foreach (var i in t.Interfaces) queue.Enqueue(i);
        }
    }
}

public sealed class ApiType
{
    public string Name { get; set; } = "";
    /// <summary>Short human-readable name, e.g. "JsonConvert" or "Dictionary&lt;TKey, TValue&gt;".</summary>
    public string Display { get; set; } = "";
    public string Module { get; set; } = "";
    public ApiTypeKind Kind { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    /// <summary>True when outside code can derive from the type (it has an accessible constructor).</summary>
    public bool IsInheritable { get; set; }
    public string? BaseType { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public Deprecation? Deprecated { get; set; }
    public List<ApiMember> Members { get; set; } = new();

    [JsonIgnore] private Dictionary<string, ApiMember>? _index;

    [JsonIgnore]
    public IReadOnlyDictionary<string, ApiMember> MemberIndex =>
        _index ??= Members.GroupBy(m => m.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiTypeKind>))]
public enum ApiTypeKind { Class, Struct, Interface, Enum, Delegate }

public sealed class ApiMember
{
    /// <summary>Join key, unique within the declaring type. See the identity rules above.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeclaringType { get; set; } = "";
    /// <summary>Human-readable signature, e.g. "JsonConvert.SerializeObject(object, Formatting)".</summary>
    public string Display { get; set; } = "";
    public ApiMemberKind Kind { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public string ReturnType { get; set; } = "";
    public List<ApiParameter> Parameters { get; set; } = new();
    public Deprecation? Deprecated { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ApiMemberKind>))]
public enum ApiMemberKind { Method, Constructor, Property, Event, Field }

public sealed class ApiParameter
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public bool HasDefault { get; set; }
    /// <summary>Variadic parameter (C# params, Java T..., TS ...rest).</summary>
    public bool IsParams { get; set; }
}

public sealed class Deprecation
{
    public string? Message { get; set; }
    /// <summary>True when using the API is a compile error (e.g. [Obsolete(msg, true)]).</summary>
    public bool IsError { get; set; }
}
