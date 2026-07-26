using SharedWorlds.Desktop;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class StewardDesktopRemoteConfigurationTests
{
    [Fact]
    public void FriendsBuildPackageLoadsOnlyHttpsBackendCoordinate()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/api"
            }
            """);

        var loaded = StewardDesktopRemoteConfiguration.TryLoadFriendsBuildPackage(
            path,
            out var configuration,
            out var problem);

        Assert.True(loaded, problem);
        Assert.NotNull(configuration);
        Assert.Equal(
            StewardDesktopAuthenticationMode.FriendsBuild,
            configuration.AuthenticationMode);
        Assert.Equal("https://steward.example.test/api/", configuration.ApiBaseAddress.AbsoluteUri);
        Assert.Null(configuration.SteamAppId);
        Assert.Null(configuration.SteamWebApiIdentity);
    }

    [Fact]
    public void SteamReleasePackageLoadsExpectedNonSecretConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/api",
              "steamAppId": 123456,
              "steamWebApiIdentity": "steward"
            }
            """,
            StewardDesktopRemoteConfiguration.SteamReleaseConfigurationFileName);

        var loaded = StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            path,
            out var configuration,
            out var problem);

        Assert.True(loaded, problem);
        Assert.NotNull(configuration);
        Assert.Equal(StewardDesktopAuthenticationMode.Steam, configuration.AuthenticationMode);
        Assert.Equal("https://steward.example.test/api/", configuration.ApiBaseAddress.AbsoluteUri);
        Assert.Equal((uint)123456, configuration.SteamAppId);
        Assert.Equal("steward", configuration.SteamWebApiIdentity);
    }

    [Theory]
    [InlineData("http://steward.example.test/")]
    [InlineData("https://user:secret@steward.example.test/")]
    [InlineData("https://steward.example.test/?token=secret")]
    [InlineData("https://steward.example.test/#fragment")]
    public void FriendsBuildPackageRejectsUnsafeBackendCoordinate(string apiBaseUrl)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            $$"""
            {
              "schemaVersion": 1,
              "apiBaseUrl": "{{apiBaseUrl}}"
            }
            """);

        var loaded = StewardDesktopRemoteConfiguration.TryLoadFriendsBuildPackage(
            path,
            out var configuration,
            out var problem);

        Assert.False(loaded);
        Assert.Null(configuration);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("http://steward.example.test/")]
    [InlineData("https://user:secret@steward.example.test/")]
    [InlineData("https://steward.example.test/?token=secret")]
    [InlineData("https://steward.example.test/#fragment")]
    public void SteamReleasePackageRejectsUnsafeBackendCoordinate(string apiBaseUrl)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            $$"""
            {
              "schemaVersion": 1,
              "apiBaseUrl": "{{apiBaseUrl}}",
              "steamAppId": 123456,
              "steamWebApiIdentity": "steward"
            }
            """,
            StewardDesktopRemoteConfiguration.SteamReleaseConfigurationFileName);

        var loaded = StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            path,
            out var configuration,
            out var problem);

        Assert.False(loaded);
        Assert.Null(configuration);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("\"123456\"")]
    public void SteamReleasePackageRejectsInvalidExpectedAppId(string appIdJson)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            $$"""
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/",
              "steamAppId": {{appIdJson}},
              "steamWebApiIdentity": "steward"
            }
            """,
            StewardDesktopRemoteConfiguration.SteamReleaseConfigurationFileName);

        Assert.False(StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            path,
            out _,
            out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("tab\tidentity")]
    public void SteamReleasePackageRejectsInvalidWebApiIdentity(string identity)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.WriteConfiguration(
            $$"""
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/",
              "steamAppId": 123456,
              "steamWebApiIdentity": "{{identity}}"
            }
            """,
            StewardDesktopRemoteConfiguration.SteamReleaseConfigurationFileName);

        Assert.False(StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            path,
            out _,
            out _));
    }

    [Fact]
    public void FriendsBuildPackageRejectsUnknownOrFutureShape()
    {
        using var temporary = new TemporaryDirectory();
        var unknownField = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/",
              "credential": "must-never-be-packaged"
            }
            """,
            "unknown.json");
        var futureVersion = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 2,
              "apiBaseUrl": "https://steward.example.test/"
            }
            """,
            "future.json");

        Assert.False(StewardDesktopRemoteConfiguration.TryLoadFriendsBuildPackage(
            unknownField,
            out _,
            out _));
        Assert.False(StewardDesktopRemoteConfiguration.TryLoadFriendsBuildPackage(
            futureVersion,
            out _,
            out _));
    }

    [Fact]
    public void SteamReleasePackageRejectsUnknownOrFutureShape()
    {
        using var temporary = new TemporaryDirectory();
        var unknownField = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://steward.example.test/",
              "steamAppId": 123456,
              "steamWebApiIdentity": "steward",
              "publisherApiKey": "must-never-be-packaged"
            }
            """,
            "steam-unknown.json");
        var futureVersion = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 2,
              "apiBaseUrl": "https://steward.example.test/",
              "steamAppId": 123456,
              "steamWebApiIdentity": "steward"
            }
            """,
            "steam-future.json");

        Assert.False(StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            unknownField,
            out _,
            out _));
        Assert.False(StewardDesktopRemoteConfiguration.TryLoadSteamReleasePackage(
            futureVersion,
            out _,
            out _));
    }

    [Fact]
    public void MultiplePackageConfigurationSourcesFailClosed()
    {
        using var temporary = new TemporaryDirectory();
        var friendsPath = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://friends.example.test/"
            }
            """);
        var steamPath = temporary.WriteConfiguration(
            """
            {
              "schemaVersion": 1,
              "apiBaseUrl": "https://release.example.test/",
              "steamAppId": 123456,
              "steamWebApiIdentity": "steward"
            }
            """,
            StewardDesktopRemoteConfiguration.SteamReleaseConfigurationFileName);

        var loaded = StewardDesktopRemoteConfiguration.TryLoad(
            friendsPath,
            steamPath,
            out var configuration,
            out var problem);

        Assert.False(loaded);
        Assert.Null(configuration);
        Assert.Contains("ambiguous", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageConfigurationRejectsOversizedConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "oversized.json");
        File.WriteAllText(path, new string('x', 4097));

        var loaded = StewardDesktopRemoteConfiguration.TryLoadFriendsBuildPackage(
            path,
            out var configuration,
            out var problem);

        Assert.False(loaded);
        Assert.Null(configuration);
        Assert.NotNull(problem);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "steward-config-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteConfiguration(string content, string? fileName = null)
        {
            var path = System.IO.Path.Combine(
                Path,
                fileName ?? StewardDesktopRemoteConfiguration.FriendsBuildConfigurationFileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
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
