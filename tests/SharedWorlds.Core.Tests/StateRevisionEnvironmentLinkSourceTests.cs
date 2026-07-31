using System.Text;

namespace SharedWorlds.Core.Tests;

public sealed class StateRevisionEnvironmentLinkSourceTests
{
    [Fact]
    public void EveryProductionStateRevisionConstructionNamesItsEnvironmentRevision()
    {
        var sourceRoot = FindRepositoryDirectory("src");
        var missing = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(path);
            var searchFrom = 0;
            while (true)
            {
                var start = text.IndexOf("new StateRevision(", searchFrom, StringComparison.Ordinal);
                if (start < 0)
                {
                    break;
                }

                var invocation = ReadInvocation(text, start);
                if (!invocation.Contains("EnvironmentRevisionId:", StringComparison.Ordinal))
                {
                    var relative = Path.GetRelativePath(
                        Directory.GetParent(sourceRoot)!.FullName,
                        path);
                    var line = 1 + text.AsSpan(0, start).Count('\n');
                    missing.Add($"{relative}:{line}");
                }

                searchFrom = start + invocation.Length;
            }
        }

        Assert.True(
            missing.Count == 0,
            "Every production StateRevision must name the exact EnvironmentRevision that produced it. Missing:\n" +
            string.Join('\n', missing.Order(StringComparer.Ordinal)));
    }

    private static string ReadInvocation(string text, int start)
    {
        var open = text.IndexOf('(', start);
        Assert.True(open >= 0);

        var depth = 0;
        var inString = false;
        var verbatimString = false;
        var escaped = false;
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (verbatimString)
                {
                    if (character == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            index++;
                        }
                        else
                        {
                            inString = false;
                            verbatimString = false;
                        }
                    }
                }
                else if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                verbatimString = index > 0 && text[index - 1] == '@';
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return text[start..(index + 1)];
            }
        }

        throw new InvalidDataException("Unterminated StateRevision constructor invocation.");
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory '{relativePath}'.");
    }
}
