using System.Security.Cryptography;
using SharedWorlds.Desktop;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class FriendsBuildCredentialStoreTests
{
    [Fact]
    public async Task SaveLoadAndDeleteRoundTripUsesProtectedCurrentUserStorage()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "friends-build-credential.bin");
        var store = new FriendsBuildCredentialStore(path);
        const string credential =
            "st_friend_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq";

        await store.SaveAsync(credential);

        Assert.True(File.Exists(path));
        var protectedBytes = await File.ReadAllBytesAsync(path);
        Assert.NotEmpty(protectedBytes);
        Assert.DoesNotContain(
            credential,
            Convert.ToBase64String(protectedBytes),
            StringComparison.Ordinal);

        var loaded = await store.LoadAsync();
        Assert.Equal(credential, loaded);

        store.Delete();
        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task InvalidProtectedPayloadFailsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "friends-build-credential.bin");
        var store = new FriendsBuildCredentialStore(path);
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(64));

        await Assert.ThrowsAsync<CryptographicException>(() => store.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "steward-credential-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
