using System.Reflection;
using SharedWorlds.Core.Abstractions;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class GameAdapterRecoveryCapabilityAuditTests
{
    [Fact]
    public void EveryLaunchCapableAdapterDeclaresRecoveryIdentityBeforeMaterialization()
    {
        var launchCapabilities =
            GameAdapterCapabilities.AutomaticLocalLaunch |
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.AutomaticClientJoin;

        var adapterTypes = Directory
            .EnumerateFiles(
                AppContext.BaseDirectory,
                "SharedWorlds.GameAdapters.*.dll",
                SearchOption.TopDirectoryOnly)
            .Select(Assembly.LoadFrom)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                type is { IsAbstract: false, IsInterface: false } &&
                type.IsPublic &&
                typeof(IGameAdapter).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) is not null)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(adapterTypes);

        var violations = new List<string>();
        foreach (var adapterType in adapterTypes)
        {
            var adapter = (IGameAdapter)Activator.CreateInstance(adapterType)!;
            if ((adapter.Capabilities & launchCapabilities) == 0)
            {
                continue;
            }

            if (!typeof(IPreparedWorldRecoveryPlanner).IsAssignableFrom(adapterType))
            {
                violations.Add(
                    $"{adapter.Id} ({adapterType.FullName}) advertises a writable launch capability without {nameof(IPreparedWorldRecoveryPlanner)}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Launch-capable adapters must declare durable recovery identity before Core materializes writable runtime state. " +
            string.Join(Environment.NewLine, violations));
    }
}
