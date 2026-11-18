using System.Diagnostics;
using System.Runtime;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-371: gives memory back to Windows after a large job (a Self-Hosted Direct pull, a DNA import). At a customer the
/// worker held 3.6 GB of private memory after a Pull now and kept it while idle: the pull's data was garbage, but an
/// idle process allocates almost nothing, so .NET had no reason to collect it. A few seconds after the job ends (so its
/// request or scope has finished and nothing still holds its data), this runs one blocking, compacting, aggressive
/// collection, which also returns the freed memory to Windows. Calls close together share one collection. The pause
/// is a second or so, once per job.
/// </summary>
public sealed class MemoryReleaser(ILogger<MemoryReleaser> logger)
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);
    private int _pending;

    /// <summary>Asks for a collection shortly; returns at once. <paramref name="after"/> names the job, for the log.</summary>
    public void ReleaseSoon(string after)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay);
                Volatile.Write(ref _pending, 0);
                ReleaseNow(after);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _pending, 0);
                logger.LogWarning(ex, "Couldn't release memory after {Job}.", after);
            }
        });
    }

    /// <summary>The collection itself; public for tests.</summary>
    public (long BeforeMb, long AfterMb) ReleaseNow(string after)
    {
        using var process = Process.GetCurrentProcess();
        var before = process.PrivateMemorySize64 / (1024 * 1024);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        process.Refresh();
        var afterMb = process.PrivateMemorySize64 / (1024 * 1024);
        logger.LogInformation("Released memory after {Job}: private memory {BeforeMb:N0} MB, now {AfterMb:N0} MB.", after, before, afterMb);
        return (before, afterMb);
    }
}
