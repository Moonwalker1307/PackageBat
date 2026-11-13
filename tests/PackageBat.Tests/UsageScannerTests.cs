using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// Checks each way the usage scanner finds a reference into the package
/// (IL operands, signatures, attributes, inheritance, overrides), and that it
/// reports the right kind, member key, file, line and symbol for each.
/// </summary>
public sealed class UsageScannerTests : IClassFixture<UsageScannerTests.Fixture>
{
    const string Lib = """
        using System;
        namespace Acme
        {
            public class Widget
            {
                public int Size;
                public static int Count;
                public void Spin() { }
                public static void Reset() { }
                public static T Echo<T>(T value) => value;
                public virtual void Draw() { }
                public virtual void Paint() { }
                public int Height { get; set; }
            }
            public struct Gadget { public int X; }
            public interface IPlugin { void Load(); }
            public class TagAttribute : Attribute { public TagAttribute(string name) { } }
            public static class Generic { public static void Use<T>() { } }
            public class Holder<T> { public static void Hold() { } }
            public class Outer { public class Inner { public static void Go() { } } }
        }
        public class GlobalThing { public static void Go() { } }
        """;

    // Line numbers matter: the assertions below refer to them.
    const string App = """
        using System;
        using System.Collections.Generic;
        using Acme;

        public class App
        {
            public Widget? Stored;

            public void Calls()
            {
                long big = 1234567890123L;
                double d = 3.25;
                var w = new Widget();
                w.Spin();
                Widget.Reset();
                w.Size = (int)big + (int)d;
            }

            public int Switch(int n, Widget w)
            {
                switch (n)
                {
                    case 0: return 1; case 1: return 2;
                    case 2: return 3; case 3: return 4;
                    case 4: return 5; case 5: return 6;
                    case 6: return 7; case 7: return 8;
                }
                return Widget.Count + w.Height;
            }

            public Type Tokens()
            {
                var g = default(Gadget);
                Action a = Widget.Reset;
                var list = new List<Gadget>();
                Generic.Use<Gadget>();
                Holder<int>.Hold();
                return typeof(Widget);
            }

            public int Echo() => Widget.Echo(5);

            public Widget Sig(Widget w, Gadget g) => w;

            public void NoPackage() { Console.WriteLine("hi"); }
        }

        [Tag("app")]
        public class Tagged
        {
            [Tag("field")] public int F;
            [Tag("method")] public void M() { }
            [Tag("prop")] public int P { get; set; }
        }

        public class MyWidget : Widget
        {
            public override void Draw() { }
            public new virtual void Paint() { }
            public void Own() { }
        }

        public class Plugin : IPlugin
        {
            void IPlugin.Load() { }
        }

        public class Init
        {
            public Widget W = new Widget();
            public void Later() { }
            public void Param([Tag("p")] int x) { }
            [Tag("setter")] public int SetOnly { set { } }
            public void Nested() { Outer.Inner.Go(); GlobalThing.Go(); }
        }
        """;

    // A consumer that references System only: must not be scanned at all.
    const string Unrelated = """
        public class Nothing { public void Run() { System.Console.WriteLine(); } }
        """;

    public sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packagebat-scanner-tests", Guid.NewGuid().ToString("N"));
        public string RepoRoot => Path.Combine(Root, "repo");
        public string LibDll { get; }
        public string AppDll { get; }
        public ApiSurface Api { get; }
        public UsageSet Usages { get; }
        public string Log { get; }

