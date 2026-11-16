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
    /// <summary>
    /// The new version loads a module by name at run time (not through a
    /// compile-time reference), and nothing in the consumer's dependencies
    /// provides it. Not an API change, so it has no member and no call site.
    /// </summary>
    RuntimeDependencyMissing,
    /// <summary>
    /// The new version declares a different licence (or a changed licence
    /// file). Applies to the whole package, so it has no member and no call site.
    /// </summary>
    LicenseChanged,
    /// <summary>
    /// The change moves the package to a new version in some project files,
    /// while others in the repository still set a different version. Not an
    /// API change, so it has no member and no call site.
    /// </summary>
    VersionMismatch,
}

public static class ChangeKinds
{
    /// <summary>
    /// Findings about the package as a whole rather than one of its APIs:
    /// they have no call sites and are shown above the API table.
    /// </summary>
    public static bool IsPackageLevel(this ChangeKind kind) =>
        kind is ChangeKind.RuntimeDependencyMissing or ChangeKind.LicenseChanged or ChangeKind.VersionMismatch;
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
    /// <summary>
    /// Set when the consumer doesn't pin this package itself: the bumped package
    /// whose new version requires this newer version of it.
    /// </summary>
    public string? Via { get; set; }

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
    /// <summary>
    /// Transitive bumps that were followed but left out of Packages, because
    /// none of the consumer's code references them and nothing was found.
    /// </summary>
    public List<PackageBump> Unreferenced { get; set; } = new();
    /// <summary>Set when coverage files were given: how many affected call sites tests run.</summary>
    public CoverageSummary? Coverage { get; set; }

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
    /// <summary>For a transitive bump: the bumped package whose new version requires this one.</summary>
    public string? Via { get; set; }
    /// <summary>
    /// True for a package the change adds rather than bumps (From is empty).
    /// There is nothing to compare, but it tells the runtime checks that the
    /// consumer will have the package.
    /// </summary>
    public bool? Added { get; set; }
    /// <summary>
    /// Other project files that still set a different version of this package
    /// after the change (e.g. a project the PR didn't update). Null or empty when
    /// every file that sets it agrees.
    /// </summary>
    public List<PinnedVersion>? Elsewhere { get; set; }
}

/// <summary>A version of a package that one file in the repository sets.</summary>
public sealed class PinnedVersion
{
    public string File { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>Test coverage of the affected call sites (distinct file:line across all packages).</summary>
public sealed class CoverageSummary
{
    /// <summary>The coverage files that were read.</summary>
    public List<string> Files { get; set; } = new();
    public int CallSites { get; set; }
    /// <summary>Tests ran the line at least once.</summary>
    public int Covered { get; set; }
    /// <summary>The coverage instruments the line, but no test ran it.</summary>
    public int NotCovered { get; set; }
    /// <summary>No file/line, or the coverage doesn't include it.</summary>
    public int Unknown { get; set; }
}
