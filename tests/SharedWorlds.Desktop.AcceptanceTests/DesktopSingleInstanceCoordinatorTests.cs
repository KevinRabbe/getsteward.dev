using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopSingleInstanceCoordinatorTests
{
    [Fact]
    public void SecondDesktopCannotBecomePrimaryForSameUserSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        using var primary = DesktopSingleInstanceCoordinator.Create(
            $"SafeWorld.Test.Primary.{suffix}",
            $"SafeWorld.Test.Activation.{suffix}");
        using var secondary = DesktopSingleInstanceCoordinator.Create(
            $"SafeWorld.Test.Primary.{suffix}",
            $"SafeWorld.Test.Activation.{suffix}");

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
    }

    [Fact]
    public async Task SecondaryForwardsPortableWorldToPrimaryAndReceivesAcknowledgement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var instanceName = $"SafeWorld.Test.Primary.{suffix}";
        var pipeName = $"SafeWorld.Test.Activation.{suffix}";
        using var primary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        using var secondary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        var received = new TaskCompletionSource<DesktopActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(request =>
        {
            received.TrySetResult(request);
            return Task.CompletedTask;
        });
        var portablePath = Path.GetFullPath("500h-megabase.safeworld");

        var forwarded = secondary.TryForward(
            new DesktopActivationRequest(portablePath),
            timeoutMilliseconds: 5000);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(forwarded);
        Assert.Equal(portablePath, request.PortableWorldPath);
    }

    [Fact]
    public async Task SecondaryLaunchWithoutFileCanBringPrimaryForward()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var instanceName = $"SafeWorld.Test.Primary.{suffix}";
        var pipeName = $"SafeWorld.Test.Activation.{suffix}";
        using var primary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        using var secondary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        var received = new TaskCompletionSource<DesktopActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(request =>
        {
            received.TrySetResult(request);
            return Task.CompletedTask;
        });

        var forwarded = secondary.TryForward(
            new DesktopActivationRequest(PortableWorldPath: null),
            timeoutMilliseconds: 5000);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(forwarded);
        Assert.Null(request.PortableWorldPath);
    }

    [Fact]
    public void SecondaryRejectsNonPortableActivationBeforePipeHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var instanceName = $"SafeWorld.Test.Primary.{suffix}";
        var pipeName = $"SafeWorld.Test.Activation.{suffix}";
        using var primary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        using var secondary = DesktopSingleInstanceCoordinator.Create(instanceName, pipeName);
        primary.StartListening(_ => Task.CompletedTask);

        Assert.False(secondary.TryForward(
            new DesktopActivationRequest(Path.GetFullPath("not-a-world.txt")),
            timeoutMilliseconds: 1000));
    }
}
