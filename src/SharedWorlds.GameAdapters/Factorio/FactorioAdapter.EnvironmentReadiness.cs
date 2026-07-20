using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    public async Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        PreparedWorld? prepared = null;
        try
        {
            prepared = await PrepareEnvironmentAsync(
                installation,
                requiredEnvironment,
                cancellationToken);
            return EnvironmentVerificationReport.Ready();
        }
        catch (EnvironmentReproductionException exception)
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    Code: "factorio-environment-mismatch",
                    Message: exception.Problem,
                    CanRepairAutomatically: false));
        }
        catch (FileNotFoundException exception)
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    Code: "factorio-local-file-missing",
                    Message: exception.Message,
                    CanRepairAutomatically: false));
        }
        catch (DirectoryNotFoundException exception)
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    Code: "factorio-local-directory-missing",
                    Message: exception.Message,
                    CanRepairAutomatically: false));
        }
        catch (UnauthorizedAccessException exception)
        {
            return EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    Code: "factorio-local-access-denied",
                    Message: exception.Message,
                    CanRepairAutomatically: false));
        }
        finally
        {
            if (prepared is not null)
            {
                DeleteOwnedWorkspace(prepared.WorkingDirectory);
            }
        }
    }

    public async Task<EnvironmentRepairResult> RepairEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);

        if (verification.IsReady)
        {
            return new EnvironmentRepairResult(
                Changed: false,
                Verification: verification,
                Message: "This device can already reproduce the exact Factorio environment. Nothing was changed.");
        }

        return new EnvironmentRepairResult(
            Changed: false,
            Verification: verification,
            Message: "SharedWorlds found an environment problem, but no safe automatic Factorio repair path is enabled yet. Nothing was changed.");
    }
}
