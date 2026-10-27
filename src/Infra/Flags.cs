namespace Dtssh.Infra;

// Minimal getopt-style parser mirroring Go's flag package semantics closely enough
// for dtssh: supports "--name value", "--name=value", "-name value", boolean flags
// (present => true, or "--name=false"), and collects leading/trailing positionals.
internal sealed class Flags
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> boolNames = new(StringComparer.Ordinal);
    private readonly List<string> positionals = new();

    public Flags Bool(string name) { boolNames.Add(name); return this; }

    public static Flags Parse(string[] args, params string[] boolFlags)
    {
        var f = new Flags();
        foreach (var b in boolFlags) f.boolNames.Add(b);
        f.ParseInto(args);
        return f;
    }

    public static Flags ParseStrict(string[] args, string[] valueFlags, params string[] boolFlags)
    {
        var f = new Flags();
        foreach (var b in boolFlags) f.boolNames.Add(b);
        f.ParseInto(args, new HashSet<string>(valueFlags, StringComparer.Ordinal));
        return f;
    }

    private static bool IsFlag(string a) =>
        a.Length >= 2 && a[0] == '-' && !char.IsDigit(a[1]);

    private void ParseInto(string[] args, HashSet<string>? valueNames = null)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (IsFlag(a))
            {
                var name = a.TrimStart('-');
                string? inlineVal = null;
                var eq = name.IndexOf('=');
                if (eq >= 0) { inlineVal = name[(eq + 1)..]; name = name[..eq]; }

                if (valueNames is not null)
                {
                    if (!boolNames.Contains(name) && !valueNames.Contains(name))
                    {
                        throw new ArgumentException($"unknown option \"{a}\"");
                    }
                    if (valueNames.Contains(name) &&
                        (inlineVal == "" || (inlineVal is null &&
                            (i + 1 == args.Length || args[i + 1] == "" || IsFlag(args[i + 1])))))
                    {
                        throw new ArgumentException($"--{name} requires a value");
                    }
                }

                if (boolNames.Contains(name))
                {
                    values[name] = inlineVal ?? "true";
                }
                else if (inlineVal is not null)
                {
                    values[name] = inlineVal;
                }
                else if (i + 1 < args.Length)
                {
                    values[name] = args[++i];
                }
                else
                {
                    values[name] = "";
                }
            }
            else
            {
                if (valueNames is not null)
                {
                    throw new ArgumentException($"unexpected argument \"{a}\"");
                }
                positionals.Add(a);
            }
        }
    }

    public IReadOnlyList<string> Positionals => positionals;
    public string? Arg(int i) => i < positionals.Count ? positionals[i] : null;

    public string Str(string name, string fallback = "") => values.TryGetValue(name, out var v) ? v : fallback;

    public int Int(string name, int fallback) =>
        values.TryGetValue(name, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public bool Bool(string name, bool fallback = false) =>
        values.TryGetValue(name, out var v)
            ? v is not ("false" or "0" or "no" or "off")
            : fallback;

    public bool Has(string name) => values.ContainsKey(name);
}
