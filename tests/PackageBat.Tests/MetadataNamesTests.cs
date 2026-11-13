using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using PackageBat.Ecosystems.DotNet;
using Xunit;

namespace PackageBat.Tests;

/// <summary>
/// The key format is the join between the package's API and the consumer's
/// usages, so every piece of it is pinned here; the display format is what
/// people read in the PR comment.
/// </summary>
public class MetadataNamesTests
{
    static readonly TypeNameProvider Key = TypeNameProvider.Key;
    static readonly TypeNameProvider Display = TypeNameProvider.Display;

    [Theory]
    [InlineData(PrimitiveTypeCode.Boolean, "System.Boolean", "bool")]
    [InlineData(PrimitiveTypeCode.Byte, "System.Byte", "byte")]
    [InlineData(PrimitiveTypeCode.SByte, "System.SByte", "sbyte")]
    [InlineData(PrimitiveTypeCode.Char, "System.Char", "char")]
    [InlineData(PrimitiveTypeCode.Int16, "System.Int16", "short")]
    [InlineData(PrimitiveTypeCode.UInt16, "System.UInt16", "ushort")]
    [InlineData(PrimitiveTypeCode.Int32, "System.Int32", "int")]
    [InlineData(PrimitiveTypeCode.UInt32, "System.UInt32", "uint")]
    [InlineData(PrimitiveTypeCode.Int64, "System.Int64", "long")]
    [InlineData(PrimitiveTypeCode.UInt64, "System.UInt64", "ulong")]
    [InlineData(PrimitiveTypeCode.Single, "System.Single", "float")]
    [InlineData(PrimitiveTypeCode.Double, "System.Double", "double")]
    [InlineData(PrimitiveTypeCode.String, "System.String", "string")]
    [InlineData(PrimitiveTypeCode.Object, "System.Object", "object")]
    [InlineData(PrimitiveTypeCode.Void, "System.Void", "void")]
    [InlineData(PrimitiveTypeCode.IntPtr, "System.IntPtr", "nint")]
    [InlineData(PrimitiveTypeCode.UIntPtr, "System.UIntPtr", "nuint")]
    [InlineData(PrimitiveTypeCode.TypedReference, "System.TypedReference", "TypedReference")]
    public void Primitive_types(PrimitiveTypeCode code, string key, string display)
    {
        Assert.Equal(key, Key.GetPrimitiveType(code));
        Assert.Equal(display, Display.GetPrimitiveType(code));
    }

    [Fact]
    public void Arrays_pointers_and_refs()
    {
        Assert.Equal("X[]", Key.GetSZArrayType("X"));
        Assert.Equal("X[]", Key.GetArrayType("X", new ArrayShape(1, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)));
        Assert.Equal("X[,]", Key.GetArrayType("X", new ArrayShape(2, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)));
        Assert.Equal("X[,,]", Display.GetArrayType("X", new ArrayShape(3, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)));
        Assert.Equal("X[]", Key.GetArrayType("X", new ArrayShape(0, ImmutableArray<int>.Empty, ImmutableArray<int>.Empty)));
        Assert.Equal("X&", Key.GetByReferenceType("X"));
        Assert.Equal("ref X", Display.GetByReferenceType("X"));
        Assert.Equal("X*", Key.GetPointerType("X"));
        Assert.Equal("X*", Display.GetPointerType("X"));
        Assert.Equal("X", Key.GetPinnedType("X"));
        Assert.Equal("X", Key.GetModifiedType("System.Runtime.CompilerServices.IsVolatile", "X", true));
        Assert.Equal("X", Display.GetModifiedType("M", "X", false));
    }

    [Fact]
    public void Generic_instantiations()
    {
        var two = ImmutableArray.Create("A", "B");
        Assert.Equal("G`2<A,B>", Key.GetGenericInstantiation("G`2", two));
        Assert.Equal("G<A, B>", Display.GetGenericInstantiation("G", two));
        Assert.Equal("int?", Display.GetGenericInstantiation("Nullable", ImmutableArray.Create("int")));
        Assert.Equal("System.Nullable`1<System.Int32>", Key.GetGenericInstantiation("System.Nullable`1", ImmutableArray.Create("System.Int32")));
        // Only the single-argument BCL Nullable gets the "?" short form.
        Assert.Equal("Nullable<A, B>", Display.GetGenericInstantiation("Nullable", two));
        Assert.Equal("Other<int>", Display.GetGenericInstantiation("Other", ImmutableArray.Create("int")));
    }

