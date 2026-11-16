using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PackageBat.Model;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>
/// The licence a .nuspec declares: an SPDX expression, a file inside the
/// package, or (older packages) a URL. <see cref="Text"/> is the licence
/// file's contents when it was available.
/// </summary>
internal sealed record NuspecLicense(string? Expression, string? File, string? Url, bool RequireAcceptance)
{
    public string? Text { get; init; }

    /// <summary>The URL NuGet writes into licenseUrl when a package uses &lt;license&gt;; it says nothing about the licence.</summary>
    const string DeprecatedUrlPlaceholder = "https://aka.ms/deprecateLicenseUrl";

    /// <summary>Null for missing or malformed XML: a manifest we can't read has no licence to compare.</summary>
    public static NuspecLicense? Read(string? nuspecXml)
    {
        if (string.IsNullOrWhiteSpace(nuspecXml)) return null;
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(nuspecXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return null; }

        var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata == null) return null;
        string? Element(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        var value = license?.Value.Trim() is { Length: > 0 } lv ? lv : null;
        var type = ((string?)license?.Attribute("type"))?.Trim().ToLowerInvariant();
        var url = Element("licenseUrl");
        if (string.Equals(url, DeprecatedUrlPlaceholder, StringComparison.OrdinalIgnoreCase)) url = null;
        var accept = string.Equals(Element("requireLicenseAcceptance"), "true", StringComparison.OrdinalIgnoreCase);

        return new NuspecLicense(
            Expression: type == "expression" ? value : null,
            File: type == "file" ? value?.Replace('\\', '/').TrimStart('/') : null,
            Url: url,
            RequireAcceptance: accept);
    }

    /// <summary>What a reader sees: "MIT", "the file LICENSE.txt", "https://...", "no licence".</summary>
    public string Describe() =>
        Expression ?? (File != null ? $"the licence file {File}" : Url ?? "no licence");

    /// <summary>
    /// A LicenseChanged change when the licence a consumer agrees to differs
    /// between the two versions, otherwise null. Only the declared licence is
    /// compared; a licence file is compared by its text when both texts are known.
    /// </summary>
    public static ApiChange? Compare(string package, NuspecLicense? oldLicense, NuspecLicense? newLicense)
    {
        if (oldLicense == null || newLicense == null) return null;
        var reasons = new List<string>();

        var oldName = Comparable(oldLicense);
        var newName = Comparable(newLicense);
        string subject;
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
        {
            subject = $"{oldLicense.Describe()} → {newLicense.Describe()}";
            reasons.Add($"the licence changed from {oldLicense.Describe()} to {newLicense.Describe()}");
        }
        else if (oldLicense.File != null && newLicense.File != null && FileChange(oldLicense, newLicense) is { } fileChange)
        {
            subject = $"{newLicense.File} changed";
            reasons.Add(fileChange);
        }
        else subject = newLicense.Describe();

        if (!oldLicense.RequireAcceptance && newLicense.RequireAcceptance)
            reasons.Add("the package now requires accepting its licence on install");
        if (reasons.Count == 0) return null;

        var description = string.Join("; ", reasons);
        return new ApiChange
        {
            Id = $"license:{package}",
            Kind = ChangeKind.LicenseChanged,
            Severity = Severity.Warning,
            TypeName = "",
            Subject = subject,
            Description = char.ToUpperInvariant(description[0]) + description[1..],
        };
    }

    /// <summary>
    /// Both versions ship a licence file: compared by text when both texts are
    /// known (a renamed file with the same text is no change), otherwise by path.
    /// </summary>
    static string? FileChange(NuspecLicense oldLicense, NuspecLicense newLicense)
    {
        if (oldLicense.Text != null && newLicense.Text != null)
            return Normalize(oldLicense.Text) == Normalize(newLicense.Text) ? null : $"the text of the licence file {newLicense.File} changed";
        return string.Equals(oldLicense.File, newLicense.File, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"the licence file moved from {oldLicense.File} to {newLicense.File}, and its text could not be compared";
    }

    /// <summary>
    /// The licence's identity for comparing: the expression, the file, or the
    /// URL. A URL that ends in the same SPDX id as the other side's expression
    /// (licenses.nuget.org/MIT, opensource.org/licenses/MIT) counts as that id,
    /// so a package moving from licenseUrl to an expression isn't reported.
    /// </summary>
    static string Comparable(NuspecLicense l)
    {
        if (l.Expression != null) return "expr:" + Regex.Replace(l.Expression, @"\s+", " ").Trim();
        if (l.File != null) return "file";
        if (l.Url != null && SpdxFromUrl(l.Url) is { } id) return "expr:" + id;
        return l.Url != null ? "url:" + l.Url.TrimEnd('/') : "none";
    }

    static string? SpdxFromUrl(string url)
    {
        var m = Regex.Match(url, @"^https?://(?:licenses\.nuget\.org|(?:www\.)?opensource\.org/licenses?|spdx\.org/licenses)/(?<id>[A-Za-z0-9.+-]+?)(?:\.html|\.php)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>Line endings, trailing spaces and blank lines at the ends don't count as a change.</summary>
    static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd())).Trim();
}
