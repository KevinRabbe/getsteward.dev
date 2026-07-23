namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldWorldOptionIniMirrorTests
{
    [Fact]
    public void MirrorsKnownScalarSettingsAndOverridesOnlyManagementFields()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [
                new("ExpRate", "FloatProperty", "2.6", null, 4, false),
                new("BaseCampWorkerMaxNum", "IntProperty", "15", null, 4, false),
                new("bIsPvP", "BoolProperty", "False", null, 0, false),
                new("DeathPenalty", "EnumProperty", "EPalOptionWorldDeathPenalty::All", "EPalOptionWorldDeathPenalty", 0, false),
                new("ServerName", "StrProperty", "Steward Test", null, 0, false),
                new("AdminPassword", "StrProperty", "canonical-secret", null, 0, true),
                new("RESTAPIEnabled", "BoolProperty", "False", null, 0, false),
                new("RESTAPIPort", "IntProperty", "8212", null, 4, false)
            ]);

        var mirror = PalworldWorldOptionIniMirror.Create(snapshot, "runtime-secret", 9123);

        Assert.Contains("ExpRate=2.6", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("BaseCampWorkerMaxNum=15", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("bIsPvP=False", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("DeathPenalty=All", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("ServerName=\"Steward Test\"", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("AdminPassword=\"runtime-secret\"", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("RESTAPIEnabled=True", mirror.Contents, StringComparison.Ordinal);
        Assert.Contains("RESTAPIPort=9123", mirror.Contents, StringComparison.Ordinal);
        Assert.DoesNotContain("canonical-secret", mirror.Contents, StringComparison.Ordinal);
        Assert.Equal(3, mirror.ManagementOverrides.Count);
    }

    [Fact]
    public void OpaqueSettingFailsClosed()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [new("UnknownSetting", "StructProperty", null, "UnknownType", 12, false)]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionIniMirror.Create(snapshot, "runtime-secret", 8212));

        Assert.Contains("unsupported property type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateSettingFailsClosed()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [
                new("ExpRate", "FloatProperty", "1", null, 4, false),
                new("ExpRate", "FloatProperty", "2", null, 4, false)
            ]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionIniMirror.Create(snapshot, "runtime-secret", 8212));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EPalOptionWorldDeathPenalty::ItemAndEquipment", "ItemAndEquipment")]
    [InlineData("None", "None")]
    public void RestExpectedEnumValueDropsUnrealTypePrefix(string encoded, string expected)
    {
        var setting = new PalworldWorldOptionSetting(
            "DeathPenalty",
            "EnumProperty",
            encoded,
            "EPalOptionWorldDeathPenalty",
            0,
            false);

        Assert.Equal(expected, PalworldWorldOptionIniMirror.NormalizeExpectedRestValue(setting));
    }

    [Fact]
    public void UnsafeControlCharacterInStringFailsClosed()
    {
        var snapshot = new PalworldWorldOptionSettingsSnapshot(
            "PlM/0x31",
            [new("ServerName", "StrProperty", "bad\nname", null, 0, false)]);

        Assert.Throws<InvalidDataException>(() =>
            PalworldWorldOptionIniMirror.Create(snapshot, "runtime-secret", 8212));
    }
}
