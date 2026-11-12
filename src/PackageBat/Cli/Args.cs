namespace PackageBat.Cli;

/// <summary>Tiny "--name value" parser; repeated options collect into a list.</summary>
internal sealed class Args
{
    readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    public string? Command { get; }

    public Args(string[] argv, IReadOnlySet<string> flagNames)
    {
        int i = 0;
        if (argv.Length > 0 && !argv[0].StartsWith("-")) Command = argv[i++];
        for (; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--")) throw new Ecosystems.UsageException($"Unexpected argument '{a}'.");
            var name = a[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
            if (flagNames.Contains(name) && value == null) { _flags.Add(name); continue; }
            if (value == null)
            {
                if (i + 1 >= argv.Length) throw new Ecosystems.UsageException($"--{name} needs a value.");
                value = argv[++i];
            }
            if (!_values.TryGetValue(name, out var list)) _values[name] = list = new();
            list.Add(value);
        }
    }

    public string? Get(string name) { _used.Add(name); return _values.TryGetValue(name, out var l) ? l[^1] : null; }
    public string Require(string name) => Get(name) ?? throw new Ecosystems.UsageException($"--{name} is required.");
    public IReadOnlyList<string> All(string name) { _used.Add(name); return _values.TryGetValue(name, out var l) ? l : Array.Empty<string>(); }
    public bool Flag(string name) { _used.Add(name); return _flags.Contains(name); }

    public void EnsureAllUsed()
    {
        var unknown = _values.Keys.Concat(_flags).Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0) throw new Ecosystems.UsageException($"Unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
    }
}
