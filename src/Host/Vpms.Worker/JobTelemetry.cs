using System.Diagnostics;
using Vpms.BuildingBlocks.Observability;

namespace Vpms.Worker;

/// <summary>Counts and times each background pass and traces it, so a job that stops working or starts failing shows up in metrics and alerts.</summary>
public static class JobTelemetry
{
    public static async Task RunAsync(string job, Func<Task> pass)
    {
        using var activity = VpmsTelemetry.Source.StartActivity("job " + job);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await pass();
            VpmsTelemetry.JobRun(job, true, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException)
        {
            throw; // shutting down is not a failure
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            VpmsTelemetry.JobRun(job, false, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }
}

/// <summary>
/// The worker has no web endpoint, so its liveness is a file touched every few seconds. A container probe runs <c>Vpms.Worker --healthcheck</c>,
/// which succeeds only while the file is fresh.
/// </summary>
public sealed class WorkerHeartbeat(IConfiguration configuration, TimeProvider clock, ILogger<WorkerHeartbeat> logger) : BackgroundService
{
    public static string PathFor(IConfiguration configuration) => configuration["Worker:HeartbeatFile"] ?? Path.Combine(Path.GetTempPath(), "vpms-worker.alive");

    public static bool IsAlive(IConfiguration configuration, TimeSpan maxAge) =>
        File.Exists(PathFor(configuration)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(PathFor(configuration)) <= maxAge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = PathFor(configuration);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await File.WriteAllTextAsync(path, clock.GetUtcNow().ToString("O"), stoppingToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not write the worker heartbeat file: {Type}", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
