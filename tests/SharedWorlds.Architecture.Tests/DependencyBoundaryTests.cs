using System.Xml.Linq;

namespace SharedWorlds.Architecture.Tests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void Core_DoesNotReferenceConcreteProjects()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreProject = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.Core",
            "SharedWorlds.Core.csproj");

        Assert.Empty(ReadProjectReferences(coreProject));
    }

    [Fact]
    public void Infrastructure_ReferencesOnlyCore()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreProject = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.Core",
            "SharedWorlds.Core.csproj");
        var infrastructureProject = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.Infrastructure",
            "SharedWorlds.Infrastructure.csproj");

        AssertReferencesOnly(infrastructureProject, coreProject);
    }

    [Fact]
    public void EachGameAdapter_ReferencesOnlyCore()
    {
        var repositoryRoot = FindRepositoryRoot();
        var coreProject = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.Core",
            "SharedWorlds.Core.csproj");
        var adaptersRoot = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.GameAdapters");
        var adapterProjects = Directory
            .EnumerateFiles(adaptersRoot, "*.csproj", SearchOption.AllDirectories)
            .ToArray();

        Assert.NotEmpty(adapterProjects);

        foreach (var adapterProject in adapterProjects)
        {
            AssertReferencesOnly(adapterProject, coreProject);
        }
    }

    private static void AssertReferencesOnly(
        string projectPath,
        params string[] allowedProjects)
    {
        var actual = ReadProjectReferences(projectPath);
        var expected = allowedProjects
            .Select(Path.GetFullPath)
            .OrderBy(path => path, PathComparer())
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static string[] ReadProjectReferences(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException(
                $"Cannot resolve project directory for '{projectPath}'.");

        return document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include!)))
            .OrderBy(path => path, PathComparer())
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SharedWorlds.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repository root from the test output directory.");
    }

    private static StringComparer PathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
