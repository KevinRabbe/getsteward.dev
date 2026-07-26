using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.Desktop;

internal sealed class FriendsBuildCredentialStore
{
    private const int MaximumCredentialCharacters = 256;
    private const int MaximumProtectedBytes = 4096;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("Steward Friends Build bootstrap credential v1");
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _path;

    public FriendsBuildCredentialStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var fileInfo = new FileInfo(_path);
        if (fileInfo.Length is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("Stored Friends Build credential has an invalid size.");
        }

        var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
        if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
        {
            throw new InvalidDataException("Stored Friends Build credential has an invalid size.");
        }

        var plaintext = ProtectedData.Unprotect(
            protectedBytes,
            OptionalEntropy,
            DataProtectionScope.CurrentUser);
        try
        {
            var credential = StrictUtf8.GetString(plaintext);
            ValidateCredential(credential);
            return credential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SaveAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        ValidateCredential(credential);

        var plaintext = StrictUtf8.GetBytes(credential);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (protectedBytes.Length is <= 0 or > MaximumProtectedBytes)
            {
                throw new InvalidDataException("Protected Friends Build credential has an invalid size.");
            }

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Friends Build credential path has no parent directory.");
            Directory.CreateDirectory(directory);

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static void ValidateCredential(string credential)
    {
        if (string.IsNullOrWhiteSpace(credential) ||
            credential.Length > MaximumCredentialCharacters ||
            credential.Any(char.IsWhiteSpace) ||
            credential.Any(char.IsControl))
        {
            throw new InvalidDataException("Friends Build credential has an invalid format.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
