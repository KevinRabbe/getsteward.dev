using System.IO;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal Task OpenPortableWorldFromPathAsync(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        return RunUnifiedOperationAsync(
            $"Opening {Path.GetFileName(fullPath)}...",
            () => ImportPortableWorldFileAsync(fullPath));
    }
}
