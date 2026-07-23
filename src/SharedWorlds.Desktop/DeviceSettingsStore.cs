using System.IO;
using System.Text.Json;

namespace SharedWorlds.Desktop;

internal sealed class DeviceSettingsStore
{
    private const string DocumentType = "sharedworlds.device-settings";
    private const int CurrentSchemaVersion = 2;
    private const int LegacySchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    public DeviceSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<DeviceSettings> LoadOrCreateAsync(
        bool hasManagedWorlds,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            var initial = CreateInitial(hasManagedWorlds);
            await SaveAsync(initial, cancellationToken);
            return initial;
        }

        await using var stream = File.OpenRead(_path);
        var envelope = await JsonSerializer.DeserializeAsync<DeviceSettingsEnvelope>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (envelope is null ||
            !string.Equals(envelope.DocumentType, DocumentType, StringComparison.Ordinal) ||
            envelope.Payload is null ||
            envelope.SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidDataException(
                $"Device settings at '{_path}' are not a supported {DocumentType} document.");
        }

        if (envelope.SchemaVersion == CurrentSchemaVersion)
        {
            var installationId = ValidateInstallationId(envelope.Payload.InstallationId);
            return new DeviceSettings(
                envelope.Payload.AllowHosting,
                envelope.Payload.HostingPreferenceExplicit,
                installationId);
        }

        // v1 predated installation-bound Steward authentication. Preserve the user's existing
        // hosting preference and add exactly one stable installation identity during migration.
        var migrated = new DeviceSettings(
            envelope.Payload.AllowHosting,
            envelope.Payload.HostingPreferenceExplicit,
            CreateInstallationId());
        await SaveAsync(migrated, cancellationToken);
        return migrated;
    }

    public async Task SaveAsync(
        DeviceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var installationId = ValidateInstallationId(settings.InstallationId);

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Cannot resolve settings directory for '{_path}'.");
        Directory.CreateDirectory(directory);

        var envelope = new DeviceSettingsEnvelope(
            DocumentType,
            CurrentSchemaVersion,
            new DeviceSettingsPayload(
                settings.AllowHosting,
                settings.HostingPreferenceExplicit,
                installationId));
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    envelope,
                    SerializerOptions,
                    cancellationToken);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static DeviceSettings CreateInitial(bool hasManagedWorlds)
        => new(
            AllowHosting: hasManagedWorlds,
            HostingPreferenceExplicit: false,
            InstallationId: CreateInstallationId());

    private static string CreateInstallationId() => Guid.NewGuid().ToString("N");

    private static string ValidateInstallationId(string? installationId)
    {
        if (string.IsNullOrWhiteSpace(installationId) ||
            installationId.Any(char.IsWhiteSpace) ||
            installationId.Length > 128)
        {
            throw new InvalidDataException("Device settings contain an invalid Steward installation identity.");
        }

        return installationId;
    }

    private sealed record DeviceSettingsEnvelope(
        string DocumentType,
        int SchemaVersion,
        DeviceSettingsPayload? Payload);

    private sealed record DeviceSettingsPayload(
        bool AllowHosting,
        bool HostingPreferenceExplicit,
        string? InstallationId);
}

internal sealed record DeviceSettings(
    bool AllowHosting,
    bool HostingPreferenceExplicit,
    string InstallationId);