        public Fixture()
        {
            LibDll = Compiler.Compile(Path.Combine(Root, "lib"), "Acme.Lib", "1.0.0.0", new[] { ("Lib.cs", Lib) });
            var bin = Path.Combine(RepoRoot, "bin");
            AppDll = Compiler.Compile(bin, "Consumer", "1.0.0.0", new[]
            {
                (Path.Combine(RepoRoot, "src", "App.cs"), App),
                (Path.Combine(RepoRoot, "src", "Asm.cs"), "[assembly: Acme.Tag(\"asm\")]"),
            }, LibDll);
            Compiler.Compile(bin, "Unrelated", "1.0.0.0", new[] { (Path.Combine(RepoRoot, "src", "Unrelated.cs"), Unrelated) });
            // The same build copied to a second output folder must be scanned once.
            var copy = Path.Combine(RepoRoot, "tests", "bin");
            Directory.CreateDirectory(copy);
            File.Copy(AppDll, Path.Combine(copy, "Consumer.dll"));
            File.Copy(Path.ChangeExtension(AppDll, ".pdb"), Path.Combine(copy, "Consumer.pdb"));

            Api = ApiExtractor.Extract(new[] { LibDll }, "Acme.Lib", "1.0.0", null);
            var log = new StringWriter();
            Usages = UsageScanner.Scan(new[] { RepoRoot }, Api, RepoRoot, log);
            Log = log.ToString();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    readonly Fixture _f;
    public UsageScannerTests(Fixture f) => _f = f;

    IEnumerable<Usage> In(string symbol) => _f.Usages.Usages.Where(u => u.Location.Symbol == symbol);

    static string Show(IEnumerable<Usage> us) => string.Join("\n", us.Select(u => $"{u.Kind} {u.TypeName} {u.MemberKey} @ {u.Location.File}:{u.Location.Line} [{u.Location.Symbol}]"));

    Usage One(string symbol, UsageKind kind, string type, string? key)
    {
        var found = In(symbol).Where(u => u.Kind == kind && u.TypeName == type && u.MemberKey == key).ToList();
        if (found.Count == 0)
            throw new Xunit.Sdk.XunitException($"No {kind} {type} {key} in {symbol}. Got:\n{Show(_f.Usages.Usages)}");
        return found[0];
    }

    // ---- which assemblies -----------------------------------------------------

    [Fact]
    public void Scans_each_build_once_and_skips_assemblies_without_package_references()
    {
        Assert.Equal(new[] { "bin/Consumer.dll" }, _f.Usages.Scanned.Select(s => s.Replace('\\', '/')).Where(s => s.EndsWith("Consumer.dll")).Take(1));
        Assert.Single(_f.Usages.Scanned);
        Assert.Equal("dotnet", _f.Usages.Ecosystem);
        Assert.Contains($"Scanned Consumer: {_f.Usages.Usages.Count} references", _f.Log);
        Assert.DoesNotContain("Unrelated", _f.Log);
        Assert.DoesNotContain("warning", _f.Log);
    }

    [Fact]
    public void Every_usage_names_the_consumer_module()
    {
        Assert.NotEmpty(_f.Usages.Usages);
        Assert.All(_f.Usages.Usages, u => Assert.Equal("Consumer", u.Location.Module));
        Assert.All(_f.Usages.Usages, u => Assert.True(u.TypeName.StartsWith("Acme.") || u.TypeName == "GlobalThing", u.TypeName));
    }

    [Fact]
    public void Code_that_does_not_touch_the_package_gives_nothing() =>
        Assert.Empty(In("App.NoPackage"));

    // ---- IL operands ----------------------------------------------------------

    [Fact]
    public void Calls_and_field_access_get_the_line_of_their_statement()
    {
        Assert.Equal(13, One("App.Calls", UsageKind.Member, "Acme.Widget", ".ctor():System.Void").Location.Line);
        Assert.Equal(14, One("App.Calls", UsageKind.Member, "Acme.Widget", "Spin():System.Void").Location.Line);
        Assert.Equal(15, One("App.Calls", UsageKind.Member, "Acme.Widget", "Reset():System.Void static").Location.Line);
        Assert.Equal(16, One("App.Calls", UsageKind.Member, "Acme.Widget", "Size|System.Int32").Location.Line);
        Assert.All(In("App.Calls"), u => Assert.Equal("src/App.cs", u.Location.File));
        Assert.Equal(4, In("App.Calls").Count());
    }

    [Fact]
    public void Switch_tables_are_skipped_without_losing_the_code_after_them()
    {
        Assert.Equal(28, One("App.Switch", UsageKind.Member, "Acme.Widget", "Count|System.Int32").Location.Line);
        Assert.Equal(28, One("App.Switch", UsageKind.Member, "Acme.Widget", "get_Height():System.Int32").Location.Line);
        // The parameter type is a signature usage, located at the method's first line.
        Assert.Equal(20, One("App.Switch", UsageKind.Type, "Acme.Widget", null).Location.Line);
    }

    [Fact]
    public void Type_tokens_two_byte_opcodes_and_generic_instantiations()
    {
        Assert.Equal(33, One("App.Tokens", UsageKind.Type, "Acme.Gadget", null).Location.Line);            // initobj (0xFE prefix)
        Assert.Equal(34, One("App.Tokens", UsageKind.Member, "Acme.Widget", "Reset():System.Void static").Location.Line); // ldftn (0xFE prefix)
        Assert.Contains(In("App.Tokens"), u => u is { Kind: UsageKind.Type, TypeName: "Acme.Gadget", Location.Line: 35 });  // List<Gadget> TypeSpec
        Assert.Contains(In("App.Tokens"), u => u is { Kind: UsageKind.Type, TypeName: "Acme.Gadget", Location.Line: 36 });  // Use<Gadget> MethodSpec
        Assert.Contains(In("App.Tokens"), u => u is { Kind: UsageKind.Member, TypeName: "Acme.Generic", MemberKey: "Use``1():System.Void static", Location.Line: 36 });
        Assert.Contains(In("App.Tokens"), u => u is { Kind: UsageKind.Member, TypeName: "Acme.Holder`1", MemberKey: "Hold():System.Void static", Location.Line: 37 });
        Assert.Contains(In("App.Tokens"), u => u is { Kind: UsageKind.Type, TypeName: "Acme.Widget", Location.Line: 38 });  // typeof (ldtoken)
    }

    [Fact]
    public void Generic_method_instantiated_with_a_framework_type_is_a_member_usage_only()
    {
        var echo = In("App.Echo").ToList();
        var u = Assert.Single(echo);
        Assert.Equal((UsageKind.Member, "Acme.Widget", "Echo``1(!!0):!!0 static", 41), (u.Kind, u.TypeName, u.MemberKey, u.Location.Line));
    }

    [Fact]
    public void Signature_types_are_reported_once_each_at_the_first_line()
    {
        var sig = In("App.Sig").ToList();
        Assert.All(sig, u => Assert.Equal(UsageKind.Type, u.Kind));
        Assert.Equal(new[] { "Acme.Gadget", "Acme.Widget" }, sig.Select(u => u.TypeName).Order()); // Widget is both a parameter and the return type
        Assert.All(sig, u => Assert.Equal(43, u.Location.Line));
    }

    [Fact]
    public void Field_of_a_package_type_is_reported_on_the_type()
    {
        var u = One("App.Stored", UsageKind.Type, "Acme.Widget", null);
        Assert.Equal("src/App.cs", u.Location.File);
        Assert.Equal(10, u.Location.Line); // the first line of App's first method stands in for the class
    }

    [Fact]
    public void Type_location_is_the_earliest_line_of_its_methods_not_the_first_method()
    {
        // The field initializer lives in the constructor, which comes after Later() in metadata but sits on an earlier line.
        Assert.Equal(70, One("Init.W", UsageKind.Type, "Acme.Widget", null).Location.Line);
        Assert.Equal(70, One("Init..ctor", UsageKind.Member, "Acme.Widget", ".ctor():System.Void").Location.Line);
    }

    [Fact]
    public void Implicit_base_constructor_call_has_no_line()
    {
        var u = One("MyWidget..ctor", UsageKind.Member, "Acme.Widget", ".ctor():System.Void");
        Assert.Equal(((string?)null, (int?)null), (u.Location.File, u.Location.Line));
    }

    // ---- attributes -------------------------------------------------------------

    [Theory]
    [InlineData("Tagged", 52)]   // type and field: the type's earliest method line
    [InlineData("Tagged.F", 52)]
    [InlineData("Tagged.M", 52)]
    [InlineData("P", 53)]        // property: its getter
    public void Attribute_usages_are_located_on_what_they_decorate(string symbol, int line)
    {
        var u = One(symbol, UsageKind.Member, "Acme.TagAttribute", ".ctor(System.String):System.Void");
        Assert.Equal(("src/App.cs", line), (u.Location.File, u.Location.Line));
    }

    [Fact]
    public void Attribute_on_a_set_only_property_is_located_on_its_setter()
    {
        var u = One("SetOnly", UsageKind.Member, "Acme.TagAttribute", ".ctor(System.String):System.Void");
        Assert.Equal(73, u.Location.Line);
    }

    [Fact]
    public void Attributes_on_parameters_and_the_assembly_have_no_line_and_name_the_module()
    {
        var tags = _f.Usages.Usages.Where(u => u.TypeName == "Acme.TagAttribute" && u.Location.Symbol == "Consumer").ToList();
        Assert.Equal(2, tags.Count); // [assembly: Tag] and [Tag] on a parameter
        Assert.All(tags, u => Assert.Equal(((string?)null, (int?)null), (u.Location.File, u.Location.Line)));
    }

    // ---- type shape ---------------------------------------------------------------

    [Fact]
    public void Subclass_reports_inherit_and_every_virtual_it_might_override()
    {
        Assert.Equal(58, One("MyWidget", UsageKind.Inherit, "Acme.Widget", null).Location.Line);
        Assert.Equal(58, One("MyWidget.Draw", UsageKind.Override, "Acme.Widget", "Draw():System.Void").Location.Line);
        // "new virtual" starts a new slot, and non-virtual methods can't override anything.
        Assert.DoesNotContain(In("MyWidget.Paint"), u => u.Kind == UsageKind.Override);
        Assert.Empty(In("MyWidget.Own"));
        Assert.DoesNotContain(_f.Usages.Usages, u => u.Location.Symbol!.StartsWith("App") && u.Kind is UsageKind.Inherit or UsageKind.Override);
    }

    [Fact]
    public void Interface_implementation_and_explicit_implementation()
    {
        Assert.Equal(65, One("Plugin", UsageKind.Implement, "Acme.IPlugin", null).Location.Line);
        var u = One("Plugin.Acme.IPlugin.Load", UsageKind.Override, "Acme.IPlugin", "Load():System.Void");
        Assert.Equal(("src/App.cs", 65), (u.Location.File, u.Location.Line));
    }

    // ---- missing or broken PDBs and files -----------------------------------------

    [Fact]
    public void Without_a_pdb_usages_have_no_location_but_are_still_found()
    {
        var dir = Path.Combine(_f.Root, "nopdb");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "Consumer.dll");
        File.Copy(_f.AppDll, dll);
        var log = new StringWriter();
        var set = UsageScanner.Scan(new[] { dll }, _f.Api, dir, log);

        Assert.Equal(_f.Usages.Usages.Count, set.Usages.Count);
        Assert.All(set.Usages, u => Assert.Equal(((string?)null, (int?)null), (u.Location.File, u.Location.Line)));
        Assert.All(set.Usages, u => Assert.NotNull(u.Location.Symbol));
        Assert.Contains("warning: no portable PDB for", log.ToString());
        Assert.DoesNotContain("could not read", log.ToString()); // a missing PDB is not an error
        Assert.Equal(new[] { "Consumer.dll" }, set.Scanned);
    }

