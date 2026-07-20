using System.IO;
using System.Text.Json;

namespace SharedWorlds.Desktop;

internal sealed class DeviceSettingsStore
{
    private const string DocumentType = "sharedworlds.device-settings";
    private const int SchemaVersion = 1;

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
            var initial = new DeviceSettings(
                AllowHosting: hasManagedWorlds,
                HostingPreferenceExplicit: false);
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
            envelope.SchemaVersion != SchemaVersion ||
            envelope.Payload is null)
        {
            throw new InvalidDataException(
                $"Device settings at '{_path}' are not a supported {DocumentType} v{SchemaVersion} document.");
        }

        return envelope.Payload;
    }

    public async Task SaveAsync(
        DeviceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException($"Cannot resolve settings directory for '{_path}'.");
        Directory.CreateDirectory(directory);

        var envelope = new DeviceSettingsEnvelope(
            DocumentType,
            SchemaVersion,
            settings);
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

    private sealed record DeviceSettingsEnvelope(
        string DocumentType,
        int SchemaVersion,
        DeviceSettings? Payload);
}

internal sealed record DeviceSettings(
    bool AllowHosting,
    bool HostingPreferenceExplicit);