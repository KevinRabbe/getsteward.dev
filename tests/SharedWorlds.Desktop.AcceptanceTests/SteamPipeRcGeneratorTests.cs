using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPipeRcGeneratorTests
{
    private const uint AppId = 123456789;
    private const uint DepotId = 123456790;

    [Fact]
    public void GeneratorVerifiesPeerProductAndEmitsPreviewUploadAndEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryDirectory();
        var product = Path.Combine(workspace.Path, "product");
        var output = Path.Combine(workspace.Path, "steampipe");
        Directory.CreateDirectory(product);
        WriteSyntheticPeerProduct(product, AppId);

        var result = RunGenerator(product, output);

        Assert.Equal(0, result.ExitCode);
        var preview = File.ReadAllText(Path.Combine(output, $"app_build_{AppId}_preview.vdf"));
        var upload = File.ReadAllText(Path.Combine(output, $"app_build_{AppId}_upload.vdf"));
        Assert.Contains($"\"AppID\" \"{AppId}\"", preview, StringComparison.Ordinal);
        Assert.Contains($"\"{DepotId}\"", preview, StringComparison.Ordinal);
        Assert.Contains("\"Preview\" \"1\"", preview, StringComparison.Ordinal);
        Assert.Contains("\"FileMapping\"", preview, StringComparison.Ordinal);
        Assert.Contains("\"LocalPath\" \"*\"", preview, StringComparison.Ordinal);
        Assert.Contains("\"DepotPath\" \".\"", preview, StringComparison.Ordinal);
        Assert.Contains("\"Recursive\" \"1\"", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Preview\"", upload, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLive", preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetLive", upload, StringComparison.OrdinalIgnoreCase);

        using var evidenceDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(output, "steampipe-rc-input.json")));
        var evidence = evidenceDocument.RootElement;
        Assert.Equal("steward.steampipe-rc-input", evidence.GetProperty("documentType").GetString());
        Assert.Equal(AppId, evidence.GetProperty("steamAppId").GetUInt32());
        Assert.Equal(DepotId, evidence.GetProperty("windowsDepotId").GetUInt32());
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", evidence.GetProperty("productCommitSha").GetString());
        Assert.Equal(64, evidence.GetProperty("productManifestSha256").GetString()!.Length);
        Assert.Equal(64, evidence.GetProperty("previewVdfSha256").GetString()!.Length);
        Assert.Equal(64, evidence.GetProperty("uploadVdfSha256").GetString()!.Length);
    }

    [Fact]
    public void GeneratorRefusesTamperedOrLegacyConfiguredProductBeforeOutputMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryDirectory();
        var product = Path.Combine(workspace.Path, "product");
        Directory.CreateDirectory(product);
        WriteSyntheticPeerProduct(product, AppId);

        var tamperedOutput = Path.Combine(workspace.Path, "tampered-output");
        File.AppendAllText(Path.Combine(product, "SharedWorlds.Desktop.exe"), "tampered", Encoding.UTF8);
        var tampered = RunGenerator(product, tamperedOutput);
        Assert.NotEqual(0, tampered.ExitCode);
        Assert.Contains("mismatch", tampered.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(tamperedOutput));

        Directory.Delete(product, recursive: true);
        Directory.CreateDirectory(product);
        WriteSyntheticPeerProduct(product, AppId);
        File.WriteAllText(Path.Combine(product, "steward-steam-release.json"), "{}", Encoding.UTF8);

        var legacyOutput = Path.Combine(workspace.Path, "legacy-output");
        var legacy = RunGenerator(product, legacyOutput);
        Assert.NotEqual(0, legacy.ExitCode);
        Assert.Contains("migration/legacy", legacy.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(legacyOutput));
    }

    [Fact]
    public void GeneratorHasNoCredentialOrAutomaticPromotionInputs()
    {
        var source = File.ReadAllText(FindRepositoryFile("tools/new-steam-peer-beta-steampipe.ps1"));
        var parameterBlockEnd = source.IndexOf("Set-StrictMode", StringComparison.Ordinal);
        Assert.True(parameterBlockEnd > 0);
        var parameters = source[..parameterBlockEnd];

        Assert.Contains("SteamAppId", parameters, StringComparison.Ordinal);
        Assert.Contains("WindowsDepotId", parameters, StringComparison.Ordinal);
        Assert.Contains("ProductDirectory", parameters, StringComparison.Ordinal);
        Assert.Contains("OutputDirectory", parameters, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SteamGuard", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProductKey", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetLive", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preview", source, StringComparison.Ordinal);
        Assert.Contains("acceptance-build.json", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", source, StringComparison.Ordinal);
        Assert.Contains("steward-steam-release.json", source, StringComparison.Ordinal);
        Assert.Contains("steward-friends-build.json", source, StringComparison.Ordinal);
        Assert.Contains("unmanifested file", source, StringComparison.Ordinal);
        Assert.Contains("Assert-DisjointDirectory", source, StringComparison.Ordinal);
    }

    private static ProcessResult RunGenerator(string product, string output)
    {
        var script = FindRepositoryFile("tools/new-steam-peer-beta-steampipe.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-SteamAppId");
        startInfo.ArgumentList.Add(AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-WindowsDepotId");
        startInfo.ArgumentList.Add(DepotId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-ProductDirectory");
        startInfo.ArgumentList.Add(product);
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(output);
        startInfo.ArgumentList.Add("-BuildDescription");
        startInfo.ArgumentList.Add("Steward peer RC generator acceptance");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);
        return new ProcessResult(process.ExitCode, stdoutTask.Result + Environment.NewLine + stderrTask.Result);
    }

    private static void WriteSyntheticPeerProduct(string product, uint appId)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["SharedWorlds.Desktop.exe"] = Encoding.UTF8.GetBytes("synthetic-desktop-executable"),
            ["steam_api64.dll"] = Encoding.UTF8.GetBytes("synthetic-steam-runtime"),
            ["steward-steam.json"] = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"steamAppId\":{appId}}}"),
        };

        foreach (var (relativePath, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(product, relativePath), bytes);
        }

        var manifest = new
        {
            documentType = "steward.e4-desktop-acceptance-build",
            schemaVersion = 2,
            commitSha = "0123456789abcdef0123456789abcdef01234567",
            builtAtUtc = "2026-08-11T12:00:00.0000000+00:00",
            runtime = "win-x64",
            configuration = "Release",
            selfContained = true,
            executable = "SharedWorlds.Desktop.exe",
            steamNativeRuntime = "steam_api64.dll",
            files = files.Select(pair => new
            {
                path = pair.Key.Replace('\\', '/'),
                byteSize = pair.Value.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
            }).ToArray(),
        };
        File.WriteAllText(
            Path.Combine(product, "acceptance-build.json"),
            JsonSerializer.Serialize(manifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "steward-steampipe-test-" + Guid.NewGuid().ToString("N"));
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
            catch
            {
                // Best effort: test temp cleanup must not mask the assertion result.
            }
        }
    }
}
