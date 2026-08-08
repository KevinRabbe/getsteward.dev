using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class LegacyAuthorityRetirementApiCompositionTests
{
    [Fact]
    public void ProgramInitializesRetirementSchemaBeforeMappingEndpoint()
    {
        var source = Read("src/SharedWorlds.Backend.Api/Program.cs");
        var baseSchema = RequiredIndex(source, "await PostgreSqlBackendSchema.InitializeAsync(dataSource);");
        var retirementSchema = RequiredIndex(
            source,
            "await PostgreSqlLegacySharedWorldAuthorityRetirementSchema.InitializeAsync(dataSource);",
            baseSchema);
        var map = RequiredIndex(
            source,
            "app.MapStewardLegacyAuthorityRetirementApiV1();",
            retirementSchema);

        Assert.True(baseSchema < retirementSchema);
        Assert.True(retirementSchema < map);
        Assert.Contains(
            "builder.Services.AddSingleton<PostgreSqlLegacySharedWorldAuthorityRetirementStore>();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddSingleton<ILegacySharedWorldAuthorityRetirementStore>",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetirementPostAcceptsOnlyReservationTupleAndUsesAuthenticatedInstallation()
    {
        var source = Read("src/SharedWorlds.Backend.Api/StewardLegacyAuthorityRetirementApi.cs");
        var requestRecord = RequiredIndex(
            source,
            "public sealed record RetireLegacyAuthorityRequest(\n        Guid SessionId,\n        long Generation);");
        var authenticate = RequiredIndex(
            source,
            "var caller = await AuthenticateAsync(request, sessions, cancellationToken);",
            0);
        var retirementCall = RequiredIndex(
            source,
            "var result = await retirement.RetireAsync(",
            authenticate);
        var authenticatedInstallation = RequiredIndex(
            source,
            "caller.InstallationId,",
            retirementCall);

        Assert.True(authenticate < retirementCall);
        Assert.True(retirementCall < authenticatedInstallation);
        Assert.True(requestRecord >= 0);
        Assert.DoesNotContain(
            "RetireLegacyAuthorityRequest(\n        string InstallationId",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RetireLegacyAuthorityRequest(\n        string Provider",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RetireLegacyAuthorityRequest(\n        string ExternalId",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessEvidenceIncludesExactFrozenCanonicalHeadAndMembership()
    {
        var source = Read("src/SharedWorlds.Backend.Api/StewardLegacyAuthorityRetirementApi.cs");

        Assert.Contains(
            "result.RetiredStateRevisionId is null",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "result.RetiredStateRevisionId.Value.Value",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "result.RetiredEnvironmentRevisionId?.Value",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StableIdentitySetFingerprint.IsCanonicalFingerprint(\n                result.RetiredActiveMembersFingerprint)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "result.RetiredActiveMembersFingerprint!",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Guid StateRevisionId,\n        Guid? EnvironmentRevisionId,\n        string ActiveMembersFingerprint,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionalLegacyAccessStateIsNotConvertibleToPeerAuthority()
    {
        var source = Read("src/SharedWorlds.Backend.Api/StewardLegacyAuthorityRetirementApi.cs");

        Assert.Contains(
            "LegacySharedWorldAuthorityRetirementStatus.AccessStateNotReady => Results.Conflict(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"LegacyAccessStateNotReady\"",
            source,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
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
