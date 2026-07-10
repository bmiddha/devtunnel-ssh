using System.Text.Json;
using Dtssh.Infra;
using Dtssh.Json;
using Dtssh.Ssh;

namespace Dtssh.Discovery;

// Auto-heals managed known_hosts pins from the authoritative devtunnel account
// metadata, so a host that rotated its SSH host key (reinstall, cleared state, or
// a switch to/from --system-sshd) still connects without a scary host-key
// mismatch. The account metadata is served over TLS to the owning account and is
// not reachable by a relay MITM, so refreshing from it is no weaker than the
// original discover-time pin. Ported from the Go internal/forward/hostkey.go.
internal static class HostKey
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    private static TimeSpan EffectiveTtl()
    {
        var v = Environment.GetEnvironmentVariable("DTSSH_HOSTKEY_TTL");
        if (!string.IsNullOrEmpty(v) && TryParseDuration(v, out var d)) return d;
        return DefaultTtl;
    }

    private static string StampPath(string tunnelId)
    {
        var safe = tunnelId.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(Paths.StateRoot(), "hostkey-" + safe + ".json");
    }

    private static bool RecentlyChecked(string tunnelId)
    {
        try
        {
            var st = JsonSerializer.Deserialize(File.ReadAllText(StampPath(tunnelId)),
                AppJsonContext.Default.HostKeyStamp);
            return st is not null && DateTime.UtcNow - st.CheckedAt < EffectiveTtl();
        }
        catch { return false; }
    }

    private static void StampChecked(string tunnelId)
    {
        try
        {
            Paths.EnsureDir(Paths.StateRoot());
            var json = JsonSerializer.Serialize(
                new HostKeyStamp { CheckedAt = DateTime.UtcNow }, AppJsonContext.Default.HostKeyStamp);
            File.WriteAllText(StampPath(tunnelId), json);
        }
        catch { /* best effort */ }
    }

    // Best-effort: refreshes pins for tunnelId; never blocks or fails the SSH
    // connection. cold forces a check regardless of the TTL throttle.
    public static async Task ReconcileAsync(string tunnelId, bool cold, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tunnelId)) return;
        try
        {
            if (!cold && RecentlyChecked(tunnelId)) return;

            var aliases = SshConfigFile.List()
                .Where(e => e.TunnelId == tunnelId && e.Pinned)
                .Select(e => e.Alias)
                .ToList();
            if (aliases.Count == 0) return;

            var (meta, ok) = await Discover.FetchAsync(tunnelId, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrEmpty(meta.HostPub))
            {
                Log.Debugf("hostkey: no authoritative metadata for \"{0}\"; keeping existing pin", tunnelId);
                return;
            }

            foreach (var a in aliases)
            {
                if (SshConfigFile.UpdatePin(a, meta.HostPub))
                {
                    Console.Error.WriteLine(
                        $"dtssh: host key for \"{a}\" changed; refreshed the pin from your devtunnel account.");
                    Log.Debugf("hostkey: refreshed pin for alias \"{0}\" (tunnel \"{1}\")", a, tunnelId);
                }
            }
            StampChecked(tunnelId);
        }
        catch (Exception e) { Log.Debugf("hostkey: reconcile failed: {0}", e.Message); }
    }

    private static bool TryParseDuration(string s, out TimeSpan d)
    {
        d = default;
        s = s.Trim();
        if (s.Length < 2) return false;
        var unit = s[^1];
        if (!double.TryParse(s[..^1], out var n)) return false;
        d = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        return d != TimeSpan.Zero || n == 0;
    }
}

// Persisted throttle stamp for the warm-path host-key re-verification.
internal sealed class HostKeyStamp
{
    public DateTime CheckedAt { get; set; }
}
