using System.Text.Json;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldRuntimeSettingsVerifierTests
{
    [Fact]
    public void VerifiesScalarAndObservedArrayRepresentationsSemantically()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [
                new("ExpRate", "FloatProperty", "2.6", null, 4, false),
                new("bIsPvP", "BoolProperty", "False", null, 0, false),
                new(
                    "CrossplayPlatforms",
                    "ArrayProperty",
                    "(EPalAllowConnectPlatform::Steam,EPalAllowConnectPlatform::Xbox,EPalAllowConnectPlatform::PS5,EPalAllowConnectPlatform::Mac)",
                    "EnumProperty",
                    64,
                    false),
                new("DenyTechnologyList", "ArrayProperty", "()", "NameProperty", 4, false),
                new("AdminPassword", "StrProperty", "canonical-secret", null, 0, true),
                new("RESTAPIEnabled", "BoolProperty", "False", null, 0, false),
                new("RESTAPIPort", "IntProperty", "8212", null, 4, false)
            ]);
        using var rest = JsonDocument.Parse(
            """
            {
              "ExpRate": 2.6,
              "bIsPvP": false,
              "CrossplayPlatforms": ["Steam", "Xbox", "PS5", "Mac"],
              "DenyTechnologyList": [],
              "RESTAPIEnabled": true,
              "RESTAPIPort": 8212
            }
            """);
        var overrides = new HashSet<string>(StringComparer.Ordinal)
        {
            "AdminPassword",
            "RESTAPIEnabled",
            "RESTAPIPort"
        };

        var result = PalworldRuntimeSettingsVerifier.Verify(
            snapshot,
            rest.RootElement,
            8212,
            overrides);

        Assert.True(result.IsMatch);
        Assert.Equal(4, result.VerifiedWorldSettingCount);
        Assert.Empty(result.Mismatches);
        Assert.Empty(result.UnexposedWorldSettings);
    }

    [Fact]
    public void ArrayOrderMismatchFails()
    {
        var setting = new PalworldWorldOptionSetting(
            "CrossplayPlatforms",
            "ArrayProperty",
            "(EPalAllowConnectPlatform::Steam,EPalAllowConnectPlatform::Xbox)",
            "EnumProperty",
            32,
            false);
        using var rest = JsonDocument.Parse("[\"Xbox\",\"Steam\"]");

        Assert.False(PalworldRuntimeSettingsVerifier.RestValueMatches(setting, rest.RootElement));
    }

    [Fact]
    public void ArrayMustBeJsonArrayOfStrings()
    {
        var setting = new PalworldWorldOptionSetting(
            "DenyTechnologyList",
            "ArrayProperty",
            "()",
            "NameProperty",
            4,
            false);
        using var rest = JsonDocument.Parse("\"\"");

        Assert.False(PalworldRuntimeSettingsVerifier.RestValueMatches(setting, rest.RootElement));
    }

    [Fact]
    public void MissingWorldSettingFailsClosed()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [new("ExpRate", "FloatProperty", "2.6", null, 4, false)]);
        using var rest = JsonDocument.Parse("{}");

        var result = PalworldRuntimeSettingsVerifier.Verify(
            snapshot,
            rest.RootElement,
            8212,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(result.IsMatch);
        Assert.Equal(["ExpRate"], result.UnexposedWorldSettings);
    }
}
