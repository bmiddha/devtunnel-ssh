using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dtssh.Infra;

// Small shared helpers used across commands (mirrors the Go internal/commands
// helper functions: self path, alias sanitisation, loopback readiness).
internal static class Cli
{
    // Absolute path to the running dtssh executable, embedded in generated
    // ssh_config ProxyCommand lines.
    public static string SelfPath() =>
        Environment.ProcessPath ?? "dtssh";

    // Lowercases and keeps only alias-safe characters (letters, digits, dashes),
    // collapsing others to dashes — matches the Go sanitizeAlias.
    public static string SanitizeAlias(string s)
    {
        s = s.ToLowerInvariant();
        var b = new StringBuilder();
        foreach (var r in s)
        {
            if ((r >= 'a' && r <= 'z') || (r >= '0' && r <= '9') || r == '-') b.Append(r);
            else if (r is '.' or '_' or ' ') b.Append('-');
        }
        var outp = b.ToString().Trim('-');
        return outp.Length == 0 ? "host" : outp;
    }

    public static string CurrentUser() =>
        Environment.UserName is { Length: > 0 } u ? u : "user";

    public static string Hostname()
    {
        try { return Dns.GetHostName(); } catch { return "host"; }
    }

    // Waits until a loopback TCP port accepts connections, or the timeout elapses.
    public static async Task<bool> WaitPortAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                return true;
            }
            catch { await Task.Delay(150, ct).ConfigureAwait(false); }
        }
        return false;
    }
}
