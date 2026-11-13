using System.Collections.Concurrent;
using System.Diagnostics;
using CsCheck;
using Xunit.Abstractions;

namespace PackageBat.Fuzz;

/// <summary>
/// Shared fuzzing harness.
///
/// Budget: by default each target runs a few hundred iterations, so the suite
/// is a quick smoke test in normal CI. Set FUZZ_SECONDS to fuzz each target for
/// that many seconds instead (the nightly workflow does this). FUZZ_SEED replays
/// one seed that CsCheck printed for a failure.
///
/// Crash targets don't stop at the first failure: they keep going for the whole
/// budget and collect one example per distinct crash (exception type + the
/// frame that threw it), writing each input to FUZZ_OUT (default: fuzz-findings/)
/// so it can be replayed and attached to a bug.
/// </summary>
internal static class Fuzz
{
    public static readonly int Seconds = int.TryParse(Environment.GetEnvironmentVariable("FUZZ_SECONDS"), out var s) && s > 0 ? s : -1;
    public static readonly string? Seed = Environment.GetEnvironmentVariable("FUZZ_SEED") is { Length: > 0 } seed ? seed : null;
    public static readonly long Iterations = Seconds > 0 ? -1 : 300;
    public static readonly string OutDir = Path.GetFullPath(Environment.GetEnvironmentVariable("FUZZ_OUT") is { Length: > 0 } o ? o : "fuzz-findings");

    /// <summary>An input that took longer than this is reported as a hang candidate.</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(10);

    /// <summary>Property test: CsCheck stops at the first failure and shrinks it to a minimal input.</summary>
    public static void Property<T>(Gen<T> gen, Action<T> assert, Func<T, string>? print = null) =>
        gen.Sample(assert, seed: Seed, iter: Iterations, time: Seconds, print: print);

    /// <summary>
    /// Crash fuzzing: runs <paramref name="target"/> on every generated input and
    /// fails at the end if any exception escaped that <paramref name="expected"/>
    /// does not allow (the way the real caller would handle it).
    /// </summary>
    public static void Crashes<T>(string name, ITestOutputHelper output, Gen<T> gen, Action<T> target,
        Func<Exception, bool> expected, Func<T, byte[]> save, string extension)
    {
        var found = new ConcurrentDictionary<string, (Exception Error, string File)>();
        long runs = 0, rejected = 0, slowest = 0;
        gen.Sample(input =>
        {
            Interlocked.Increment(ref runs);
            // A stack overflow kills the process before anything can be caught, so keep
            // the input that is running on each thread on disk until it has finished.
            var bytes = save(input);
            File.WriteAllBytes(InFlight(name), bytes);
            var sw = Stopwatch.StartNew();
            try
            {
                target(input);
            }
            catch (Exception e) when (expected(e) && !FromOurCode(e.InnerException))
            {
                Interlocked.Increment(ref rejected);
            }
            catch (Exception caught)
            {
                var e = caught is BadImageFormatException { InnerException: { } inner } && FromOurCode(inner) ? inner : caught;
                var sig = Signature(e);
                if (!found.ContainsKey(sig))
                {
                    var file = SaveInput(name, sig, bytes, extension);
                    found.TryAdd(sig, (e, file));
                }
            }
            var ms = sw.ElapsedMilliseconds;
            InterlockedMax(ref slowest, ms);
            if (sw.Elapsed > Slow)
            {
                var sig = $"Slow input ({ms} ms)";
                if (found.IsEmpty || !found.Keys.Any(k => k.StartsWith("Slow input")))
                    found.TryAdd(sig, (new TimeoutException(sig), SaveInput(name, "slow", bytes, extension)));
            }
        }, seed: Seed, iter: Iterations, time: Seconds);

        foreach (var f in Directory.EnumerateFiles(Path.Combine(OutDir, name), "in-flight-*"))
            File.Delete(f);
        if (!Directory.EnumerateFileSystemEntries(Path.Combine(OutDir, name)).Any())
            Directory.Delete(Path.Combine(OutDir, name));

        output.WriteLine($"{name}: {runs} inputs, {rejected} rejected cleanly, slowest {slowest} ms, {found.Count} distinct crash(es).");
        foreach (var (sig, (e, file)) in found)
            output.WriteLine($"\n== {sig}\n   input: {file}\n{e}");

        if (!found.IsEmpty)
            throw new Xunit.Sdk.XunitException(
                $"{name}: {found.Count} distinct crash(es), inputs saved under {OutDir}:\n" +
                string.Join("\n", found.Select(f => $"  {f.Key}\n    {f.Value.Error.GetType().Name}: {f.Value.Error.Message}\n    input: {f.Value.File}")));
    }

    static string InFlight(string target)
    {
        var dir = Path.Combine(OutDir, target);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"in-flight-{Environment.CurrentManagedThreadId}");
    }

    /// <summary>
    /// The code under test wraps exceptions from System.Reflection.Metadata on
    /// malformed files in BadImageFormatException. If what it wrapped was thrown
    /// by PackageBat itself, that's still a bug.
    /// </summary>
    static bool FromOurCode(Exception? inner) =>
        inner != null && new StackTrace(inner, false).GetFrames().FirstOrDefault()?.GetMethod()?.DeclaringType?.FullName?.StartsWith("PackageBat.", StringComparison.Ordinal) == true;

    /// <summary>Exception type plus the first frame in PackageBat code and the frame that threw, so one bug is one entry.</summary>
    static string Signature(Exception e)
    {
        var frames = new StackTrace(e, false).GetFrames();
        string Describe(StackFrame f) => f.GetMethod() is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : "?";
        var thrower = frames.Length > 0 ? Describe(frames[0]) : "?";
        var ours = frames.FirstOrDefault(f => f.GetMethod()?.DeclaringType?.FullName?.StartsWith("PackageBat.", StringComparison.Ordinal) == true
            && f.GetMethod()?.DeclaringType?.FullName?.StartsWith("PackageBat.Fuzz", StringComparison.Ordinal) != true);
        var where = ours != null && Describe(ours) != thrower ? $"{thrower} via {Describe(ours)}" : thrower;
        return $"{e.GetType().Name} at {where}";
    }

    static string SaveInput(string target, string signature, byte[] data, string extension)
    {
        var dir = Path.Combine(OutDir, target);
        Directory.CreateDirectory(dir);
        var safe = new string(signature.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_').ToArray());
        if (safe.Length > 120) safe = safe[..120];
        var path = Path.Combine(dir, safe + extension);
        File.WriteAllBytes(path, data);
        return path;
    }

    static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    /// <summary>A scratch directory per thread, for targets that only read files.</summary>
    public static string ScratchDir => _scratch.Value!;
    static readonly ThreadLocal<string> _scratch = new(() =>
    {
        var d = Path.Combine(Path.GetTempPath(), "packagebat-fuzz", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    });
}
