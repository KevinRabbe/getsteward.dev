using SharedWorlds.Backend.Worlds;

namespace SharedWorlds.Backend.Api;

/// <summary>
/// Low-frequency BE-D010 revision-retention cleanup. Multiple API instances may run concurrently:
/// retirement rechecks under the per-World database lock and object deletion is idempotent/retryable
/// through the durable cleanup queue.
/// </summary>
public sealed class SharedRevisionRetentionCleanupWorker : BackgroundService
{
    private readonly SharedRevisionRetentionCleanupService _cleanup;
    private readonly SharedPackageTransferCleanupWorkerOptions _workerOptions;
    private readonly ILogger<SharedRevisionRetentionCleanupWorker> _logger;

    public SharedRevisionRetentionCleanupWorker(
        SharedRevisionRetentionCleanupService cleanup,
        SharedPackageTransferCleanupWorkerOptions workerOptions,
        ILogger<SharedRevisionRetentionCleanupWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(workerOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _cleanup = cleanup;
        _workerOptions = workerOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_workerOptions.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var result = await _cleanup.RunOnceAsync(stoppingToken);
                if (HasActivity(result))
                {
                    _logger.LogInformation(
                        "Revision retention cleanup completed. CandidatesInspected={CandidatesInspected}; RevisionsRetired={RevisionsRetired}; NoLongerEligible={NoLongerEligible}; AlreadyGone={AlreadyGone}; ObjectDeletesCompleted={ObjectDeletesCompleted}; ObjectDeletesFailed={ObjectDeletesFailed}",
                        result.CandidatesInspected,
                        result.RevisionsRetired,
                        result.CandidatesNoLongerEligible,
                        result.CandidatesAlreadyGone,
                        result.ObjectDeletesCompleted,
                        result.ObjectDeletesFailed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Revision retention cleanup pass failed. The worker will retry on the next scheduled pass.");
            }
        }
    }

    private static bool HasActivity(SharedRevisionRetentionCleanupResult result)
        => result.CandidatesInspected != 0 ||
           result.RevisionsRetired != 0 ||
           result.CandidatesNoLongerEligible != 0 ||
           result.CandidatesAlreadyGone != 0 ||
           result.ObjectDeletesCompleted != 0 ||
           result.ObjectDeletesFailed != 0;
}
