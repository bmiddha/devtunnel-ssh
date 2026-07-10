using Dtssh.Auth;

namespace Dtssh.Commands;

// `dtssh login [devtunnel-login-flags]` — ensures the devtunnel CLI is present and
// delegates to `devtunnel user login`, passing flags straight through.
internal static class LoginCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        foreach (var a in args)
            if (a is "-h" or "--help" or "help") { PrintUsage(); return 0; }

        return await DevtunnelCli.LoginAsync(args).ConfigureAwait(false);
    }

    private static void PrintUsage() => Console.Error.Write(
"""
dtssh login — log into Microsoft Dev Tunnels

USAGE:
    dtssh login [devtunnel-login-flags]

Ensures the devtunnel CLI is installed and then runs `devtunnel user login`.
Any flags are passed straight through, for example:

    dtssh login              # browser auth (default)
    dtssh login -d           # device-code auth (headless hosts)
    dtssh login -g           # GitHub account
    dtssh login -e           # Entra ID / Microsoft account

You must be logged in before `dtssh host`, `dtssh service` or `dtssh discover`
can reach your tunnels.

""");
}
