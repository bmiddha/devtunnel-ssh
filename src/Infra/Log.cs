using System.Globalization;

namespace Dtssh.Infra;

// Opt-in diagnostic logging, enabled by DTSSH_DEBUG (non-empty, non "0"/"false"/"no"/"off").
// Written to stderr so it never disturbs the stdout stream the SSH ProxyCommand relies on.
internal static class Log
{
    private static readonly Lazy<bool> enabled = new(() =>
    {
        var v = (Environment.GetEnvironmentVariable("DTSSH_DEBUG") ?? "").Trim().ToLowerInvariant();
        return v is not ("" or "0" or "false" or "no" or "off");
    });

    private static readonly object gate = new();

    public static bool Enabled => enabled.Value;

    public static void Debug(string message)
    {
        if (!Enabled) return;
        lock (gate)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            Console.Error.WriteLine($"[dtssh debug {ts}] {message}");
        }
    }

    public static void Debugf(string format, params object?[] args) => Debug(string.Format(format, args));

    // User-facing status line on stderr (always shown), matching the Go tool's `dtssh: ...`.
    public static void Info(string message) => Console.Error.WriteLine($"dtssh: {message}");
}
