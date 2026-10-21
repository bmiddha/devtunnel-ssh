using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dtssh.Commands;

internal static class Program
{
    private static readonly string[] Scenarios =
    [
        "completed",
        "async-completed",
        "canceled",
        "faulted",
        "async-faulted",
        "handlers-released",
        "handlers-released-after-fault",
    ];

    private const string RelayFailure = "relay cleanup failed";

    private static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(3);
        if (args is ["--child", var childScenario])
        {
            await RunChildAsync(childScenario);
            Console.WriteLine($"completed: {childScenario}");
            return 0;
        }
        if (args.Length != 0) return 2;

        var failures = 0;
        foreach (var scenario in Scenarios)
        {
            var start = new ProcessStartInfo(
                Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--child");
            start.ArgumentList.Add(scenario);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Child did not start");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var timedOut = false;
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            var output = await stdout;
            var errors = await stderr;
            if (!timedOut && child.ExitCode == 0 &&
                output.Trim() == $"completed: {scenario}" && errors.Length == 0)
            {
                Console.WriteLine($"PASS {scenario} (including process exit)");
            }
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL {scenario}: exit={child.ExitCode}, timedOut={timedOut}");
                Console.Error.Write(output);
                Console.Error.Write(errors);
            }
        }
        Console.WriteLine($"{Scenarios.Length - failures} passed; {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunChildAsync(string scenario)
    {
        if (scenario is "handlers-released" or "handlers-released-after-fault")
        {
            var reference = CompleteScope(scenario == "handlers-released-after-fault");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Ensure(!reference.IsAlive, "An event handler still retains the disposed cancellation source");
            return;
        }

        using var cts = new CancellationTokenSource();
        Func<Task, CancellationToken, Task> wait = scenario switch
        {
            "completed" => static (_, _) => Task.CompletedTask,
            "async-completed" => static async (_, _) => await Task.Yield(),
            "canceled" => (_, token) =>
            {
                cts.Cancel();
                return Task.FromCanceled(token);
            },
            "faulted" => static (_, _) => throw new InvalidOperationException(RelayFailure),
            "async-faulted" => static async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException(RelayFailure);
            },
            _ => throw new ArgumentException($"Unknown scenario: {scenario}"),
        };
        var expectFailure = scenario is "faulted" or "async-faulted";
        try
        {
            var stopping = await HostShutdown.WaitForShutdownAsync(wait, cts);
            Ensure(!expectFailure, "Relay cleanup failure was swallowed");
            Ensure(!stopping, "Completion without a stop signal was treated as a clean stop");
        }
        catch (InvalidOperationException e) when (expectFailure && e.Message == RelayFailure) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteScope(bool fault)
    {
        using var cts = new CancellationTokenSource();
        var reference = new WeakReference(cts);
        try
        {
            var stopping = HostShutdown.WaitForShutdownAsync(
                fault
                    ? static (_, _) => throw new InvalidOperationException(RelayFailure)
                    : static (_, _) => Task.CompletedTask,
                cts).GetAwaiter().GetResult();
            Ensure(!fault && !stopping, "Unexpected shutdown result");
        }
        catch (InvalidOperationException e) when (fault && e.Message == RelayFailure) { }
        return reference;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
