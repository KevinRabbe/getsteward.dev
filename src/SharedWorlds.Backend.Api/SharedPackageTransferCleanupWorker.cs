using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.Api;

public sealed record SharedPackageTransferCleanupWorkerOptions
{
    public SharedPackageTransferCleanupWorkerOptions(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1) || interval > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        Interval = interval;
    }

    public TimeSpan Interval { get; }

    public static SharedPackageTransferCleanupWorkerOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromMinutes(15));
}

/// <summary>
/// Low-frequency background reconciliation for BE-3 transfer-session artifacts.
///
/// Multiple API instances may run this worker concurrently. Destructive persistence changes are
/// guarded by transfer owner/state compare-and-set operations, multipart abort is idempotent, and
/// verified candidate objects are never physically deleted by this worker.
/// </summary>
public sealed class SharedPackageTransferCleanupWorker : BackgroundService
{
    private readonly SharedPackageTransferCleanupService _cleanup;
    private readonly SharedPackageTransferCleanupWorkerOptions _options;
    private readonly ILogger<SharedPackageTransferCleanupWorker> _logger;

    public SharedPackageTransferCleanupWorker(
        SharedPackageTransferCleanupService cleanup,
        SharedPackageTransferCleanupWorkerOptions options,
        ILogger<SharedPackageTransferCleanupWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _cleanup = cleanup;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var result = await _cleanup.RunOnceAsync(stoppingToken);
                if (HasActivity(result))
                {
                    _logger.LogInformation(
                        "Transfer cleanup completed. ExpiredActive={ExpiredActive}; RepairedFinalized={RepairedFinalized}; PartialUploadsAborted={PartialUploadsAborted}; TransferRecordsDeleted={TransferRecordsDeleted}; RetainedVerifiedCandidates={RetainedVerifiedCandidates}; CleanupEligibleVerifiedCandidates={CleanupEligibleVerifiedCandidates}; IntegrityFailuresPreserved={IntegrityFailuresPreserved}; PublicationConflictsPreserved={PublicationConflictsPreserved}",
                        result.ExpiredActiveTransfers,
                        result.RepairedFinalizedTransfers,
                        result.PartialUploadsAborted,
                        result.TransferRecordsDeleted,
                        result.RetainedVerifiedCandidates,
                        result.VerifiedCandidatesCleanupEligible,
                        result.IntegrityFailuresPreserved,
                        result.PublicationConflictsPreserved);
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
                    "Transfer cleanup pass failed. The worker will retry on the next scheduled pass.");
            }
        }
    }

    private static bool HasActivity(SharedPackageTransferCleanupResult result)
        => result.ExpiredActiveTransfers != 0 ||
           result.RepairedFinalizedTransfers != 0 ||
           result.PartialUploadsAborted != 0 ||
           result.TransferRecordsDeleted != 0 ||
           result.RetainedVerifiedCandidates != 0 ||
           result.VerifiedCandidatesCleanupEligible != 0 ||
           result.IntegrityFailuresPreserved != 0 ||
           result.PublicationConflictsPreserved != 0;
}
