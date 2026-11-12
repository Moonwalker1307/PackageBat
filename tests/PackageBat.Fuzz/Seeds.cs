using System.Reflection.PortableExecutable;
using CsCheck;
using PackageBat.Tests;

namespace PackageBat.Fuzz;

/// <summary>
/// Valid assemblies to mutate. Compiled once from C# that touches most of what
/// the extractor and scanner read: generics, nested types, properties, events,
/// [Obsolete], explicit interface implementations, attributes, enums, delegates,
/// default and params parameters, and IL that calls into the library.
/// </summary>
internal static class Seeds
{
    const string Library = """
        using System;
        using System.Collections.Generic;
        [assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(System.Uri))]
        namespace Fuzz.Lib
        {
            public class Client
            {
                public Client(string url) { }
                protected Client() { }
                public string Get(string path, int retries = 3, params string[] headers) => path;
                public T Read<T>(IDictionary<string, List<T>> map) where T : class => default!;
                public int Timeout { get; set; }
                [Obsolete("Use Timeout", true)] public int LegacyTimeout { get => 0; set { } }
                public event EventHandler<string>? Changed;
                [Obsolete] public event EventHandler? Old;
                public virtual void OnError(Exception e) { }
                public static readonly Client Default = new("x");
                public const int Max = 10;
                public ref int Ref(ref int x, out long y, in decimal z) { y = 0; return ref x; }
                public unsafe void Pointer(int* p, delegate*<int, void> f) { }
                public (int A, string B)[] Tuples(int[,] grid, Nullable<int> n) => null!;
                public class Nested<TInner> { public TInner? Value; public class Deeper { } }
            }
            public interface IHandler<in T> { void Handle(T message); int Count { get; } }
            public abstract class Base : IHandler<string>, IDisposable
            {
                public abstract void Run();
                void IHandler<string>.Handle(string m) { }
                public int Count => 0;
                public void Dispose() { }
            }
            public sealed class Sealed : Base { public override void Run() { } }
            [Obsolete("gone soon")] public struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
            public enum Mode : byte { A = 1, B = 2, C = 255 }
            [Flags] public enum Flags { None = 0, One = 1, Two = 2 }
            public delegate TResult Map<in T, out TResult>(T value);
            public static class Extensions { public static void Log(this Client c, string m, Mode mode = Mode.B) { } }
            [AttributeUsage(AttributeTargets.All)] public sealed class MarkAttribute : Attribute { public MarkAttribute(Type t, Mode m, string[] s) { } public int Named { get; set; } }
        }
        """;

    const string Consumer = """
        using System;
        using System.Collections.Generic;
        using Fuzz.Lib;
        namespace Fuzz.App
        {
            [Mark(typeof(Client), Mode.C, new[] { "a", "b" }, Named = 4)]
            public class MyClient : Client
            {
                public MyClient() : base("u") { }
                public override void OnError(Exception e) { base.OnError(e); }
            }
            public class Handler : Base, IHandler<int>
            {
                public Map<int, string>? Mapper;
                public override void Run()
                {
                    var c = new Client("x");
                    c.Get("p");
                    c.Get("p", 1, "h1", "h2");
                    c.Timeout = c.Timeout + Client.Max;
                    c.Changed += (s, e) => { };
                    c.Log("m");
                    var n = new Client.Nested<int>.Deeper();
                    var r = c.Read(new Dictionary<string, List<string>>());
                    object o = Mode.A;
                    if (o is Point p) Console.WriteLine(p.X);
                    var t = typeof(Sealed);
                    try { c.OnError(new InvalidOperationException()); } catch (ArgumentException) when (r != null) { }
                    switch (Environment.TickCount % 3) { case 0: c.Get("a"); break; case 1: c.Get("b"); break; default: break; }
                    int x = 1; c.Ref(ref x, out _, 2m);
                    Mapper = i => i.ToString();
                }
                void IHandler<int>.Handle(int m) { }
            }
        }
        """;

    public static readonly string Dir = Path.Combine(Path.GetTempPath(), "packagebat-fuzz-seeds", Guid.NewGuid().ToString("N"));

