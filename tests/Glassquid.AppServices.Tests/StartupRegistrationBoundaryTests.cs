using Microsoft.Win32;
using Glassquid.Core.Models.Settings;
using Glassquid.Core.Storage;
using Glassquid.Platform.Startup;

namespace Glassquid.AppServices.Tests;

public sealed class StartupRegistrationBoundaryTests
{
    [Fact]
    public void RestoreAndResetPageModifyOnlyProductDataAndPreserveTestStartupRegistration()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glassquid-StartupBoundary-" + Guid.NewGuid().ToString("N"));
        var registryPath = @"Software\Glassquid.Tests\StartupBoundary\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);

        try
        {
            var executablePath = Path.Combine(root, "Glassquid Tiếng Việt Test.exe");
            File.WriteAllText(executablePath, "test executable placeholder");
            using var runKey = Registry.CurrentUser.CreateSubKey(registryPath, writable: true)!;
            const string valueName = "Glassquid.Tests.StartupBoundary";
            var registration = new StartupRegistration(runKey, valueName);
            var enabled = registration.SetEnabled(executablePath, enabled: true);
            Assert.True(enabled.Succeeded, enabled.Error);
            var ownedCommand = runKey.GetValue(valueName);

            var data = new GlassquidDataStore(new AppPaths(Path.Combine(root, "data")));
            data.LoadAll();
            data.Settings.Current.Appearance.AccentColor = "#123456";
            data.Settings.Save();

            var reset = SettingsPageEditor.ResetPage(data.Settings.Current, "Giao diện");
            SettingsPageEditor.Apply(data.Settings.Current, reset);
            data.Settings.Save();

            var archive = Path.Combine(root, "external archive");
            Assert.True(data.Backup.BackupTo(archive).Succeeded);
            Assert.True(data.Backup.RestoreFrom(archive).Succeeded);

            Assert.Equal(ownedCommand, runKey.GetValue(valueName));
            Assert.Equal(StartupRegistrationState.Owned, registration.Query(executablePath).State);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
