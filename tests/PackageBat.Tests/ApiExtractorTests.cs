using PackageBat.Ecosystems.DotNet;
using PackageBat.Model;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// Pins down every fact the API extractor reads from metadata, one at a time,
/// because each one feeds a severity decision in the diff (e.g. a missed
/// optional flag turns "binary only" into "breaking").
/// </summary>
public sealed class ApiExtractorTests : IClassFixture<ApiExtractorTests.Fixture>
{
    const string Lib = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        namespace Acme
        {
            public class Plain
            {
                public Plain() { }
                public Plain(int size, string name = "x") { }
                static Plain() { }
                public void Run() { }
                public int Count(string text) => 0;
                public static string Make() => "";
                public virtual void Hook() { }
                protected void Prot() { }
                protected internal void ProtInt() { }
                internal void Inter() { }
                private void Priv() { }
                private protected void PrivProt() { }
                public void Optional(int a, int b = 3) { }
                public void InteropOptional([Optional] int a) { }
                public void Many(params string[] items) { }
                public void ByRef(ref int value, out string text) { text = ""; }
                public T Map<T>(T input, List<T> list) => input;
                public int Size { get; set; }
                public string this[int index, string key] { get => ""; set { } }
                public event EventHandler? Changed;
                public int Field;
                public static readonly string StaticField = "";
                internal int InternalField;
                protected int ProtectedField;
                public const int Answer = 42;
                public int? MaybeNumber() => null;
                public int[,] Grid() => new int[1, 1];
                public unsafe void Ptr(int* p, delegate*<int, void> f) { }
                public nint Native(nuint x) => 0;
                public Outer.PublicNested Nest() => null!;
                public Dictionary<string, List<int>> Nested() => null!;
                [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("s")] public string? Ret(string? s) => s;
            }

            public class Derived : Plain
            {
                public sealed override void Hook() { }
            }

            public abstract class Shape : IDisposable, IComparable<Shape>
            {
                protected Shape() { }
                public abstract double Area();
                public void Dispose() { }
                public int CompareTo(Shape? other) => 0;
            }

            public sealed class Final { }
            public static class Helpers { public static void Help() { } }
            public class NoPublicCtor { private NoPublicCtor() { } public static NoPublicCtor Create() => new NoPublicCtor(); public void Use() { } }
            public class InternalCtor { internal InternalCtor() { } }
            public struct Point { public int X; }
            public interface IThing { void Do(); int Value { get; } }
            public enum Color { Red, Green = 5 }
            public delegate int Calc(int a);
            public class Box<TItem, TKey> { public TItem? Get(TKey key) => default; }

            public class Outer
            {
                public class PublicNested { }
                protected class ProtectedNested { }
                protected internal class ProtIntNested { }
                internal class InternalNested { }
                private class PrivateNested { }
                public class Deep { public class Deeper { } }
            }
            internal class Hidden { public class NestedInHidden { } }

            [Obsolete("Use Plain")] public class OldType { }
            [Obsolete] public class OldNoMessage { }
            [Obsolete("Gone", true)] public class OldError { }
            [Obsolete("Soft", false)] public class OldSoft { }

            public class Deprecations
            {
                [Obsolete("Use Next")] public void OldMethod() { }
                [Obsolete("Field gone")] public int OldField;
                [Obsolete("Prop gone", true)] public int OldProp { get; set; }
                [Obsolete("Read only")] public int OldGetter => 0;
                [Obsolete("Event gone")] public event EventHandler? OldEvent;
                public int FineProp { get; set; }
                public event EventHandler? FineEvent;
            }
        }
        public class GlobalType { }
        """;

    const string Moved = """
        namespace Acme.Moved { public class Relocated { public class Inner { } } }
        public class GlobalMoved { }
        """;

    const string Forwarder = """
        [assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Acme.Moved.Relocated))]
        [assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(GlobalMoved))]
        namespace Acme { public class Stays { } }
        """;

    public sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packagebat-extractor-tests", Guid.NewGuid().ToString("N"));
        public string LibDll { get; }
        public string ForwarderDll { get; }
        public ApiSurface Api { get; }
        public ApiSurface Forwarding { get; }

        public Fixture()
        {
            LibDll = Compiler.Compile(Path.Combine(Root, "lib"), "Acme.Lib", "1.0.0.0", new[] { ("Lib.cs", Lib) });
            var moved = Compiler.Compile(Path.Combine(Root, "moved"), "Acme.Moved", "1.0.0.0", new[] { ("Moved.cs", Moved) });
            ForwarderDll = Compiler.Compile(Path.Combine(Root, "fwd"), "Acme.Forwarder", "1.0.0.0", new[] { ("Fwd.cs", Forwarder) }, moved);
            Api = ApiExtractor.Extract(new[] { LibDll }, "Acme", "1.2.3", "net8.0");
            Forwarding = ApiExtractor.Extract(new[] { ForwarderDll }, "Acme", "2.0.0", null);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    readonly Fixture _f;
    public ApiExtractorTests(Fixture f) => _f = f;

    ApiType T(string name) => _f.Api.FindType(name)
        ?? throw new Xunit.Sdk.XunitException($"No type {name}. Got: {string.Join(", ", _f.Api.Types.Select(t => t.Name))}");

    ApiMember M(string type, string key) => T(type).MemberIndex.GetValueOrDefault(key)
        ?? throw new Xunit.Sdk.XunitException($"No member {key} on {type}. Got:\n{string.Join("\n", T(type).Members.Select(m => m.Key))}");

    ApiMember Named(string type, string name) => T(type).Members.Single(m => m.Name == name);

    // ---- surface ------------------------------------------------------------

    [Fact]
    public void Surface_carries_the_package_identity_and_module()
    {
        Assert.Equal("dotnet", _f.Api.Ecosystem);
        Assert.Equal("Acme", _f.Api.Package);
        Assert.Equal("1.2.3", _f.Api.Version);
        Assert.Equal("net8.0", _f.Api.Framework);
        Assert.Equal(new[] { "Acme.Lib" }, _f.Api.Modules);
        Assert.All(_f.Api.Types, t => Assert.Equal("Acme.Lib", t.Module));
        Assert.Empty(_f.Api.ForwardedTypes);
    }

    [Fact]
    public void Same_module_given_twice_is_listed_once()
    {
        var api = ApiExtractor.Extract(new[] { _f.LibDll, _f.LibDll }, "Acme", "1", null);
        Assert.Equal(new[] { "Acme.Lib" }, api.Modules);
    }

    [Fact]
    public void Several_assemblies_are_merged_into_one_surface()
    {
        var api = ApiExtractor.Extract(new[] { _f.LibDll, _f.ForwarderDll }, "Acme", "1", null);
        Assert.Equal(new[] { "Acme.Lib", "Acme.Forwarder" }, api.Modules);
        Assert.NotNull(api.FindType("Acme.Plain"));
        Assert.Equal("Acme.Forwarder", api.FindType("Acme.Stays")!.Module);
        Assert.Equal(2, api.ForwardedTypes.Count);
    }

    [Fact]
    public void Netmodules_are_not_assemblies_and_are_skipped()
    {
        var path = Path.Combine(_f.Root, "Part.netmodule");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Part",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class InModule { }") },
            new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.NetModule));
        using (var s = File.Create(path))
            Assert.True(compilation.Emit(s).Success);
        var api = ApiExtractor.Extract(new[] { path }, "Acme", "1", null);
        Assert.Empty(api.Modules);
        Assert.Empty(api.Types);
    }

    [Fact]
    public void Return_value_attributes_do_not_shift_parameters()
    {
        var ret = M("Acme.Plain", "Ret(System.String):System.String");
        var p = Assert.Single(ret.Parameters);
        Assert.Equal(("s", false), (p.Name, p.HasDefault));
    }

    [Fact]
    public void Type_forwards_are_recorded_with_their_full_name()
    {
        Assert.Equal(new[] { "Acme.Moved.Relocated", "GlobalMoved" }, _f.Forwarding.ForwardedTypes.Order());
        Assert.Equal(new[] { "Acme.Stays" }, _f.Forwarding.Types.Select(t => t.Name));
        Assert.Null(_f.Forwarding.Framework);
    }

    [Fact]
    public void Corrupt_metadata_is_a_bad_image()
    {
        var path = Path.Combine(_f.Root, "broken.dll");
        var bytes = File.ReadAllBytes(_f.LibDll);
        // Keep the PE headers but wreck the metadata tables.
        var copy = bytes.ToArray();
        for (int i = bytes.Length / 2; i < bytes.Length; i++) copy[i] = 0xFF;
        File.WriteAllBytes(path, copy);
        // Whichever exception System.Reflection.Metadata throws inside, callers only ever see a BadImageFormatException.
        Assert.ThrowsAny<BadImageFormatException>(() => ApiExtractor.Extract(new[] { path }, "Acme", "1", null));
    }

    [Fact]
    public void Not_a_pe_file_is_a_bad_image()
    {
        var path = Path.Combine(_f.Root, "text.dll");
        File.WriteAllText(path, "this is not an assembly");
        Assert.ThrowsAny<BadImageFormatException>(() => ApiExtractor.Extract(new[] { path }, "Acme", "1", null));
    }

    // ---- type visibility ----------------------------------------------------

    [Theory]
    [InlineData("Acme.Plain", true)]
    [InlineData("GlobalType", true)]
    [InlineData("Acme.Outer+PublicNested", true)]
    [InlineData("Acme.Outer+ProtectedNested", true)]
    [InlineData("Acme.Outer+ProtIntNested", true)]
    [InlineData("Acme.Outer+InternalNested", false)]
    [InlineData("Acme.Outer+PrivateNested", false)]
    [InlineData("Acme.Outer+Deep+Deeper", true)]
    [InlineData("Acme.Hidden", false)]
    [InlineData("Acme.Hidden+NestedInHidden", false)]
    [InlineData("<Module>", false)]
    public void Only_types_reachable_from_outside_are_extracted(string name, bool visible) =>
        Assert.Equal(visible, _f.Api.FindType(name) != null);

    // ---- type facts ---------------------------------------------------------

    [Theory]
    [InlineData("Acme.Plain", ApiTypeKind.Class)]
    [InlineData("Acme.Point", ApiTypeKind.Struct)]
    [InlineData("Acme.IThing", ApiTypeKind.Interface)]
    [InlineData("Acme.Color", ApiTypeKind.Enum)]
    [InlineData("Acme.Calc", ApiTypeKind.Delegate)]
    [InlineData("Acme.Helpers", ApiTypeKind.Class)]
    public void Type_kind_comes_from_flags_and_base_type(string name, ApiTypeKind kind) =>
        Assert.Equal(kind, T(name).Kind);

    [Theory]
    // name, abstract, sealed, inheritable
    [InlineData("Acme.Plain", false, false, true)]
    [InlineData("Acme.Shape", true, false, true)]        // protected ctor is enough to derive
    [InlineData("Acme.Final", false, true, false)]
    [InlineData("Acme.Helpers", true, true, false)]      // static class
    [InlineData("Acme.NoPublicCtor", false, false, false)]
    [InlineData("Acme.InternalCtor", false, false, false)]
    [InlineData("Acme.Point", false, true, false)]
    [InlineData("Acme.IThing", true, false, false)]
    [InlineData("Acme.Color", false, true, false)]
    public void Abstract_sealed_and_inheritable_flags(string name, bool isAbstract, bool isSealed, bool inheritable)
    {
        var t = T(name);
        Assert.Equal((isAbstract, isSealed, inheritable), (t.IsAbstract, t.IsSealed, t.IsInheritable));
    }

    [Fact]
    public void Base_type_and_interfaces_use_full_names()
    {
        Assert.Equal("System.Object", T("Acme.Plain").BaseType);
        Assert.Equal("Acme.Plain", T("Acme.Derived").BaseType);
        Assert.Equal("System.ValueType", T("Acme.Point").BaseType);
        Assert.Equal("System.Enum", T("Acme.Color").BaseType);
        Assert.Equal("System.MulticastDelegate", T("Acme.Calc").BaseType);
        Assert.Null(T("Acme.IThing").BaseType);
        // A generic interface instance is recorded as its generic definition.
        Assert.Equal(new[] { "System.IComparable`1", "System.IDisposable" }, T("Acme.Shape").Interfaces.Order());
        Assert.Empty(T("Acme.Plain").Interfaces);
    }

    [Theory]
    [InlineData("Acme.Plain", "Plain")]
    [InlineData("Acme.Box`2", "Box<TItem, TKey>")]
    [InlineData("Acme.Outer+Deep+Deeper", "Outer.Deep.Deeper")]
    [InlineData("GlobalType", "GlobalType")]
    public void Type_display_is_short_and_shows_generic_parameters(string name, string display) =>
        Assert.Equal(display, T(name).Display);

    [Fact]
    public void Type_obsolete_reads_message_and_error_flag()
    {
        Assert.Null(T("Acme.Plain").Deprecated);
        Assert.Equal(("Use Plain", false), (T("Acme.OldType").Deprecated!.Message, T("Acme.OldType").Deprecated!.IsError));
        Assert.Equal(((string?)null, false), (T("Acme.OldNoMessage").Deprecated!.Message, T("Acme.OldNoMessage").Deprecated!.IsError));
        Assert.Equal(("Gone", true), (T("Acme.OldError").Deprecated!.Message, T("Acme.OldError").Deprecated!.IsError));
        Assert.Equal(("Soft", false), (T("Acme.OldSoft").Deprecated!.Message, T("Acme.OldSoft").Deprecated!.IsError));
    }

    // ---- member visibility --------------------------------------------------

    [Theory]
    [InlineData("Run", true)]
    [InlineData("Prot", true)]
    [InlineData("ProtInt", true)]
    [InlineData("Inter", false)]
    [InlineData("Priv", false)]
    [InlineData("PrivProt", false)]
    [InlineData("Field", true)]
    [InlineData("ProtectedField", true)]
    [InlineData("InternalField", false)]
    [InlineData("<Changed>k__BackingField", false)]
    public void Only_members_reachable_from_outside_are_extracted(string name, bool visible) =>
        Assert.Equal(visible, T("Acme.Plain").Members.Any(m => m.Name == name));

    // ---- methods ------------------------------------------------------------

    [Fact]
    public void Method_keys_match_the_scanner_format()
    {
        var keys = T("Acme.Plain").Members.Select(m => m.Key).ToHashSet();
        Assert.Contains("Run():System.Void", keys);
        Assert.Contains("Count(System.String):System.Int32", keys);
        Assert.Contains("Make():System.String static", keys);
        Assert.Contains("Map``1(!!0,System.Collections.Generic.List`1<!!0>):!!0", keys);
        Assert.Contains("ByRef(System.Int32&,System.String&):System.Void", keys);
        Assert.Contains(".ctor():System.Void", keys);
        Assert.Contains(".ctor(System.Int32,System.String):System.Void", keys);
        Assert.DoesNotContain(".cctor():System.Void static", keys); // static constructors are private
        Assert.Contains("Field|System.Int32", keys);
        Assert.Contains("StaticField|System.String", keys);
        Assert.Contains("MaybeNumber():System.Nullable`1<System.Int32>", keys);
        Assert.Contains("Grid():System.Int32[,]", keys);
    }

    [Fact]
    public void Method_facts()
    {
        var count = M("Acme.Plain", "Count(System.String):System.Int32");
        Assert.Equal(("Count", "Acme.Plain", ApiMemberKind.Method, "System.Int32"), (count.Name, count.DeclaringType, count.Kind, count.ReturnType));
        Assert.Equal((false, false, false), (count.IsStatic, count.IsAbstract, count.IsVirtual));
        var p = Assert.Single(count.Parameters);
        Assert.Equal(("System.String", "text", false, false), (p.Type, p.Name, p.HasDefault, p.IsParams));

        Assert.True(M("Acme.Plain", "Make():System.String static").IsStatic);
        Assert.Null(M("Acme.Plain", "Run():System.Void").Deprecated);
    }

    [Theory]
    // type, member name, virtual, abstract
    [InlineData("Acme.Plain", "Hook", true, false)]
    [InlineData("Acme.Plain", "Run", false, false)]
    [InlineData("Acme.Derived", "Hook", false, false)]  // sealed override can't be overridden again
    [InlineData("Acme.Shape", "Area", true, true)]
    [InlineData("Acme.Shape", "Dispose", false, false)] // implicit interface impl is virtual+final in IL
    [InlineData("Acme.IThing", "Do", true, true)]
    public void Virtual_and_abstract_flags(string type, string name, bool isVirtual, bool isAbstract)
    {
        var m = Named(type, name);
        Assert.Equal((isVirtual, isAbstract), (m.IsVirtual, m.IsAbstract));
    }

    [Fact]
    public void Optional_parameters_from_defaults_and_the_optional_attribute()
    {
        var opt = M("Acme.Plain", "Optional(System.Int32,System.Int32):System.Void").Parameters;
        Assert.Equal(new (string?, bool)[] { ("a", false), ("b", true) }, opt.Select(p => (p.Name, p.HasDefault)));
        Assert.True(Assert.Single(M("Acme.Plain", "InteropOptional(System.Int32):System.Void").Parameters).HasDefault);
        var ctor = M("Acme.Plain", ".ctor(System.Int32,System.String):System.Void").Parameters;
        Assert.Equal(new (string?, bool)[] { ("size", false), ("name", true) }, ctor.Select(p => (p.Name, p.HasDefault)));
        Assert.All(opt.Concat(ctor), p => Assert.False(p.IsParams));
    }

    [Fact]
    public void Params_arrays_are_flagged()
    {
        var p = Assert.Single(M("Acme.Plain", "Many(System.String[]):System.Void").Parameters);
        Assert.Equal(("System.String[]", "items", true, false), (p.Type, p.Name, p.IsParams, p.HasDefault));
    }

    [Theory]
    [InlineData("Run():System.Void", "Plain.Run()")]
    [InlineData("Count(System.String):System.Int32", "Plain.Count(string) : int")]
    [InlineData("Make():System.String static", "Plain.Make() : string")]
    [InlineData("Optional(System.Int32,System.Int32):System.Void", "Plain.Optional(int, int)")]
    [InlineData("ByRef(System.Int32&,System.String&):System.Void", "Plain.ByRef(ref int, ref string)")]
    [InlineData("Map``1(!!0,System.Collections.Generic.List`1<!!0>):!!0", "Plain.Map<T>(T, List<T>) : T")]
    [InlineData("MaybeNumber():System.Nullable`1<System.Int32>", "Plain.MaybeNumber() : int?")]
    [InlineData("Grid():System.Int32[,]", "Plain.Grid() : int[,]")]
    [InlineData("Ptr(System.Int32*,delegate*<System.Int32,System.Void>):System.Void", "Plain.Ptr(int*, delegate*<int,void>)")]
    [InlineData("Native(System.UIntPtr):System.IntPtr", "Plain.Native(nuint) : nint")]
    [InlineData("Nest():Acme.Outer+PublicNested", "Plain.Nest() : Outer.PublicNested")]
    [InlineData("Nested():System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32>>", "Plain.Nested() : Dictionary<string, List<int>>")]
    [InlineData(".ctor():System.Void", "new Plain()")]
    [InlineData(".ctor(System.Int32,System.String):System.Void", "new Plain(int, string)")]
    [InlineData("get_Size():System.Int32", "Plain.Size (get) : int")]
    [InlineData("set_Size(System.Int32):System.Void", "Plain.Size (set)")]
    [InlineData("get_Item(System.Int32,System.String):System.String", "Plain.Item[int, string] (get) : string")]
    [InlineData("set_Item(System.Int32,System.String,System.String):System.Void", "Plain.Item[int, string] (set)")]
    [InlineData("add_Changed(System.EventHandler):System.Void", "Plain.Changed (event add)")]
    [InlineData("remove_Changed(System.EventHandler):System.Void", "Plain.Changed (event remove)")]
    [InlineData("Field|System.Int32", "Plain.Field (field: int)")]
    [InlineData("Answer|System.Int32", "Plain.Answer (field: int)")]
    public void Member_display(string key, string display) =>
        Assert.Equal(display, M("Acme.Plain", key).Display);

    [Fact]
    public void Generic_type_parameters_are_named_in_display_and_numbered_in_keys()
    {
        var get = Named("Acme.Box`2", "Get");
        Assert.Equal("Get(!1):!0", get.Key);
        Assert.Equal("Box<TItem, TKey>.Get(TKey) : TItem", get.Display);
    }

    [Theory]
    [InlineData("get_Size", ApiMemberKind.Property)]
    [InlineData("set_Size", ApiMemberKind.Property)]
    [InlineData("get_Item", ApiMemberKind.Property)]
    [InlineData("add_Changed", ApiMemberKind.Event)]
    [InlineData("remove_Changed", ApiMemberKind.Event)]
    [InlineData(".ctor", ApiMemberKind.Constructor)]
    [InlineData("Run", ApiMemberKind.Method)]
    [InlineData("Field", ApiMemberKind.Field)]
    public void Member_kind(string name, ApiMemberKind kind)
    {
        var members = T("Acme.Plain").Members.Where(m => m.Name == name).ToList();
        Assert.NotEmpty(members);
        Assert.All(members, m => Assert.Equal(kind, m.Kind));
    }

    [Fact]
    public void Methods_named_like_accessors_are_still_methods()
    {
        const string src = """
            namespace Acme
            {
                public class Odd
                {
                    public int get_Thing() => 0;
                    public void set_Thing(int v) { }
                    public void add_Thing(int v) { }
                    public void remove_Thing(int v) { }
                }
            }
            """;
        var dll = Compiler.Compile(Path.Combine(_f.Root, "odd"), "Acme.Odd", "1.0.0.0", new[] { ("Odd.cs", src) });
        var odd = ApiExtractor.Extract(new[] { dll }, "Acme", "1", null).FindType("Acme.Odd")!;
        Assert.All(odd.Members.Where(m => m.Kind != ApiMemberKind.Constructor), m => Assert.Equal(ApiMemberKind.Method, m.Kind));
        Assert.Equal("Odd.get_Thing() : int", odd.Members.Single(m => m.Name == "get_Thing").Display);
        Assert.Equal("Odd.add_Thing(int)", odd.Members.Single(m => m.Name == "add_Thing").Display);
    }

    // ---- fields -------------------------------------------------------------

    [Fact]
    public void Field_facts()
    {
        var f = M("Acme.Plain", "Field|System.Int32");
        Assert.Equal(("Field", "Acme.Plain", ApiMemberKind.Field, "System.Int32", false), (f.Name, f.DeclaringType, f.Kind, f.ReturnType, f.IsStatic));
        Assert.True(M("Acme.Plain", "StaticField|System.String").IsStatic);
        Assert.True(M("Acme.Plain", "Answer|System.Int32").IsStatic);
        Assert.Empty(f.Parameters);
    }

    [Fact]
    public void Enum_values_are_listed_without_the_value_field()
    {
        var color = T("Acme.Color");
        Assert.Equal(new[] { "Green", "Red" }, color.Members.Where(m => m.Kind == ApiMemberKind.Field).Select(m => m.Name).Order());
        Assert.DoesNotContain(color.Members, m => m.Name == "value__");
        Assert.Equal("Color.Red (enum value)", M("Acme.Color", "Red|Acme.Color").Display);
        Assert.Equal("Point.X (field: int)", M("Acme.Point", "X|System.Int32").Display);
    }

    // ---- obsolete members ---------------------------------------------------

    [Fact]
    public void Obsolete_methods_and_fields()
    {
        var m = M("Acme.Deprecations", "OldMethod():System.Void").Deprecated!;
        Assert.Equal(("Use Next", false), (m.Message, m.IsError));
        Assert.Equal("Field gone", M("Acme.Deprecations", "OldField|System.Int32").Deprecated!.Message);
    }

    [Fact]
    public void Obsolete_on_a_property_or_event_marks_its_accessors()
    {
        var get = M("Acme.Deprecations", "get_OldProp():System.Int32").Deprecated!;
        var set = M("Acme.Deprecations", "set_OldProp(System.Int32):System.Void").Deprecated!;
        Assert.Equal(("Prop gone", true), (get.Message, get.IsError));
        Assert.Equal(("Prop gone", true), (set.Message, set.IsError));
        Assert.Equal("Read only", M("Acme.Deprecations", "get_OldGetter():System.Int32").Deprecated!.Message);
        Assert.Equal("Event gone", M("Acme.Deprecations", "add_OldEvent(System.EventHandler):System.Void").Deprecated!.Message);
        Assert.Equal("Event gone", M("Acme.Deprecations", "remove_OldEvent(System.EventHandler):System.Void").Deprecated!.Message);
    }

    [Fact]
    public void Accessors_of_normal_properties_and_events_are_not_obsolete()
    {
        Assert.Null(M("Acme.Deprecations", "get_FineProp():System.Int32").Deprecated);
        Assert.Null(M("Acme.Deprecations", "set_FineProp(System.Int32):System.Void").Deprecated);
        Assert.Null(M("Acme.Deprecations", "add_FineEvent(System.EventHandler):System.Void").Deprecated);
        Assert.Null(M("Acme.Deprecations", "remove_FineEvent(System.EventHandler):System.Void").Deprecated);
    }
}
