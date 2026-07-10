using System.Text;
using System.Text.RegularExpressions;
using Dtssh.Infra;

namespace Dtssh.Ssh;

// Installs and removes the per-alias OpenSSH client configuration and
// known_hosts pinning that make `ssh <alias>` transparently hop through a dev
// tunnel with no prompts. Managed-block markers and known_hosts pin format match
// the Go tool so existing configs stay valid. Ported from Go internal/sshconfig.
internal static partial class SshConfigFile
{
    public sealed record Entry
    {
        public required string Alias { get; init; }
        public string User { get; init; } = "";
        public int Port { get; init; }
        public required string TunnelId { get; init; }
        public string IdentityFile { get; init; } = "";
        public string HostPublicKey { get; init; } = "";
        public string DtsshBin { get; init; } = "";

        public string Block()
        {
            var proxy = $"{DtsshBin} proxy {TunnelId} --port {Port}";

            var b = new StringBuilder();
            b.Append(MarkerStart(Alias)).Append('\n');
            b.Append($"Host {Alias}\n");
            b.Append($"    User {User}\n");
            b.Append($"    IdentityFile {IdentityFile}\n");
            b.Append("    IdentitiesOnly yes\n");
            b.Append($"    ProxyCommand {proxy}\n");
            if (!string.IsNullOrEmpty(HostPublicKey))
            {
                b.Append($"    UserKnownHostsFile {KnownHostsPath()}\n");
                b.Append("    StrictHostKeyChecking yes\n");
            }
            else
            {
                b.Append("    StrictHostKeyChecking accept-new\n");
            }
            b.Append("    ServerAliveInterval 30\n");
            b.Append(MarkerEnd(Alias)).Append('\n');
            return b.ToString();
        }

        // Writes the known_hosts pin and inserts/updates the ssh_config block.
        public string Install()
        {
            if (!string.IsNullOrEmpty(HostPublicKey)) PinHostKey(Alias, HostPublicKey);
            var cfgPath = Paths.SshConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
            var existing = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
            var updated = UpsertBlock(existing, Alias, Block());
            File.WriteAllText(cfgPath, updated);
            return cfgPath;
        }
    }

    public sealed record ManagedEntry
    {
        public string Alias = "";
        public string User = "";
        public string TunnelId = "";
        public int Port;
        public string IdentityFile = "";
        public bool Pinned;
    }

    private static string MarkerStart(string alias) => "# >>> dtssh " + alias + " >>>";
    private static string MarkerEnd(string alias) => "# <<< dtssh " + alias + " <<<";

    public static string KnownHostsPath() => Path.Combine(Paths.ClientDir(), "known_hosts");

    [GeneratedRegex(@"^#\s*>>>\s*dtssh\s+(\S+)\s+>>>\s*$")]
    private static partial Regex BlockStartRe();
    [GeneratedRegex(@"\bproxy\s+(\S+)")]
    private static partial Regex ProxyTunRe();
    [GeneratedRegex(@"--port\s+(\d+)")]
    private static partial Regex ProxyPortRe();

    // Parses the dtssh-managed blocks from the ssh_config in file order.
    public static List<ManagedEntry> List()
    {
        var path = Paths.SshConfigPath();
        if (!File.Exists(path)) return new List<ManagedEntry>();

        var entries = new List<ManagedEntry>();
        ManagedEntry? cur = null;
        foreach (var raw in File.ReadAllText(path).Split('\n'))
        {
            var line = raw.Trim();
            var m = BlockStartRe().Match(line);
            if (m.Success)
            {
                cur = new ManagedEntry { Alias = m.Groups[1].Value };
                entries.Add(cur);
                continue;
            }
            if (cur is null) continue;
            if (line == MarkerEnd(cur.Alias)) { cur = null; continue; }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            switch (fields[0])
            {
                case "User": cur.User = fields[1]; break;
                case "IdentityFile": cur.IdentityFile = fields[1]; break;
                case "StrictHostKeyChecking": cur.Pinned = fields[1] == "yes"; break;
                case "ProxyCommand":
                    var tm = ProxyTunRe().Match(line);
                    if (tm.Success) cur.TunnelId = tm.Groups[1].Value;
                    var pm = ProxyPortRe().Match(line);
                    if (pm.Success) cur.Port = int.Parse(pm.Groups[1].Value);
                    break;
            }
        }
        return entries;
    }

