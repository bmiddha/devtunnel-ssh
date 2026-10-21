namespace Dtssh.Commands;

internal static class HostShutdown
{
    internal static async Task<bool> WaitForShutdownAsync(
        Func<Task, CancellationToken, Task> wait, CancellationTokenSource cts)
    {
        var signalLock = new object();
        var closed = false;
        var stopping = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSignal(object? _, EventArgs __)
        {
            lock (signalLock)
            {
                if (closed || stopping) return;
                stopping = true;
                Console.Error.WriteLine("\ndtssh: tunnel host stopped.");
                cts.Cancel();
                done.TrySetResult();
            }
        }
        ConsoleCancelEventHandler onCancelKeyPress = (s, e) => { e.Cancel = true; OnSignal(s, e); };

        try
        {
            Console.CancelKeyPress += onCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnSignal;

            try { await wait(done.Task, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            // Unsubscribing cannot recall a callback that is already in flight.
            lock (signalLock) closed = true;
            Console.CancelKeyPress -= onCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit -= OnSignal;
        }
        return stopping;
    }
}