    static readonly Lazy<(byte[] Lib, byte[] App, byte[] AppPdb, string LibPath)> _built = new(() =>
    {
        var lib = Compiler.Compile(Path.Combine(Dir, "lib"), "Fuzz.Lib", "1.0.0.0", new[] { ("Lib.cs", Library) });
        var app = Compiler.Compile(Path.Combine(Dir, "app"), "Fuzz.App", "1.0.0.0", new[] { ("src/App.cs", Consumer) }, lib);
        var appPdb = Path.ChangeExtension(app, ".pdb");
        var result = (File.ReadAllBytes(lib), File.ReadAllBytes(app), File.ReadAllBytes(appPdb), lib);
        // Only mutated copies of the consumer may be found, never the original PDB.
        File.Delete(appPdb);
        File.Delete(app);
        return result;
    });

    public static byte[] LibraryDll => _built.Value.Lib;
    public static byte[] ConsumerDll => _built.Value.App;
    public static byte[] ConsumerPdb => _built.Value.AppPdb;
    public static string LibraryPath => _built.Value.LibPath;
}

/// <summary>Byte-level mutations of a seed file, biased towards the metadata where the interesting parsing happens.</summary>
internal static class Mutate
{
    public readonly record struct Op(int Kind, uint Position, uint Value);

    static readonly int[] Interesting = { 0, 1, -1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, int.MaxValue, int.MinValue, 0x10000, 0x1000000 };

    public static Gen<Op[]> Ops => Gen.Select(Gen.Int[0, 9], Gen.UInt, Gen.UInt, (k, p, v) => new Op(k, p, v)).Array[1, 12];

    public static byte[] Apply(byte[] seed, Op[] ops)
    {
        var (mdStart, mdLength) = MetadataRange(seed);
        var data = new List<byte>(seed);
        foreach (var op in ops)
        {
            if (data.Count == 0) break;
            // Most mutations land in the metadata block; the rest anywhere (headers, IL bodies, PDB).
            int pos = mdLength > 0 && op.Position % 4 != 0 && mdStart + mdLength <= data.Count
                ? mdStart + (int)(op.Position / 4 % (uint)mdLength)
                : (int)(op.Position % (uint)data.Count);
            switch (op.Kind)
            {
                case 0: // flip one bit
                case 1:
                    data[pos] ^= (byte)(1 << (int)(op.Value % 8));
                    break;
                case 2: // random byte
                    data[pos] = (byte)op.Value;
                    break;
                case 3: // interesting 1/2/4-byte value
                {
                    var v = Interesting[op.Value % (uint)Interesting.Length];
                    var width = 1 << (int)(op.Value / 16 % 3);
                    for (int i = 0; i < width && pos + i < data.Count; i++)
                        data[pos + i] = (byte)(v >> (8 * i));
                    break;
                }
                case 4: // small arithmetic on a byte (table row counts, indexes, lengths)
                    data[pos] = (byte)(data[pos] + (int)(op.Value % 33) - 16);
                    break;
                case 5: // truncate
                    if (op.Value % 4 == 0) data.RemoveRange(pos, data.Count - pos);
                    break;
                case 6: // delete a run
                    data.RemoveRange(pos, Math.Min((int)(op.Value % 64) + 1, data.Count - pos));
                    break;
                case 7: // insert random bytes
                {
                    var n = (int)(op.Value % 16) + 1;
                    data.InsertRange(pos, Enumerable.Range(0, n).Select(i => (byte)(op.Value >> (i % 4 * 8))));
                    break;
                }
                case 8: // copy a run from elsewhere in the file (keeps plausible structure)
                {
                    var src = (int)(op.Value % (uint)data.Count);
                    var n = Math.Min(Math.Min(16, data.Count - src), data.Count - pos);
                    for (int i = 0; i < n; i++) data[pos + i] = data[src + i];
                    break;
                }
                default: // zero a run
                    for (int i = 0; i < (int)(op.Value % 32) + 1 && pos + i < data.Count; i++) data[pos + i] = 0;
                    break;
            }
        }
        return data.ToArray();
    }

    /// <summary>File offset and length of the metadata (PE) or the whole file (PDB, which is all metadata).</summary>
    static (int Start, int Length) MetadataRange(byte[] seed)
    {
        if (seed.Length > 4 && seed[0] == 'B' && seed[1] == 'S' && seed[2] == 'J' && seed[3] == 'B')
            return (0, seed.Length);
        lock (_ranges)
        {
            if (_ranges.TryGetValue(seed, out var r)) return r;
            using var pe = new PEReader(new MemoryStream(seed));
            r = (pe.PEHeaders.MetadataStartOffset, pe.PEHeaders.MetadataSize);
            _ranges[seed] = r;
            return r;
        }
    }
    static readonly Dictionary<byte[], (int, int)> _ranges = new(ReferenceEqualityComparer.Instance);
}