    // Deletes the ssh_config block and known_hosts pin for an alias.
    public static void Remove(string alias)
    {
        var cfgPath = Paths.SshConfigPath();
        if (File.Exists(cfgPath))
            File.WriteAllText(cfgPath, UpsertBlock(File.ReadAllText(cfgPath), alias, ""));

        var kh = KnownHostsPath();
        if (File.Exists(kh))
        {
            var kept = File.ReadAllText(kh).Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith(alias + " ", StringComparison.Ordinal))
                .ToList();
            File.WriteAllText(kh, string.Join('\n', kept) + "\n");
        }
    }

    private static void PinHostKey(string alias, string pub)
    {
        Paths.EnsureDir(Paths.ClientDir());
        var line = alias + " " + pub.Trim();
        var kh = KnownHostsPath();
        var kept = File.Exists(kh)
            ? File.ReadAllText(kh).Split('\n')
                .Where(l => l.Length > 0 && !l.StartsWith(alias + " ", StringComparison.Ordinal))
                .ToList()
            : new List<string>();
        kept.Add(line);
        File.WriteAllText(kh, string.Join('\n', kept) + "\n");
    }

    // The currently pinned host public key line for alias, if any.
    public static (string key, bool ok) PinnedKey(string alias)
    {
        var kh = KnownHostsPath();
        if (!File.Exists(kh)) return ("", false);
        foreach (var l in File.ReadAllText(kh).Split('\n'))
            if (l.StartsWith(alias + " ", StringComparison.Ordinal))
                return (l[(alias.Length + 1)..].Trim(), true);
        return ("", false);
    }

    // Refreshes the pinned host key for alias to pub, returning whether it changed.
    public static bool UpdatePin(string alias, string pub)
    {
        pub = pub.Trim();
        var (cur, ok) = PinnedKey(alias);
        if (ok && SameHostKey(cur, pub)) return false;
        PinHostKey(alias, pub);
        return true;
    }

    // Reports whether two known_hosts key lines denote the same key, comparing
    // only the key type and base64 material (ignoring any trailing comment).
    public static bool SameHostKey(string a, string b)
    {
        var fa = a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var fb = b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fa.Length < 2 || fb.Length < 2) return a.Trim() == b.Trim();
        return fa[0] == fb[0] && fa[1] == fb[1];
    }

    // Replaces the marked block for alias with newBlock (empty removes it),
    // preserving everything else. newBlock should end with a newline.
    private static string UpsertBlock(string content, string alias, string newBlock)
    {
        var start = MarkerStart(alias);
        var end = MarkerEnd(alias);
        var outLines = new List<string>();
        var inBlock = false;
        var replaced = false;
        foreach (var line in content.Split('\n'))
        {
            var t = line.Trim();
            if (t == start)
            {
                inBlock = true;
                if (newBlock.Length > 0 && !replaced)
                {
                    outLines.Add(newBlock.TrimEnd('\n'));
                    replaced = true;
                }
            }
            else if (t == end) { inBlock = false; }
            else if (!inBlock) { outLines.Add(line); }
        }
        var result = string.Join('\n', outLines);
        if (!replaced && newBlock.Length > 0)
        {
            if (result.Length > 0 && !result.EndsWith('\n')) result += "\n";
            result += newBlock;
        }
        result = result.TrimEnd('\n');
        if (result.Length > 0) result += "\n";
        return result;
    }
}