    [Fact]
    public void Generic_parameters_are_numbered_in_keys_and_named_in_display()
    {
        var names = new GenericNames(new[] { "TKey", "TValue" }, new[] { "TResult" });
        Assert.Equal("!1", Key.GetGenericTypeParameter(names, 1));
        Assert.Equal("!!0", Key.GetGenericMethodParameter(names, 0));
        Assert.Equal("TValue", Display.GetGenericTypeParameter(names, 1));
        Assert.Equal("TResult", Display.GetGenericMethodParameter(names, 0));
        // Out of range or no context: fall back to the number.
        Assert.Equal("!2", Display.GetGenericTypeParameter(names, 2));
        Assert.Equal("!!1", Display.GetGenericMethodParameter(names, 1));
        Assert.Equal("!0", Display.GetGenericTypeParameter(null, 0));
        Assert.Equal("!!0", Display.GetGenericMethodParameter(null, 0));
    }

    [Fact]
    public void Function_pointers_list_parameters_then_return_type()
    {
        var sig = new MethodSignature<string>(default, "R", 2, 0, ImmutableArray.Create("A", "B"));
        Assert.Equal("delegate*<A,B,R>", Key.GetFunctionPointerType(sig));
    }

    [Fact]
    public void Custom_attribute_type_hooks()
    {
        Assert.Equal("System.Type", Key.GetSystemType());
        Assert.True(Key.IsSystemType("System.Type"));
        Assert.True(Key.IsSystemType("Type"));
        Assert.False(Key.IsSystemType("System.String"));
        Assert.Equal("Some.Name", Key.GetTypeFromSerializedName("Some.Name"));
        Assert.Equal(PrimitiveTypeCode.Int32, Key.GetUnderlyingEnumType("Any.Enum"));
    }

    [Theory]
    [InlineData("Newtonsoft.Json.Linq.JToken", "JToken")]
    [InlineData("A.Outer+Inner`1", "Outer.Inner")]
    [InlineData("System.Collections.Generic.List`1", "List")]
    [InlineData("Global", "Global")]
    [InlineData("A.B+C+D", "B.C.D")]
    [InlineData("Dict`2+Entry", "Dict.Entry")]
    [InlineData("A.Outer+Inner.Dotted", "Outer.Inner.Dotted")] // only the first part has a namespace
    public void Short_names(string full, string expected) =>
        Assert.Equal(expected, MetadataNames.ShortName(full));

    [Theory]
    [InlineData(true, 0, "M(A,B):R")]
    [InlineData(false, 0, "M(A,B):R static")]
    [InlineData(true, 2, "M``2(A,B):R")]
    [InlineData(false, 1, "M``1(A,B):R static")]
    public void Method_key_format(bool instance, int genericCount, string expected)
    {
        var header = new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.Default,
            instance ? SignatureAttributes.Instance : SignatureAttributes.None);
        var sig = new MethodSignature<string>(header, "R", 2, genericCount, ImmutableArray.Create("A", "B"));
        Assert.Equal(expected, MetadataNames.MethodKey("M", sig));
    }

    [Fact]
    public void Method_key_without_parameters_and_field_key()
    {
        var header = new SignatureHeader(SignatureKind.Method, SignatureCallingConvention.Default, SignatureAttributes.Instance);
        Assert.Equal("Run():System.Void", MetadataNames.MethodKey("Run", new MethodSignature<string>(header, "System.Void", 0, 0, ImmutableArray<string>.Empty)));
        Assert.Equal("Size|System.Int32", MetadataNames.FieldKey("Size", "System.Int32"));
    }

    [Fact]
    public void Bad_image_is_always_malformed() =>
        Assert.True(MetadataNames.IsMalformed(new BadImageFormatException("x")));

    [Fact]
    public void Other_exception_types_are_never_malformed()
    {
        Assert.False(MetadataNames.IsMalformed(new IOException("x")));
        Assert.False(MetadataNames.IsMalformed(new NullReferenceException()));
        Assert.False(MetadataNames.IsMalformed(Catch(() => throw new NotSupportedException())));
    }

    [Fact]
    public void Argument_exception_counts_only_when_system_reflection_metadata_threw_it()
    {
        // Thrown inside System.Reflection.Metadata: the file is at fault.
        Assert.True(MetadataNames.IsMalformed(Catch(() => MetadataTokens.EntityHandle(-1))));
        // Thrown by anyone else (here, the test itself): a bug, not bad input.
        Assert.False(MetadataNames.IsMalformed(Catch(() => throw new ArgumentException("ours"))));
        Assert.False(MetadataNames.IsMalformed(Catch(() => throw new InvalidOperationException("ours"))));
        // Never thrown, so no stack trace at all.
        Assert.False(MetadataNames.IsMalformed(new OverflowException()));
    }

    [Fact]
    public void Too_deep_message_names_what_and_the_limit()
    {
        var e = MetadataNames.TooDeep("nested types");
        Assert.Equal("Malformed metadata: nested types nest deeper than 64 levels (or form a cycle).", e.Message);
        Assert.Equal(64, MetadataNames.MaxDepth);
    }

    static Exception Catch(Action f)
    {
        try { f(); }
        catch (Exception e) { return e; }
        throw new Xunit.Sdk.XunitException("Expected an exception.");
    }
}

