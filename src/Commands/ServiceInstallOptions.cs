using Dtssh.Infra;

namespace Dtssh.Commands;

// Separated out for testing
internal sealed record ServiceInstallOptions(IReadOnlyList<string> HostArgs, bool NoWslBoot)
{
    public static ServiceInstallOptions Parse(string[] args)
    {
        var f = Flags.ParseStrict(args,
            ["port", "user", "alias", "tunnel", "expiration"],
            "persist", "system-sshd", "no-wsl-boot");

        if (f.Has("port") && !int.TryParse(f.Str("port"), out _))
        {
            throw new ArgumentException("--port requires an integer");
        }
        if (!f.Bool("persist", true))
        {
            throw new ArgumentException($"services always persist; omit --persist={f.Str("persist")}");
        }

        // Reconstruct the `dtssh host` args. --persist reuses one tunnel + a stable
        // identity across restarts.
        var hostArgs = new List<string> { "--persist", "--port", f.Int("port", 2222).ToString() };
        foreach (var name in new[] { "user", "alias", "tunnel", "expiration" })
        {
            var value = f.Str(name);
            if (!string.IsNullOrEmpty(value)) { hostArgs.Add("--" + name); hostArgs.Add(value); }
        }
        if (f.Bool("system-sshd", false)) { hostArgs.Add("--system-sshd"); }
        return new ServiceInstallOptions(hostArgs, f.Bool("no-wsl-boot", false));
    }
}
