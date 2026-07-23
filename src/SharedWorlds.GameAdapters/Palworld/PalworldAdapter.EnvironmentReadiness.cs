using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed partial class PalworldAdapter
{
    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            PalworldDedicatedServerHosting.VerifyEnvironment(installation, requiredEnvironment));
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
                Message: "This device can already reproduce the exact Palworld dedicated-server environment. Nothing was changed.");
        }

        return new EnvironmentRepairResult(
            Changed: false,
            Verification: verification,
            Message: "Steward found a Palworld environment problem, but no validated automatic repair path is enabled. Nothing was changed.");
    }
}
