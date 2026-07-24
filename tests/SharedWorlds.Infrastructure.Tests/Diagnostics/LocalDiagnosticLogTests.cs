using SharedWorlds.Infrastructure.Diagnostics;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Diagnostics;

public sealed class LocalDiagnosticLogTests
{
    [Fact]
    public void SecretsAndUrlQueriesAreRedactedBeforeDiagnosticPersistence()
    {
        using var temp = new TemporaryDirectory();
        var exception = new InvalidOperationException(
            "Authorization: Bearer bearer-secret access_token=token-secret " +
            "https://api.example/path?ticket=query-secret#fragment");

        var incident = LocalDiagnosticLog.TryWriteException(exception, temp.Path);

        var path = Assert.IsType<string>(incident.LogPath);
        var persisted = File.ReadAllText(path);
        Assert.Contains($"IncidentId: {incident.Id}", persisted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void PathologicalExceptionDetailsAreTruncatedToBoundedIncidentFile()
    {
        using var temp = new TemporaryDirectory();
        var exception = new InvalidOperationException(new string('x', 200_000));

        var incident = LocalDiagnosticLog.TryWriteException(exception, temp.Path);

        var path = Assert.IsType<string>(incident.LogPath);
        var persisted = File.ReadAllText(path);
        Assert.Contains("<diagnostic-details-truncated>", persisted, StringComparison.Ordinal);
        Assert.True(
            new FileInfo(path).Length < 80 * 1024,
            $"Diagnostic incident unexpectedly grew to {new FileInfo(path).Length} bytes.");
    }

    [Fact]
    public void RepeatedFailuresRetainAtMostSixtyFourIncidentFiles()
    {
        using var temp = new TemporaryDirectory();

        for (var index = 0; index < 80; index++)
        {
            var incident = LocalDiagnosticLog.TryWriteException(
                new InvalidOperationException($"failure-{index}"),
                temp.Path);
            Assert.NotNull(incident.LogPath);
        }

        var files = Directory.GetFiles(temp.Path, "error-*.log", SearchOption.TopDirectoryOnly);
        Assert.Equal(64, files.Length);
    }

    [Fact]
    public void FirstSafeWriteDeletesLegacyUnredactedDailyLogs()
    {
        using var temp = new TemporaryDirectory();
        var legacy = System.IO.Path.Combine(temp.Path, "errors-2026-07-24.log");
        File.WriteAllText(legacy, "Bearer legacy-secret");

        var incident = LocalDiagnosticLog.TryWriteException(
            new InvalidOperationException("safe replacement"),
            temp.Path);

        Assert.NotNull(incident.LogPath);
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(temp.Path, "error-*.log", SearchOption.TopDirectoryOnly));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SharedWorlds.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
