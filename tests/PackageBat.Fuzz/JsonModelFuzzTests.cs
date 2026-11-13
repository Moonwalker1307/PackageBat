using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using PackageBat.Analysis;
using PackageBat.Model;
using PackageBat.Report;
using Xunit;
using Xunit.Abstractions;

namespace PackageBat.Fuzz;

/// <summary>
/// The language-neutral JSON files are PackageBat's widest input: anyone can
/// write an extractor for another language, or hand-edit a file.
///
/// Crash target: `packagebat analyze --old-api a.json --new-api b.json --usages u.json`
/// is run in-process on corrupted JSON. It may report an error (exit code 1),
/// but must never die with an unhandled exception.
/// </summary>
[Trait("Category", "Fuzz")]
[Collection("Console")] // Program.Main writes to the console, so these don't run alongside other tests
public sealed class JsonModelFuzzTests(ITestOutputHelper output)
{
    sealed record Files(string OldApi, string NewApi, string Usages)
    {
        public byte[] Bundle() => Encoding.UTF8.GetBytes($"// old-api\n{OldApi}\n// new-api\n{NewApi}\n// usages\n{Usages}\n");
    }

    // Declared before CorruptFiles: static fields initialize in order, so CorruptFiles
    // must not refer to a field further down.
    readonly record struct Edit(int Kind, int Path, int Value);
    static readonly Gen<Edit> JsonEdit = Gen.Select(Gen.Int[0, 11], Gen.Int.Positive, Gen.Int.Positive, (k, p, v) => new Edit(k, p, v));

    /// <summary>Valid documents, then a few structural edits to their JSON trees.</summary>
    static readonly Gen<Files> CorruptFiles =
        ModelGen.Bump(ModelGen.Loose)
            .SelectMany(b => ModelGen.Usages(b.Old).Select(u => (b.Old, b.New, u)))
            .SelectMany(t => Gen.Select(JsonEdit.List[0, 4], JsonEdit.List[0, 4], JsonEdit.List[0, 4], Gen.Int[0, 9],
                (e1, e2, e3, raw) => new Files(
                    Corrupt(JsonSerializer.Serialize(t.Old, Program.Json), e1, raw == 0),
                    Corrupt(JsonSerializer.Serialize(t.New, Program.Json), e2, raw == 1),
                    Corrupt(JsonSerializer.Serialize(t.u, Program.Json), e3, raw == 2))));

