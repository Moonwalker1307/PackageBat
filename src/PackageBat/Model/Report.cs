using System.Text.Json.Serialization;

namespace PackageBat.Model;

/// <summary>One public API change between two versions of a package.</summary>
public sealed class ApiChange
{
    public string Id { get; set; } = "";
    public ChangeKind Kind { get; set; }
    public Severity Severity { get; set; }
    public string TypeName { get; set; } = "";
    public string? MemberKey { get; set; }
    /// <summary>What changed, for people: "JsonConvert.SerializeObject(object)".</summary>
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Nearby APIs in the new version (other overloads, a moved type...).</summary>
    public List<string> Suggestions { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    TypeRemoved,
    MemberRemoved,
    SignatureChanged,
    AbstractMemberAdded,
    TypeSealed,
    Deprecated,
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    /// <summary>Code using this will not compile against the new version.</summary>
    Breaking = 0,
    /// <summary>Recompiling fixes it (e.g. an optional parameter was added), but old binaries break.</summary>
    BinaryOnly = 1,
    /// <summary>Still works, but is deprecated or marked for removal.</summary>
    Warning = 2,
}

public sealed class PackageReport
{
    public string Ecosystem { get; set; } = "";
    public string Package { get; set; } = "";
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string? Framework { get; set; }

    /// <summary>Changes in the package's API that the consumer's code touches.</summary>
    public List<UsedChange> Used { get; set; } = new();
    /// <summary>Every removing or breaking change in the bump, used or not.</summary>
    public List<ApiChange> AllChanges { get; set; } = new();
    public int AddedApiCount { get; set; }
    /// <summary>How many references into this package the scanner found in total.</summary>
    public int ReferenceCount { get; set; }
    /// <summary>How many distinct package APIs (types and members) those references touch.</summary>
    public int UsedApiCount { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class UsedChange
{
    public ApiChange Change { get; set; } = new();
    /// <summary>For type-level changes: the members of that type the code uses.</summary>
    public List<string> MembersUsed { get; set; } = new();
    public List<SourceLocation> Locations { get; set; } = new();
}

public sealed class BlastRadiusReport
{
    public string Tool { get; set; } = "PackageBat";
    public List<PackageReport> Packages { get; set; } = new();
    public List<string> Scanned { get; set; } = new();

    public int Count(Severity s) => Packages.Sum(p => p.Used.Count(u => u.Change.Severity == s));
}

/// <summary>A dependency version change found in a pull request.</summary>
public sealed class PackageBump
{
    public string Ecosystem { get; set; } = "";
    public string Package { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? File { get; set; }
}
