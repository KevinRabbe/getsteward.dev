using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    public static async Task<PreparedWorld> PrepareManagedEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        string managedWorkspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(managedWorkspace);

        if (!string.Equals(requiredEnvironment.AdapterId, "factorio", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Factorio.",
                nameof(requiredEnvironment));
        }

        FactorioModInputSafety.RequireReproductionInputs(installation);

        var workspace = Path.GetFullPath(managedWorkspace);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "Safe World did not create the planned Factorio managed workspace before adapter materialization.");
        }

        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing linked Factorio managed workspace '{workspace}'.");
        }

        var workspaceConfigDirectory = Path.Combine(workspace, WorkspaceConfigDirectoryName);
        var workspaceUserDataDirectory = Path.Combine(workspace, WorkspaceUserDataDirectoryName);
        var workspaceSavesDirectory = Path.Combine(workspaceUserDataDirectory, SavesDirectoryName);
        var workspaceModsDirectory = Path.Combine(workspace, ModsDirectoryName);

        Directory.CreateDirectory(workspaceConfigDirectory);
        Directory.CreateDirectory(workspaceSavesDirectory);
        Directory.CreateDirectory(workspaceModsDirectory);

        try
        {
            await CreateWorkspaceConfigAsync(
                installation,
                Path.Combine(workspaceConfigDirectory, WorkspaceConfigFileName),
                workspaceUserDataDirectory,
                cancellationToken);

            await PrepareWorkspaceModsAsync(
                installation,
                requiredEnvironment,
                workspaceModsDirectory,
                cancellationToken);

            return new PreparedWorld(installation, workspace, requiredEnvironment);
        }
        catch
        {
            // Core owns the managed workspace directory and its PreparationPending journal. Do not
            // delete it here: startup reconciliation must be able to inspect/discard it by WorkspaceId.
            throw;
        }
    }
}
