using System.Text.Json;
using Dtssh.Auth;
using Dtssh.Infra;
using Dtssh.Json;
using Dtssh.Keys;

namespace Dtssh.Discovery;

// Automatic pairing with no manual step: hosts tag their tunnel with the "dtssh"
// and stash compact pairing metadata (alias, port, user, host public key, and
// the ephemeral SSH seed) in the tunnel description. Clients logged into the same
// devtunnel account enumerate these tunnels and reconstruct everything needed to
// connect — no bundle to copy. Wire-compatible with the Go implementation.
internal static class Discover
{
    public const string Label = "dtssh";

    // Compact, description-embedded pairing metadata (400-char description cap).
    public sealed record Meta(
        string Alias, int Port, string User, string HostPub, string Seed, string CreatedBy)
    {
        public string Encode()
        {
            var j = new PairingMeta
            {
                V = 1,
                Alias = Alias,
                Port = Port,
                User = User,
                HostPub = HostPub,
                Seed = string.IsNullOrEmpty(Seed) ? null : Seed,
                CreatedBy = string.IsNullOrEmpty(CreatedBy) ? null : CreatedBy,
            };
            var s = JsonSerializer.Serialize(j, AppJsonContext.Default.PairingMeta);
            if (s.Length > 400)
                throw new DtsshException(
                    $"pairing metadata is {s.Length} bytes, exceeds the 400-char tunnel description limit");
            return s;
        }

        public static bool TryDecode(string? desc, out Meta meta)
        {
            meta = default!;
            desc = desc?.Trim() ?? "";
            if (!desc.StartsWith('{')) return false;
            PairingMeta? m;
            try { m = JsonSerializer.Deserialize(desc, AppJsonContext.Default.PairingMeta); }
            catch (JsonException) { return false; }
            if (m is null || m.V == 0 || string.IsNullOrEmpty(m.Alias)) return false;
            meta = new Meta(m.Alias!, m.Port, m.User ?? "", m.HostPub ?? "", m.Seed ?? "", m.CreatedBy ?? "");
            return true;
        }

        // Reconstructs the client SSH identity from the embedded seed.
        public Ed25519Identity Identity()
        {
            var seed = Ed25519Identity.DecodeSeedB64(Seed);
            return Ed25519Identity.FromSeed(seed, "dtssh-" + Alias);
        }
    }

    // A discovered, connectable dtssh tunnel.
    public sealed record Host(string TunnelId, Meta Meta, int HostConnections)
    {
        public bool Live => HostConnections > 0;

        // The unique token of the tunnel id (e.g. "clp92jc" from
        // "swift-book-clp92jc.usw2"), used to disambiguate shared aliases.
        public string ShortId()
        {
            var id = TunnelId;
            var dot = id.IndexOf('.');
            if (dot >= 0) id = id[..dot];
            var dash = id.LastIndexOf('-');
            return dash >= 0 ? id[(dash + 1)..] : id;
        }
    }

    // A discovered host with the final, collision-free alias to install.
    public sealed record Resolved(string Alias, Host Host, bool Ambiguous, bool Unconfirmed);

    // Enumerates dtssh-labelled tunnels and decodes their pairing metadata.
    public static async Task<List<Host>> ListAsync(CancellationToken ct = default)
    {
        var tuns = await DevtunnelCli.ListByLabelAsync(Label, ct).ConfigureAwait(false);
        var hosts = new List<Host>();
        foreach (var t in tuns)
        {
            if (!Meta.TryDecode(t.Description, out var m))
            {
                Log.Debugf("discover: skipping tunnel {0} (no dtssh metadata)", t.TunnelId);
                continue;
            }
            hosts.Add(new Host(DevtunnelCli.FullId(t), m, t.HostConnections));
        }
        Log.Debugf("discover: found {0} dtssh host(s)", hosts.Count);
        return hosts;
    }

    // Retrieves authoritative metadata for a single tunnel (used to reconcile a
    // pinned host key after the host rotates its key). ok=false when the tunnel
    // has no dtssh metadata or is not readable.
    public static async Task<(Meta meta, bool ok)> FetchAsync(string tunnelId, CancellationToken ct = default)
    {
        var t = await DevtunnelCli.ShowAsync(tunnelId, ct).ConfigureAwait(false);
        if (t is null) return (default!, false);
        return Meta.TryDecode(t.Description, out var m) ? (m, true) : (default!, false);
    }

    // Deduplicates discovered hosts by alias so one alias maps to one tunnel.
    // The service's hostConnections count lags a few seconds after a host
    // connects, so a live tunnel may still report zero. Resolve never hides a
    // candidate outright: confirmed-live tunnels drop their zero-count siblings;
    // if no candidate is confirmed live, all are wired up but flagged Unconfirmed.
    public static List<Resolved> Resolve(IReadOnlyList<Host> hosts)
    {
        var (byAlias, order) = GroupByAlias(hosts);
        var resolved = new List<Resolved>();
        foreach (var a in order)
        {
            var group = byAlias[a];
            var live = group.Where(h => h.Live).ToList();
            var candidates = live;
            var unconfirmed = false;
            if (live.Count == 0) { candidates = group; unconfirmed = true; }

            if (candidates.Count == 1)
            {
                resolved.Add(new Resolved(a, candidates[0], false, unconfirmed));
                continue;
            }
            foreach (var h in candidates)
                resolved.Add(new Resolved(a + "-" + h.ShortId(), h, true, unconfirmed));
        }
        return resolved;
    }

    // Returns tunnels that are definitively dead leftovers (offline duplicates of
    // a confirmed-live alias) and therefore safe to delete. Never returns a tunnel
    // from an alias group with no live member (the count may just be lagging).
    public static List<Host> Stale(IReadOnlyList<Host> hosts)
    {
        var (byAlias, order) = GroupByAlias(hosts);
        var stale = new List<Host>();
        foreach (var a in order)
        {
            var group = byAlias[a];
            if (!group.Any(h => h.Live)) continue;
            stale.AddRange(group.Where(h => !h.Live));
        }
        return stale;
    }

    private static (Dictionary<string, List<Host>>, List<string>) GroupByAlias(IReadOnlyList<Host> hosts)
    {
        var byAlias = new Dictionary<string, List<Host>>();
        var order = new List<string>();
        foreach (var h in hosts)
        {
            var a = h.Meta.Alias;
            if (!byAlias.TryGetValue(a, out var list))
            {
                list = new List<Host>();
                byAlias[a] = list;
                order.Add(a);
            }
            list.Add(h);
        }
        return (byAlias, order);
    }
}
