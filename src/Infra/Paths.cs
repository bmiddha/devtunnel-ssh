using System.Runtime.InteropServices;

namespace Dtssh.Infra;

// Cross-platform locations for dtssh files. Follows the XDG Base Directory Spec on
// Linux/macOS; uses %LOCALAPPDATA% on Windows. Under DTSSH_HOME or on Windows every
// category lives under a single flat root. Mirrors the Go internal/paths package so
// existing on-disk state (keys, tunnels, ssh_config pins) is reused unchanged.
internal static class Paths
{
    private static (string root, bool ok) SingleRoot()
    {
        var env = Environment.GetEnvironmentVariable("DTSSH_HOME");
        if (!string.IsNullOrEmpty(env)) return (env, true);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var la = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(la)) return (Path.Combine(la, "dtssh"), true);
            return (Path.Combine(Home(), "AppData", "Local", "dtssh"), true);
        }
        return ("", false);
    }

    private static string XdgRoot(string xdgVar, string fallbackRel)
    {
        var (root, ok) = SingleRoot();
        if (ok) return root;
        var xdg = Environment.GetEnvironmentVariable(xdgVar);
        if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "dtssh");
        return Path.Combine(Home(), fallbackRel, "dtssh");
    }

    private static string Home()
    {
        var h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(h) ? "." : h;
    }

    public static string DataRoot() => XdgRoot("XDG_DATA_HOME", Path.Combine(".local", "share"));
    public static string StateRoot() => XdgRoot("XDG_STATE_HOME", Path.Combine(".local", "state"));
    public static string CacheRoot() => XdgRoot("XDG_CACHE_HOME", ".cache");
    public static string Base() => DataRoot();

    public static string HostDir() => Path.Combine(DataRoot(), "host");
    public static string ClientDir() => Path.Combine(DataRoot(), "client");
    public static string ForwardsDir() => Path.Combine(StateRoot(), "forwards");
    public static string LogsDir() => Path.Combine(StateRoot(), "logs");
    public static string BinDir() => Path.Combine(CacheRoot(), "bin");

    public static string SshConfigPath() => Path.Combine(Home(), ".ssh", "config");

    // Creates dir (and parents) with owner-only permissions where the OS supports it.
    public static void EnsureDir(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void EnsureAll()
    {
        MigrateLegacy();
        foreach (var d in new[] { HostDir(), ClientDir(), ForwardsDir(), LogsDir() })
            EnsureDir(d);
    }

    // Relocates an existing ~/.dtssh dotfolder into the XDG roots (best-effort, XDG mode only).
    private static void MigrateLegacy()
    {
        if (SingleRoot().ok) return;
        var legacy = Path.Combine(Home(), ".dtssh");
        if (!Directory.Exists(legacy)) return;

        foreach (var (sub, dest) in new[]
        {
            ("host", HostDir()), ("client", ClientDir()),
            ("forwards", ForwardsDir()), ("logs", LogsDir()), ("bin", BinDir()),
        })
        {
            var src = Path.Combine(legacy, sub);
            if (!Directory.Exists(src) || Directory.Exists(dest)) continue;
            try
            {
                var parent = Path.GetDirectoryName(dest);
                if (parent is not null) EnsureDir(parent);
                Directory.Move(src, dest);
            }
            catch { /* best-effort */ }
        }
        try
        {
            if (Directory.Exists(legacy) && !Directory.EnumerateFileSystemEntries(legacy).Any())
                Directory.Delete(legacy);
        }
        catch { /* best-effort */ }
    }
}