/// <summary>
/// The hand-written attribute reader used for [Obsolete] and [TargetFramework]
/// (CustomAttribute.DecodeValue trusts array lengths in the blob and can be made
/// to allocate gigabytes).
/// </summary>
public sealed class AttributeArgumentTests : IDisposable
{
    const string Src = """
        using System;
        namespace T
        {
            public class S : Attribute { public S(string a) { } }
            public class SB : Attribute { public SB(string a, bool b) { } }
            public class BS : Attribute { public BS(bool b, string a) { } }
            public class None : Attribute { public None() { } }
            public class I : Attribute { public I(int a) { } }
            public class SI : Attribute { public SI(string a, int b) { } }
            public class Named : Attribute { public Named(string a) { } public int N { get; set; } }
            public class Sixteen : Attribute { public Sixteen(string a, string b, string c, string d, string e, string f, string g, string h, string i, string j, string k, string l, string m, string n, string o, string p) { } }
            public class Seventeen : Attribute { public Seventeen(string a, string b, string c, string d, string e, string f, string g, string h, string i, string j, string k, string l, string m, string n, string o, string p, string q) { } }

            [S("hello")] public class UsesS { }
            [S(null)] public class UsesNull { }
            [SB("msg", true)] public class UsesSB { }
            [BS(false, "later")] public class UsesBS { }
            [None] public class UsesNone { }
            [I(7)] public class UsesI { }
            [SI("x", 7)] public class UsesSI { }
            [Named("x", N = 5)] public class UsesNamed { }
            [Sixteen("a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p")] public class UsesSixteen { }
            [Seventeen("a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q")] public class UsesSeventeen { }
            [Obsolete("external", true)] public class UsesObsolete { }
            [S("prologue")] public class BadProlog { }
            [S("truncated-string")] public class Truncated { }
        }
        """;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "packagebat-attr-tests", Guid.NewGuid().ToString("N"));
    readonly Dictionary<string, object?[]?> _args = new();

    public AttributeArgumentTests()
    {
        var dll = Compiler.Compile(_dir, "Attrs", "1.0.0.0", new[] { ("A.cs", Src) });
        var bytes = File.ReadAllBytes(dll);
        // Wreck the prolog of one blob (01 00 -> 02 00), and make another claim a longer string than it holds.
        Patch(bytes, "prologue", at => bytes[at - 3] = 0x02);
        Patch(bytes, "truncated-string", at => bytes[at - 1] = 0x7F);
        File.WriteAllBytes(dll, bytes);

        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(File.ReadAllBytes(dll)));
        var r = pe.GetMetadataReader();
        foreach (var th in r.TypeDefinitions)
        {
            var td = r.GetTypeDefinition(th);
            foreach (var ch in td.GetCustomAttributes())
            {
                var ca = r.GetCustomAttribute(ch);
                if (MetadataNames.AttributeTypeName(r, ca) is "System.Runtime.CompilerServices.NullableContextAttribute" or "System.Runtime.CompilerServices.NullableAttribute") continue;
                _args[r.GetString(td.Name)] = MetadataNames.ReadStringAndBoolArguments(r, ca);
            }
        }
    }

    static void Patch(byte[] bytes, string text, Action<int> patch)
    {
        var at = bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.True(at > 3, text);
        patch(at);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    object?[]? Args(string type) => _args.TryGetValue(type, out var a) ? a : throw new Xunit.Sdk.XunitException($"No attribute on {type}");

    [Fact]
    public void Reads_strings_and_bools_in_order()
    {
        Assert.Equal(new object?[] { "hello" }, Args("UsesS"));
        Assert.Equal(new object?[] { null }, Args("UsesNull"));
        Assert.Equal(new object?[] { "msg", true }, Args("UsesSB"));
        Assert.Equal(new object?[] { false, "later" }, Args("UsesBS"));
        Assert.Equal(new object?[] { "x" }, Args("UsesNamed")); // named arguments are ignored
    }

    [Fact]
    public void Works_for_attributes_defined_elsewhere() =>
        Assert.Equal(new object?[] { "external", true }, Args("UsesObsolete"));

    [Fact]
    public void Parameterless_constructor_gives_no_arguments() =>
        Assert.Equal(Array.Empty<object?>(), Args("UsesNone"));

    [Theory]
    [InlineData("UsesI")]
    [InlineData("UsesSI")]
    public void Any_other_parameter_type_gives_null(string type) =>
        Assert.Null(Args(type));

    [Fact]
    public void At_most_sixteen_arguments_are_read()
    {
        Assert.Equal("abcdefghijklmnop".Select(c => (object?)c.ToString()), Args("UsesSixteen"));
        Assert.Null(Args("UsesSeventeen"));
    }

    [Theory]
    [InlineData("BadProlog")]
    [InlineData("Truncated")]
    public void Malformed_blob_gives_null(string type) =>
        Assert.Null(Args(type));
}
