using System.Text.Json.Serialization;

namespace PackageBat.Model;

/// <summary>
/// Every place the consumer's code touches a package API, as found by an
/// ecosystem's usage scanner. Language-neutral; joined against ApiSurface by
/// (TypeName, MemberKey).
/// </summary>
public sealed class UsageSet
{
    public string Ecosystem { get; set; } = "";
    /// <summary>The consumer binaries or source roots that were scanned.</summary>
    public List<string> Scanned { get; set; } = new();
    public List<Usage> Usages { get; set; } = new();
}

public sealed class Usage
{
    public string TypeName { get; set; } = "";
    /// <summary>Null for a reference to the type itself (a cast, a parameter type, an attribute...).</summary>
    public string? MemberKey { get; set; }
    public UsageKind Kind { get; set; }
    public SourceLocation Location { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<UsageKind>))]
public enum UsageKind
{
    /// <summary>Calls a method or constructor, or reads/writes a property or field.</summary>
    Member,
    /// <summary>Mentions the type (cast, typeof, parameter or field type, generic argument).</summary>
    Type,
    /// <summary>Derives from the type.</summary>
    Inherit,
    /// <summary>Implements the interface.</summary>
    Implement,
    /// <summary>Overrides a virtual member. Scanners may emit candidates; unresolved ones are ignored.</summary>
    Override,
}

public sealed class SourceLocation
{
    /// <summary>Path relative to the repository root, using forward slashes. Null when unknown.</summary>
    public string? File { get; set; }
    public int? Line { get; set; }
    /// <summary>The consumer module the usage was found in.</summary>
    public string Module { get; set; } = "";
    /// <summary>The consumer symbol containing the usage, for when there is no file.</summary>
    public string? Symbol { get; set; }
}
