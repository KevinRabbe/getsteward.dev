namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldManagedHostLaunchTests
{
    [Fact]
    public void ManagedHostLaunchForcesNativeNoModsMode()
    {
        var serverRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "steward-palworld-server"));
        var context = new PalworldManagedHostLaunchContext(
            serverRoot,
            Path.Combine(serverRoot, "PalServer.exe"),
            "A1B2C3D4",
            Path.Combine(serverRoot, "Pal", "Saved", "SaveGames", "0", "A1B2C3D4"));

        var startInfo = PalworldDedicatedServerHosting.CreateManagedHostStartInfo(context);

        Assert.Equal(context.ServerExecutable, startInfo.FileName);
        Assert.Equal(context.ServerRoot, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Single(startInfo.ArgumentList);
        Assert.Equal("-NoMods", startInfo.ArgumentList[0]);
    }
}