    [Fact]
    public void Corrupt_pdb_is_a_warning_and_usages_lose_their_location()
    {
        var dir = Path.Combine(_f.Root, "badpdb");
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "Consumer.dll");
        File.Copy(_f.AppDll, dll);
        File.WriteAllText(Path.Combine(dir, "Consumer.pdb"), "BSJB but not really a pdb");
        var log = new StringWriter();
        var set = UsageScanner.Scan(new[] { dll }, _f.Api, dir, log);

        Assert.Contains($"warning: could not read the PDB for {dll}", log.ToString());
        Assert.Contains($"warning: no portable PDB for {dll}; usages will have no file/line.", log.ToString());
        Assert.Equal(_f.Usages.Usages.Count, set.Usages.Count);
        Assert.All(set.Usages, u => Assert.Null(u.Location.Line));
    }

    [Fact]
    public void Unreadable_files_are_skipped_with_a_warning()
    {
        var dir = Path.Combine(_f.Root, "junk");
        Directory.CreateDirectory(dir);
        var junk = Path.Combine(dir, "Junk.dll");
        File.WriteAllText(junk, "not a PE file");
        var log = new StringWriter();
        var set = UsageScanner.Scan(new[] { junk, _f.AppDll }, _f.Api, _f.RepoRoot, log);
        Assert.Contains($"warning: skipped {junk}: not a readable .NET assembly", log.ToString());
        Assert.Equal(new[] { "bin/Consumer.dll" }, set.Scanned);
        Assert.Equal(_f.Usages.Usages.Count, set.Usages.Count);
    }

    [Fact]
    public void The_package_itself_is_never_scanned()
    {
        var set = UsageScanner.Scan(new[] { _f.LibDll }, _f.Api, _f.Root, TextWriter.Null);
        Assert.Empty(set.Scanned);
        Assert.Empty(set.Usages);
    }

    [Fact]
    public void Netmodules_are_skipped_quietly()
    {
        var path = Path.Combine(_f.Root, "Part.netmodule");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Part",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class InModule { }") },
            new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.NetModule));
        using (var s = File.Create(path))
            Assert.True(compilation.Emit(s).Success);
        var log = new StringWriter();
        var set = UsageScanner.Scan(new[] { path }, _f.Api, _f.Root, log);
        Assert.Empty(set.Scanned);
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void Nested_and_global_namespace_package_types_get_their_full_names()
    {
        Assert.Equal(74, One("Init.Nested", UsageKind.Member, "Acme.Outer+Inner", "Go():System.Void static").Location.Line);
        Assert.Equal(74, One("Init.Nested", UsageKind.Member, "GlobalThing", "Go():System.Void static").Location.Line);
    }

    [Fact]
    public void Package_module_names_match_case_insensitively()
    {
        var api = new ApiSurface { Modules = { "ACME.LIB" } };
        var set = UsageScanner.Scan(new[] { _f.AppDll }, api, _f.RepoRoot, TextWriter.Null);
        Assert.Contains(set.Usages, u => u.TypeName == "Acme.Widget" && u.MemberKey == "Spin():System.Void");
    }

    // ---- FindAssemblies / Rel -----------------------------------------------------

    [Fact]
    public void Find_assemblies_takes_build_outputs_with_a_pdb_and_skips_noise_folders()
    {
        var root = Path.Combine(_f.Root, "find");
        void Touch(params string[] parts)
        {
            var p = Path.Combine(new[] { root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }
        Touch("bin", "A.dll"); Touch("bin", "A.pdb");
        Touch("bin", "Tool.EXE"); Touch("bin", "Tool.pdb");
        Touch("bin", "NoPdb.dll");
        Touch("bin", "Notes.txt"); Touch("bin", "Notes.pdb");
        Touch("obj", "B.dll"); Touch("obj", "B.pdb");
        Touch(".git", "C.dll"); Touch(".git", "C.pdb");
        Touch("node_modules", "x", "D.dll"); Touch("node_modules", "x", "D.pdb");
        Touch("src", "objects", "E.dll"); Touch("src", "objects", "E.pdb"); // "obj" only as a whole folder name

        var found = UsageScanner.FindAssemblies(new[] { root, Path.Combine(root, "missing") })
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order().ToList();
        Assert.Equal(new[] { "bin/A.dll", "bin/Tool.EXE", "src/objects/E.dll" }, found);
    }

    [Fact]
    public void Find_assemblies_returns_explicit_files_as_full_paths_even_without_a_pdb()
    {
        var dir = Path.Combine(_f.Root, "explicit");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Lone.dll");
        File.WriteAllText(file, "");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, file);
        Assert.Equal(new[] { file }, UsageScanner.FindAssemblies(new[] { relative }));
    }

    [Theory]
    [InlineData("/_/src/App.cs", "/repo", "src/App.cs")]
    [InlineData("/repo/src/App.cs", "/repo", "src/App.cs")]
    [InlineData("/repo/src/App.cs", "/repo/", "src/App.cs")]
    [InlineData("/REPO/src/App.cs", "/repo", "src/App.cs")]
    [InlineData("/other/App.cs", "/repo", "/other/App.cs")]
    [InlineData("/repository/App.cs", "/repo", "/repository/App.cs")]
    [InlineData("/x/_/App.cs", "/repo", "/x/_/App.cs")]
    public void Rel_makes_paths_repo_relative(string path, string root, string expected) =>
        Assert.Equal(expected, UsageScanner.Rel(path, root));

    [Fact]
    public void Rel_normalises_backslashes()
    {
        Assert.Equal("src/App.cs", UsageScanner.Rel(@"\_\src\App.cs", "/repo"));
        Assert.Equal("src/App.cs", UsageScanner.Rel(@"\repo\src\App.cs", "/repo"));
    }
}
