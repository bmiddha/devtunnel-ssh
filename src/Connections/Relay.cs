using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using System.Diagnostics;
using System.Net.Http.Headers;
using Dtssh.Auth;
using Dtssh.Infra;

namespace Dtssh.Connections;

internal static class Relay
{
    // Splits a devtunnel id "name.cluster" into its parts. dtssh uses full ids
    // (with the cluster suffix) everywhere, matching the devtunnel CLI.
    public static (string name, string cluster) SplitId(string tunnelId)
    {
        var dot = tunnelId.IndexOf('.');
        return dot < 0 ? (tunnelId, "") : (tunnelId[..dot], tunnelId[(dot + 1)..]);
    }

    public static TraceSource Trace()
    {
        var ts = new TraceSource("dtssh", SourceLevels.Warning);
        if (Log.Enabled)
        {
            ts.Switch.Level = SourceLevels.Information;
            ts.Listeners.Add(new DebugTraceListener());
        }
        return ts;
    }

    // A management client that authenticates per-request with a tunnel access token
    // (passed via TunnelRequestOptions.AccessToken); no interactive user token needed.
    public static TunnelManagementClient ManagementClient()
    {
        var ua = new ProductInfoHeaderValue("dtssh", Program.Version);
        return new TunnelManagementClient(
            ua,
            userTokenCallback: () => Task.FromResult<AuthenticationHeaderValue?>(null),
            ManagementApiVersions.Version20230927Preview);
    }

    // Fetches the full tunnel (endpoints + ports) authorized by a tunnel access token.
    public static async Task<Tunnel> FetchTunnelAsync(
        TunnelManagementClient mgmt, string tunnelId, string accessToken, string scope, CancellationToken ct)
    {
        var (name, cluster) = SplitId(tunnelId);
        var stub = new Tunnel { TunnelId = name, ClusterId = cluster };
        var options = new TunnelRequestOptions
        {
            AccessToken = accessToken,
            TokenScopes = new[] { scope },
            IncludePorts = true,
        };
        var tunnel = await mgmt.GetTunnelAsync(stub, options, ct).ConfigureAwait(false)
            ?? throw new DtsshException($"tunnel {tunnelId} not found or access denied");

        // Ensure the access token travels with the tunnel for the relay connection.
        tunnel.AccessTokens ??= new Dictionary<string, string>();
        tunnel.AccessTokens[scope] = accessToken;
        return tunnel;
    }

    // Connects an in-process relay host for a tunnel, authorized solely by a
    // host-scoped tunnel access token (no interactive user token needed — verified
    // under NativeAOT). The tunnel's ports are forwarded to same-numbered loopback
    // ports where the local sshd listens. The returned host auto-reconnects and
    // (via the token-refresh subscription) re-mints a fresh host token from the
    // devtunnel CLI when the current one nears expiry, so long sessions survive
    // token rotation without any user interaction — the headline SDK win.
    public static async Task<TunnelRelayTunnelHost> HostAsync(
        TunnelManagementClient mgmt, Tunnel tunnel, string tunnelId, TraceSource trace, CancellationToken ct)
    {
        var host = new TunnelRelayTunnelHost(mgmt, trace);
        host.RefreshingTunnelAccessToken += (_, e) => OnRefreshToken(e, tunnelId);
        var options = new TunnelConnectionOptions { EnableReconnect = true };
        await host.ConnectAsync(tunnel, options, ct).ConfigureAwait(false);
        return host;
    }

    // Shared token-refresh handler: asynchronously re-issues a scope-appropriate
    // tunnel access token via the devtunnel CLI (which holds the user's
    // refreshable credentials). Best-effort: returns null on failure so the SDK
    // keeps the existing token and retries later.
    public static void OnRefreshToken(RefreshingTunnelAccessTokenEventArgs e, string tunnelId)
    {
        var scope = e.TunnelAccessScope;
        e.TunnelAccessTokenTask = RefreshAsync();
        async Task<string?> RefreshAsync()
        {
            try
            {
                Log.Debugf("relay: refreshing {0} token for tunnel {1}", scope, tunnelId);
                return await DevtunnelCli.IssueTokenAsync(tunnelId, scope, e.Cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debugf("relay: token refresh failed: {0}", ex.Message);
                return null;
            }
        }
    }
}

internal sealed class DebugTraceListener : TraceListener
{
    public override void Write(string? message) { if (message is not null) Log.Debug(message.TrimEnd()); }
    public override void WriteLine(string? message) { if (message is not null) Log.Debug(message.TrimEnd()); }
}
