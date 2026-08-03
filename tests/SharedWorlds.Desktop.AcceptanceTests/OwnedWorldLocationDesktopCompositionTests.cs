using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedWorldLocationDesktopCompositionTests
{
    [Fact]
    public void RuntimeReusesItsSingleAccessSessionForOwnedLocationTransport()
    {
        var runtime = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs"));

        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
        Assert.Contains(
            "var createdAccessSession = new StewardAccessSession(",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "var ownedWorldLocations = new StewardOwnedWorldLocationClient(",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "await createdAccessSession.GetAccessTokenAsync(cancellationToken)",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "public StewardOwnedWorldLocationClient OwnedWorldLocations { get; }",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnedWorldLocations = ownedWorldLocations;",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallationRegistrationCompletesBeforeRuntimeReplacement()
    {
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));

        var create = RequiredIndex(
            composition,
            "var next = StewardDesktopRemoteRuntime.Create(");
        var registrationTry = RequiredIndex(composition, "        try\n", create);
        var register = RequiredIndex(
            composition,
            "await next.OwnedWorldLocations.RegisterCurrentInstallationAsync(",
            registrationTry);
        var machineName = RequiredIndex(composition, "Environment.MachineName", register);
        var protocolCheck = RequiredIndex(composition, "registration.IsConflict", register);
        var cancellationFence = RequiredIndex(
            composition,
            "cancellationToken.ThrowIfCancellationRequested();",
            protocolCheck);
        var registrationCatch = RequiredIndex(composition, "        catch\n", cancellationFence);
        var disposeCandidate = RequiredIndex(composition, "next.Dispose();", registrationCatch);
        var previous = RequiredIndex(composition, "var previous = _remoteRuntime;", disposeCandidate);
        var activate = RequiredIndex(composition, "_remoteRuntime = next;", previous);
        var disposePrevious = RequiredIndex(composition, "previous?.Dispose();", activate);

        Assert.True(create < registrationTry);
        Assert.True(registrationTry < register);
        Assert.True(register < machineName);
        Assert.True(machineName < protocolCheck);
        Assert.True(protocolCheck < cancellationFence);
        Assert.True(cancellationFence < registrationCatch);
        Assert.True(registrationCatch < disposeCandidate);
        Assert.True(disposeCandidate < previous);
        Assert.True(previous < activate);
        Assert.True(activate < disposePrevious);
        Assert.Equal(1, CountOccurrences(composition, "_remoteRuntime = next;"));
        Assert.Contains(
            "Steward did not confirm this Safe World installation registration.",
            composition,
            StringComparison.Ordinal);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
