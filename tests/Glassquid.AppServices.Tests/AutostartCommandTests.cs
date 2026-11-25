using Glassquid.App.Startup;
using Glassquid.Platform;

namespace Glassquid.AppServices.Tests;

public sealed class AutostartCommandTests
{
    [Fact]
    public void Autostart_FirstLaunchKeepsDockAndTrayWithoutOpeningLauncherOrSettings()
    {
        var options = CommandLineOptions.Parse(["--autostart"]);

        Assert.True(options.Autostart);
        Assert.Empty(options.Unknown);
        Assert.False(StartupIntent.ShouldOpenLauncher(options));
        Assert.False(StartupIntent.ShouldOpenSettings(options));
    }

    [Fact]
    public void Autostart_DuplicateInstanceDoesNotSendShowCommandToExistingInstance()
    {
        var options = CommandLineOptions.Parse(["--autostart"]);

        Assert.Null(StartupIntent.GetDuplicateInstanceCommand(options));
    }

    [Fact]
    public void NormalLaunch_OpensSettingsAndDuplicateLaunchActivatesSettings()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.False(StartupIntent.ShouldOpenLauncher(options));
        Assert.True(StartupIntent.ShouldOpenSettings(options));
        Assert.Equal(InstanceCommands.ShowSettings, StartupIntent.GetDuplicateInstanceCommand(options));
    }

    [Theory]
    [InlineData("--show-launcher")]
    [InlineData("--show-settings")]
    [InlineData("--exit")]
    [InlineData("--selftest")]
    [InlineData("--help")]
    [InlineData("--list-library")]
    public void Autostart_CombinedWithOtherLaunchModesProducesArgumentError(string conflictingArgument)
    {
        var options = CommandLineOptions.Parse(["--autostart", conflictingArgument]);

        Assert.True(options.Autostart);
        Assert.NotEmpty(options.Unknown);
    }

    [Fact]
    public void FirstInstance_WhenReceivingExitOnlyExitsWithoutInitializingProductWindows()
    {
        var options = CommandLineOptions.Parse(["--exit"]);

        Assert.True(StartupIntent.ShouldExitWithoutExistingInstance(options));
    }

    [Fact]
    public void LegacyMutexCreatedFirst_ModernCannotAcquireCompatLeaseAndGeneratesBlockMessage()
    {
        var instanceName = "Glassquid.LegacyProbe." + Guid.NewGuid().ToString("N");
        using var legacyMutex = new Mutex(initiallyOwned: false, $@"Local\Glassquid.SingleInstance.{instanceName}");
        using var modernGuard = SingleInstanceGuard.Acquire(instanceName);
        Assert.True(modernGuard.IsFirstInstance);

        Assert.False(modernGuard.TryAcquireLegacyProtocolLease(out var status));
        Assert.Equal(LegacyInstanceStatus.Running, status);
        var message = StartupIntent.GetLegacyInstanceBlockMessage(LegacyInstanceStatus.Running);
        Assert.Contains("phiên bản Glassquid cũ", message, StringComparison.Ordinal);
        Assert.Contains("thoát", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModernFirstInstance_HoldsLegacyProtocolMutexLeaseUntilExit()
    {
        var instanceName = "Glassquid.LegacyLease." + Guid.NewGuid().ToString("N");

        using (var modernGuard = SingleInstanceGuard.Acquire(instanceName))
        {
            Assert.True(modernGuard.IsFirstInstance);
            Assert.True(modernGuard.TryAcquireLegacyProtocolLease(out var status));
            Assert.Equal(LegacyInstanceStatus.NotRunning, status);

            using var legacyAttempt = new Mutex(initiallyOwned: false, $@"Local\Glassquid.SingleInstance.{instanceName}", out var legacyCreatedNew);
            Assert.False(legacyCreatedNew);
        }

        using var afterModernExit = new Mutex(initiallyOwned: false, $@"Local\Glassquid.SingleInstance.{instanceName}", out var createdAfterExit);
        Assert.True(createdAfterExit);
    }
}