    [Fact]
    public void Analyze_command_never_crashes_on_corrupt_json()
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            Fuzz.Crashes("analyze-json", output, CorruptFiles, files =>
            {
                var dir = Fuzz.ScratchDir;
                string Write(string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text); return p; }
                var code = Program.Main(new[]
                {
                    "analyze",
                    "--old-api", Write("old.json", files.OldApi),
                    "--new-api", Write("new.json", files.NewApi),
                    "--usages", Write("usages.json", files.Usages),
                    "--markdown", Path.Combine(dir, "report.md"),
                    "--json", Path.Combine(dir, "report.json"),
                    "--fail-on", "warning",
                    "--link-base", "https://example.test/blob/sha",
                }).GetAwaiter().GetResult();
                if (code is not (Program.ExitOk or Program.ExitError or Program.ExitThreshold))
                    throw new InvalidOperationException($"Unexpected exit code {code}");
            }, _ => false, f => f.Bundle(), ".json.txt");
        }
        finally
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }
    }

    // ---- JSON tree edits -------------------------------------------------


    static readonly JsonNode?[] Replacements =
    {
        null, JsonValue.Create(""), JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(99),
        JsonValue.Create(int.MaxValue), JsonValue.Create(1e300), JsonValue.Create(true), new JsonArray(), new JsonObject(),
        JsonValue.Create("Breaking"), JsonValue.Create("NoSuchKind"), JsonValue.Create("\u0000\ud800"), JsonValue.Create("System.Object"),
        JsonValue.Create(".ctor("), JsonValue.Create(new string('x', 5000)),
    };

    static string Corrupt(string json, List<Edit> edits, bool rawBytes)
    {
        var root = JsonNode.Parse(json)!;
        foreach (var e in edits)
        {
            var nodes = Walk(root).ToList();
            var target = nodes[e.Path % nodes.Count];
            var replacement = Replacements[e.Value % Replacements.Length]?.DeepClone();
            switch (e.Kind)
            {
                case <= 4: // replace a value (or the whole document)
                    if (target.Parent == null) root = replacement ?? JsonValue.Create(0)!;
                    else Replace(target, replacement);
                    break;
                case 5: // remove a property / array item
                    if (target.Parent is JsonObject po) po.Remove(target.GetPropertyName());
                    else if (target.Parent is JsonArray pa) pa.Remove(target);
                    break;
                case 6: // duplicate an array item (duplicate types, members, usages)
                    if (target.Parent is JsonArray da) da.Add(target.DeepClone());
                    break;
                case 7: // add an unknown property
                    if (target is JsonObject uo) uo[$"extra{e.Value % 3}"] = replacement;
                    break;
                case 8: // swap with another node of the document
                {
                    var other = nodes[e.Value % nodes.Count];
                    if (other.Parent != null && target.Parent != null && !IsAncestor(other, target) && !IsAncestor(target, other))
                    {
                        var a = target.DeepClone();
                        Replace(target, other.DeepClone());
                        Replace(other, a);
                    }
                    break;
                }
                case 9: // deep nesting
                {
                    JsonNode deep = JsonValue.Create(1)!;
                    for (int i = 0; i < 20 + e.Value % 200; i++) deep = new JsonArray(deep);
                    if (target.Parent != null) Replace(target, deep);
                    break;
                }
                default: // insert null into an array
                    if (target is JsonArray na) na.Add(null);
                    else if (target.Parent is JsonArray npa) npa.Add(null);
                    break;
            }
        }
        var text = root.ToJsonString();
        if (rawBytes && edits.Count > 0)
        {
            // Occasionally damage the text itself: truncation or a stray character.
            var e = edits[0];
            var pos = e.Value % (text.Length + 1);
            text = e.Path % 2 == 0 ? text[..pos] : text.Insert(pos, ",]}\"\\"[e.Path % 5].ToString());
        }
        return text;
    }

    static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(p => p.Value),
            JsonArray a => a.AsEnumerable(),
            _ => Enumerable.Empty<JsonNode?>(),
        };
        foreach (var c in children.ToList())
            if (c != null)
                foreach (var d in Walk(c))
                    yield return d;
    }

    static void Replace(JsonNode target, JsonNode? replacement)
    {
        switch (target.Parent)
        {
            case JsonObject o: o[target.GetPropertyName()] = replacement; break;
            case JsonArray a: a[target.GetElementIndex()] = replacement; break;
        }
    }

    static bool IsAncestor(JsonNode maybeAncestor, JsonNode node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, maybeAncestor)) return true;
        return false;
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

/// <summary>
/// Properties of the diff and matcher on well-formed models. CsCheck shrinks a
/// failure to a small example and prints the seed to replay it with FUZZ_SEED.
/// </summary>
[Trait("Category", "Fuzz")]
public sealed class ModelPropertyTests
{
    static string Json<T>(T value) => JsonSerializer.Serialize(value, Program.Json);
    static string Print((ApiSurface Old, ApiSurface New) b) => $"old:\n{Json(b.Old)}\nnew:\n{Json(b.New)}";

    [Fact]
    public void Json_round_trip_is_lossless() =>
        Fuzz.Property(ModelGen.Loose, api =>
        {
            var once = Json(api);
            var back = JsonSerializer.Deserialize<ApiSurface>(once, Program.Json)!;
            Assert.Equal(once, Json(back));
        }, Json);

    [Fact]
    public void Comparing_a_version_with_itself_finds_no_changes() =>
        Fuzz.Property(ModelGen.WellFormed, api =>
        {
            var diff = ApiDiff.Compute(api, ModelGen.Clone(api));
            Assert.Empty(diff.Changes);
            Assert.Equal(0, diff.AddedCount);
        }, Json);

