using System.Windows;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PortableWorldDropActivationTests
{
    [Fact]
    public void ResolvesExactlyOnePortableWorldPath()
    {
        var path = Path.GetFullPath("500h-megabase.safeworld");

        var resolved = PortableWorldDropActivation.ResolvePath([path]);

        Assert.Equal(path, resolved);
    }

    [Fact]
    public void RejectsMultipleFilesAndNonPortableFiles()
    {
        Assert.Null(PortableWorldDropActivation.ResolvePath(
            ["one.safeworld", "two.safeworld"]));
        Assert.Null(PortableWorldDropActivation.ResolvePath(["notes.txt"]));
    }

    [Fact]
    public void WindowDropResolutionRequiresOneExistingPortableWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "safe-world-drop-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portablePath = Path.Combine(root, "500h-megabase.safeworld");
            File.WriteAllBytes(portablePath, [1, 2, 3]);
            var data = new DataObject(DataFormats.FileDrop, new[] { portablePath });

            var accepted = MainWindow.TryResolveDroppedPortableWorld(data, out var resolved);

            Assert.True(accepted);
            Assert.Equal(Path.GetFullPath(portablePath), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowDropResolutionRejectsMissingPortableWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var missing = Path.Combine(
            Path.GetTempPath(),
            $"missing-{Guid.NewGuid():N}.safeworld");
        var data = new DataObject(DataFormats.FileDrop, new[] { missing });

        Assert.False(MainWindow.TryResolveDroppedPortableWorld(data, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void WindowDropResolutionRejectsMultipleFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var data = new DataObject(
            DataFormats.FileDrop,
            new[] { "one.safeworld", "two.safeworld" });

        Assert.False(MainWindow.TryResolveDroppedPortableWorld(data, out var resolved));
        Assert.Equal(string.Empty, resolved);
    }
}
