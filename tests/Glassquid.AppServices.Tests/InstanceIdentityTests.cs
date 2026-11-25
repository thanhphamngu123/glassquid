using Glassquid.Platform;

namespace Glassquid.AppServices.Tests;

public sealed class InstanceIdentityTests
{
    [Fact]
    public void SameUserSessionGetsStableMutexAndPipeNameWhileDifferentUsersOrSessionsAreIsolated()
    {
        var owner = new InstanceIdentity("S-1-5-21-1001", 42);
        var sameSession = new InstanceIdentity("S-1-5-21-1001", 42);
        var otherSession = new InstanceIdentity("S-1-5-21-1001", 43);
        var otherUser = new InstanceIdentity("S-1-5-21-1002", 42);

        Assert.Equal(owner.GetMutexName("Glassquid.Host"), sameSession.GetMutexName("Glassquid.Host"));
        Assert.Equal(owner.GetPipeName("Glassquid.Host"), sameSession.GetPipeName("Glassquid.Host"));
        Assert.StartsWith(@"Local\", owner.GetMutexName("Glassquid.Host"), StringComparison.Ordinal);
        Assert.NotEqual(owner.GetMutexName("Glassquid.Host"), otherSession.GetMutexName("Glassquid.Host"));
        Assert.NotEqual(owner.GetPipeName("Glassquid.Host"), otherSession.GetPipeName("Glassquid.Host"));
        Assert.NotEqual(owner.GetMutexName("Glassquid.Host"), otherUser.GetMutexName("Glassquid.Host"));
        Assert.NotEqual(owner.GetPipeName("Glassquid.Host"), otherUser.GetPipeName("Glassquid.Host"));
    }

    [Fact]
    public async Task DuplicateInstanceInCurrentUserSessionReceivesCommandsOverPrivatePipe()
    {
        var name = "pipe-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.Acquire(name);
        using var second = SingleInstanceGuard.Acquire(name);
        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CommandReceived += (_, command) => received.TrySetResult(command);
        first.StartListening();

        Assert.True(await SingleInstanceGuard.TrySendCommandAsync(name, "show-launcher", TimeSpan.FromSeconds(3)));
        Assert.Equal("show-launcher", await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