    [Fact]
    public void Comparing_a_version_with_itself_finds_no_changes_even_with_duplicate_types() =>
        Fuzz.Property(ModelGen.Loose, api =>
        {
            var diff = ApiDiff.Compute(api, ModelGen.Clone(api));
            Assert.Empty(diff.Changes);
        }, Json);

    [Fact]
    public void Diff_is_deterministic_and_well_formed() =>
        Fuzz.Property(ModelGen.Bump(ModelGen.WellFormed), b =>
        {
            var diff = ApiDiff.Compute(b.Old, b.New);
            Assert.Equal(Json(diff.Changes), Json(ApiDiff.Compute(ModelGen.Clone(b.Old), ModelGen.Clone(b.New)).Changes));
            Assert.Equal(diff.Changes.Count, diff.Changes.Select(c => c.Id).Distinct().Count());
            Assert.True(diff.AddedCount >= 0);
            foreach (var c in diff.Changes)
            {
                Assert.NotNull(b.Old.FindType(c.TypeName)); // every change is about something the old version had
                Assert.False(string.IsNullOrEmpty(c.Subject));
                Assert.False(string.IsNullOrEmpty(c.Description));
                if (c.MemberKey != null && c.Kind != ChangeKind.AbstractMemberAdded)
                    Assert.NotNull(b.Old.Resolve(c.TypeName, c.MemberKey));
            }
        }, Print);

    static readonly Gen<(ApiSurface Old, ApiSurface New, UsageSet Usages)> BumpWithUsages =
        ModelGen.Bump(ModelGen.WellFormed).SelectMany(b => ModelGen.Usages(b.Old).Select(u => (b.Old, b.New, u)));

    static string Print((ApiSurface Old, ApiSurface New, UsageSet Usages) t) => Print((t.Old, t.New)) + $"\nusages:\n{Json(t.Usages)}";

    [Fact]
    public void Every_used_change_is_also_in_the_full_change_list() =>
        Fuzz.Property(BumpWithUsages, t =>
        {
            var report = UsageMatcher.Analyze(t.Old, t.New, t.Usages);
            var all = report.AllChanges.Select(c => c.Id).ToHashSet();
            foreach (var u in report.Used)
                Assert.True(all.Contains(u.Change.Id), $"{u.Change.Id} is reported as used but missing from AllChanges");
        }, Print);

    [Fact]
    public void No_change_means_nothing_used_is_reported() =>
        Fuzz.Property(ModelGen.WellFormed.SelectMany(a => ModelGen.Usages(a).Select(u => (a, u))), t =>
        {
            var report = UsageMatcher.Analyze(t.a, ModelGen.Clone(t.a), t.u);
            Assert.Empty(report.Used);
            Assert.Empty(report.AllChanges);
        }, t => Print((t.a, t.a)) + $"\nusages:\n{Json(t.u)}");

    [Fact]
    public void Report_is_well_formed_and_renders() =>
        Fuzz.Property(BumpWithUsages, t =>
        {
            var report = UsageMatcher.Analyze(t.Old, t.New, t.Usages);
            Assert.True(report.UsedApiCount <= report.ReferenceCount);
            Assert.True(report.ReferenceCount <= t.Usages.Usages.Count);
            Assert.Equal(report.Used.Count, report.Used.Select(u => u.Change.Id).Distinct().Count());
            Assert.All(report.Used, u => Assert.NotEmpty(u.Locations));
            var full = new BlastRadiusReport { Packages = { report }, Scanned = t.Usages.Scanned };
            var md = MarkdownReport.Render(full, "https://example.test/blob/sha");
            Assert.StartsWith(MarkdownReport.Marker, md);
            Assert.Equal(Json(full), Json(JsonSerializer.Deserialize<BlastRadiusReport>(Json(full), Program.Json)));
        }, Print);
}
