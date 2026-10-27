using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Dtssh.Commands;
using Dtssh.Infra;
using Dtssh.Service;

internal static class Program
{
    private const string TunnelId = "amusing-shoe-xwn7v2l.usw2";
    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string Description =
        "dtssh " + (char)0x2014 + " seamless SSH over Microsoft Dev Tunnels host";

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--task-noop", ..]) return 0;
        var root = Path.Combine(Path.GetTempPath(), "dtssh-service-tests-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("DTSSH_HOME");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("DTSSH_HOME", root);
            if (args is ["--scheduler-smoke"])
            {
                await SchedulerSmokeAsync(root);
                return 0;
            }
            if (args is ["--cli-smoke", var exe])
            {
                await CliSmokeAsync(root, exe);
                return 0;
            }
            if (args.Length != 0) throw new ArgumentException("Expected no arguments, --scheduler-smoke, or --cli-smoke <dtssh>");
            return RunTests(root);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL harness: {e}");
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("DTSSH_HOME", previousHome);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static int RunTests(string root)
    {
        var tests = new List<(string Name, Action Run)>
        {
            ("reported-order", () => Tunnel(["--port", "2222", "--persist", "--tunnel", TunnelId])),
            ("without-persist", () => Tunnel(["--port", "2222", "--tunnel", TunnelId])),
            ("persist-last", () => Tunnel(["--tunnel", TunnelId, "--persist"])),
            ("inline-tunnel", () => Tunnel(["--persist", "--tunnel=" + TunnelId])),
            ("single-dash", () => Tunnel(["-persist", "-tunnel", TunnelId])),
            ("duplicate-tunnel", () => Tunnel(["--tunnel", "old", "--tunnel", TunnelId])),
            ("duplicate-persist", () => Tunnel(["--persist", "--persist", "--tunnel", TunnelId])),
            ("persist-true-last", () => Tunnel(["--persist=false", "--persist=true", "--tunnel", TunnelId])),
            ("persist-false-last", () => Reject(["--persist", "--persist=false"], "services always persist; omit --persist=false")),
            ("defaults", () => Equal(["--persist", "--port", "2222"], ServiceInstallOptions.Parse([]).HostArgs)),
            ("forward-options", () =>
            {
                var options = ServiceInstallOptions.Parse([
                    "--port=2200", "--user", "A&B", "--alias", "my host",
                    "--tunnel", TunnelId, "--expiration", "24h", "--system-sshd", "--no-wsl-boot"]);
                Equal(["--persist", "--port", "2200", "--user", "A&B", "--alias", "my host",
                    "--tunnel", TunnelId, "--expiration", "24h", "--system-sshd"], options.HostArgs);
                Ensure(options.NoWslBoot, "no-wsl-boot was not retained");
            }),
            ("install-only-default", () => Ensure(!ServiceInstallOptions.Parse([]).NoWslBoot, "WSL boot default changed")),
            ("duplicate-port", () => Equal(["--persist", "--port", "2201"],
                ServiceInstallOptions.Parse(["--port", "2200", "--port", "2201"]).HostArgs)),
            ("negative-integer", () => Equal(["--persist", "--port", "-1"],
                ServiceInstallOptions.Parse(["--port", "-1"]).HostArgs)),
            ("unknown-before-tunnel", () => Reject(["--typo", "--tunnel", TunnelId], "unknown option \"--typo\"")),
            ("unknown-inline", () => Reject(["--typo=value"], "unknown option \"--typo=value\"")),
            ("unknown-single-dash", () => Reject(["-typo"], "unknown option \"-typo\"")),
            ("stray-positional", () => Reject([TunnelId], $"unexpected argument \"{TunnelId}\"")),
            ("extra-positional", () => Reject(["--tunnel", TunnelId, "extra"], "unexpected argument \"extra\"")),
            ("bare-dash", () => Reject(["-"], "unexpected argument \"-\"")),
            ("boolean-separated-value", () => Reject(["--persist", "false"], "unexpected argument \"false\"")),
            ("noninteger-port", () => Reject(["--port", "abc"], "--port requires an integer")),
            ("overflow-port", () => Reject(["--port", "2147483648"], "--port requires an integer")),
            ("legacy-unknown-consumes-flag", () =>
            {
                var f = Flags.Parse(["--persist", "--tunnel", TunnelId]);
                Ensure(f.Str("persist") == "--tunnel" && f.Arg(0) == TunnelId, "Legacy parsing changed");
            }),
            ("legacy-missing-value", () => Ensure(Flags.Parse(["--tunnel"]).Str("tunnel") == "", "Legacy missing-value behavior changed")),
            ("legacy-integer-fallback", () => Ensure(Flags.Parse(["--port", "abc"]).Int("port", 2222) == 2222, "Legacy integer fallback changed")),
            ("legacy-proxy", () =>
            {
                var f = Flags.Parse([TunnelId, "--port", "2222", "--direct", "localhost:22"]);
                Ensure(f.Arg(0) == TunnelId && f.Int("port", 0) == 2222 &&
                    f.Str("direct") == "localhost:22", "Legacy proxy arguments changed");
            }),
            ("legacy-direct-without-tunnel", () =>
            {
                var f = Flags.Parse(["--direct=localhost:22"]);
                Ensure(f.Arg(0) is null && f.Str("direct") == "localhost:22", "Direct-only parsing changed");
            }),
            ("legacy-boolean-and-positionals", () =>
            {
                var f = Flags.Parse(["before", "-q", "--prune=false", "after"], "q", "prune");
                Ensure(f.Bool("q", false) && !f.Bool("prune", false), "Legacy boolean parsing changed");
                Equal(["before", "after"], f.Positionals);
            }),
            ("task-utf16-bom", () =>
            {
                var path = WriteTask(root);
                var bytes = File.ReadAllBytes(path);
                Ensure(bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe, "Task XML needs a UTF-16LE BOM");
                Ensure(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
                    .StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", StringComparison.Ordinal),
                    "Task XML declaration does not match its encoding");
            }),
            ("task-xml-description", () =>
            {
                var doc = XDocument.Load(WriteTask(root));
                Ensure(doc.Root?.Element(TaskNs + "RegistrationInfo")?.Element(TaskNs + "Description")?.Value == Description,
                    "Task XML description did not preserve U+2014");
            }),
            ("task-xml-arguments", () =>
            {
                var doc = XDocument.Load(WriteTask(root));
                var arguments = TaskArguments(doc);
                Ensure(arguments.Contains("--tunnel " + TunnelId, StringComparison.Ordinal), "Task XML lost the tunnel");
                Ensure(arguments.Contains("--user A&B", StringComparison.Ordinal), "XML escaping changed the user");
                Ensure(arguments.Contains("--alias \"my host\"", StringComparison.Ordinal), "Argument quoting changed");
                Ensure(arguments.Contains("tool & host.exe", StringComparison.Ordinal), "Executable escaping changed");
            }),
        };

        foreach (var value in new[] { "true", "false", "0", "no", "off", "1" })
        {
            tests.Add(($"persist-{value}", () =>
            {
                if (value is "false" or "0" or "no" or "off")
                    Reject(["--persist=" + value, "--tunnel", TunnelId], "services always persist; omit --persist=" + value);
                else
                    Tunnel(["--persist=" + value, "--tunnel", TunnelId]);
            }));
            tests.Add(($"booleans-{value}", () =>
            {
                var options = ServiceInstallOptions.Parse(["--system-sshd=" + value, "--no-wsl-boot=" + value]);
                var expected = value is not ("false" or "0" or "no" or "off");
                Ensure(options.HostArgs.Contains("--system-sshd") == expected, "system-sshd boolean changed");
                Ensure(options.NoWslBoot == expected, "no-wsl-boot boolean changed");
            }));
        }
        foreach (var name in new[] { "port", "user", "alias", "tunnel", "expiration" })
        {
            tests.Add(($"missing-{name}", () => Reject(["--" + name], $"--{name} requires a value")));
            tests.Add(($"empty-{name}", () => Reject(["--" + name + "="], $"--{name} requires a value")));
            tests.Add(($"empty-next-{name}", () => Reject(["--" + name, ""], $"--{name} requires a value")));
            tests.Add(($"flag-as-{name}", () => Reject(["--" + name, "--persist"], $"--{name} requires a value")));
        }

        var failures = 0;
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); }
        }
        Console.WriteLine($"{tests.Count - failures} passed; {failures} failed; {tests.Count} total");
        return failures == 0 ? 0 : 1;
    }

    private static void Tunnel(string[] args)
    {
        var hostArgs = ServiceInstallOptions.Parse(args).HostArgs;
        Equal(["--persist", "--port", "2222", "--tunnel", TunnelId], hostArgs);
    }

    private static void Reject(string[] args, string message)
    {
        try { ServiceInstallOptions.Parse(args); }
        catch (ArgumentException e) when (e.Message == message) { return; }
        throw new InvalidOperationException("Expected error: " + message);
    }

    private static string WriteTask(string root)
    {
        var options = ServiceInstallOptions.Parse(["--persist", "--tunnel", TunnelId, "--user", "A&B", "--alias", "my host"]);
        var path = Path.Combine(root, "task.xml");
        SchtasksManager.WriteXml(path, new ServiceConfig(
            Path.Combine(root, "tool & host.exe"), options.HostArgs, []));
        return path;
    }

    private static string TaskArguments(XDocument doc) =>
        doc.Root?.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec")?.Element(TaskNs + "Arguments")?.Value
            ?? throw new InvalidOperationException("Task action arguments are missing");

    private static async Task CliSmokeAsync(string root, string exe)
    {
        if (!File.Exists(exe)) throw new FileNotFoundException("CLI binary is missing", exe);
        var blockedHome = Path.Combine(root, "blocked-home");
        File.WriteAllText(blockedHome, "not a directory");
        var cases = new (string[] Args, int ExitCode, string Message)[]
        {
            (["--help"], 0, "services always persist"),
            (["-h"], 0, "services always persist"),
            (["help"], 0, "services always persist"),
            (["--persist", "--tunnel"], 1, "dtssh: --tunnel requires a value"),
            (["--typo", "--tunnel", TunnelId], 1, "dtssh: unknown option \"--typo\""),
            (["--tunnel", TunnelId, "extra"], 1, "dtssh: unexpected argument \"extra\""),
            (["--user", "--tunnel", TunnelId], 1, "dtssh: --user requires a value"),
            (["--port", "abc"], 1, "dtssh: --port requires an integer"),
            (["--tunnel="], 1, "dtssh: --tunnel requires a value"),
            (["--persist=false"], 1, "dtssh: services always persist; omit --persist=false"),
            (["--persist=0"], 1, "dtssh: services always persist; omit --persist=0"),
            (["--persist=no"], 1, "dtssh: services always persist; omit --persist=no"),
            (["--persist=off"], 1, "dtssh: services always persist; omit --persist=off"),
        };
        foreach (var test in cases)
        {
            var start = new ProcessStartInfo(Path.GetFullPath(exe))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // If validation regresses, directory creation fails before any real
            // tool download or service installation can occur.
            start.Environment["DTSSH_HOME"] = blockedHome;
            start.ArgumentList.Add("service");
            start.ArgumentList.Add("install");
            foreach (var arg in test.Args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("CLI did not start");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                throw new TimeoutException("CLI validation did not finish");
            }
            var output = await stdout;
            var errors = await stderr;
            Ensure(child.ExitCode == test.ExitCode && output.Length == 0 &&
                (test.ExitCode == 0 ? errors.Contains(test.Message, StringComparison.Ordinal) : errors.TrimEnd() == test.Message),
                $"CLI [{string.Join(' ', test.Args)}]: exit={child.ExitCode}, stdout={output}, stderr={errors}");
            Ensure(File.ReadAllText(blockedHome) == "not a directory", "CLI touched the blocked data root");
            Console.WriteLine($"PASS native CLI {string.Join(' ', test.Args)}: exit={child.ExitCode}, validated before setup");
        }
        Console.WriteLine($"{cases.Length} native CLI checks passed");
    }

    private static async Task SchedulerSmokeAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Task Scheduler requires Windows");
        var path = WriteTask(root);
        var bytes = File.ReadAllBytes(path);
        Ensure(bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe, "Production task file has no UTF-16LE BOM");
        var doc = XDocument.Load(path);
        Ensure(doc.Root?.Element(TaskNs + "RegistrationInfo")?.Element(TaskNs + "Description")?.Value == Description,
            "Production task description is incorrect");
        var originalArguments = TaskArguments(doc);
        Ensure(originalArguments.Contains("--tunnel " + TunnelId, StringComparison.Ordinal), "Production task lost tunnel");
        Console.WriteLine("PASS original production XML: UTF-16LE BOM, exact description, explicit tunnel");

        // Only the disabled, inert copy is registered. Never call Install here:
        // it overwrites and starts the real \dtssh-host task.
        doc.Root!.Element(TaskNs + "Triggers")!.Remove();
        doc.Root.Element(TaskNs + "Settings")!.Element(TaskNs + "Enabled")!.Value = "false";
        var exec = doc.Root.Element(TaskNs + "Actions")!.Element(TaskNs + "Exec")!;
        var testExe = Environment.ProcessPath ?? throw new InvalidOperationException("Test executable is unavailable");
        exec.Element(TaskNs + "Command")!.Value = testExe;
        var inertArguments = "--task-noop " + originalArguments;
        if (Path.GetFileNameWithoutExtension(testExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            inertArguments = "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" " + inertArguments;
        exec.Element(TaskNs + "Arguments")!.Value = inertArguments;
        var fixture = Path.Combine(root, "disabled-task.xml");
        using (var writer = System.Xml.XmlWriter.Create(fixture,
            new System.Xml.XmlWriterSettings { Encoding = Encoding.Unicode }))
            doc.Save(writer);

        var name = "\\dtssh-service-test-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            await SchtasksAsync("/Create", "/TN", name, "/XML", fixture);
            created = true;
            // schtasks stdout uses a console code page. Read the registered
            // definition as a Unicode BSTR so that decoding cannot mask the result.
            var imported = ReadRegisteredTask(name);
            Ensure(imported.Root?.Element(TaskNs + "RegistrationInfo")?.Element(TaskNs + "Description")?.Value == Description,
                "Task Scheduler changed the description");
            Ensure(TaskArguments(imported) == inertArguments, "Task Scheduler changed the action arguments");
            Ensure(imported.Root?.Element(TaskNs + "Settings")?.Element(TaskNs + "Enabled")?.Value == "false",
                "Task Scheduler did not retain disabled state");
            Ensure(imported.Root?.Element(TaskNs + "Triggers")?.Elements().Any() != true,
                "Task Scheduler added a trigger");
            Console.WriteLine($"PASS scheduler roundtrip: {name}, exact description and tunnel arguments, disabled, no triggers");
        }
        finally
        {
            if (created)
            {
                await SchtasksAsync("/Delete", "/TN", name, "/F");
                var result = await Proc.RunAsync("schtasks.exe", ["/Query", "/TN", name, "/XML"]);
                Ensure(result.ExitCode == 1 && result.Stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase),
                    $"Could not confirm task removal: {result.ExitCode} {result.Stderr}");
                Console.WriteLine($"PASS scheduler cleanup: {name} is absent");
            }
        }
    }

    private static async Task<string> SchtasksAsync(params string[] args)
    {
        var result = await Proc.RunAsync("schtasks.exe", args);
        Ensure(result.Ok, $"schtasks {string.Join(' ', args)}: exit {result.ExitCode}\n{result.Stdout}{result.Stderr}");
        return result.Stdout;
    }

    private static XDocument ReadRegisteredTask(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler COM API is unavailable"))
            ?? throw new InvalidOperationException("Task Scheduler COM API did not start");
        try
        {
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            try
            {
                dynamic task = folder.GetTask(name);
                try { return XDocument.Parse((string)task.Xml); }
                finally { Marshal.FinalReleaseComObject(task); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(scheduler); }
    }

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Ensure(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
