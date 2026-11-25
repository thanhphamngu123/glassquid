using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Glassquid.Platform.Startup;
using Microsoft.Win32;

namespace Glassquid.AppServices.Tests;

public sealed class FastStartupRegistrationTests : IDisposable
{
    private readonly string _id = "Glassquid.Tests.FastStartup-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Glassquid-FastStartup-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _key;
    private readonly string _executable;

    public FastStartupRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _executable = Path.Combine(_root, "Glassquid Tiếng Việt.exe");
        File.WriteAllText(_executable, "test placeholder, never executed");
        _key = Registry.CurrentUser.CreateSubKey(@"Software\Glassquid.Tests\" + _id)!;
    }

    private StartupRegistration Create() => new(_key, _id, _id);

    [Fact]
    public void EnableRegistersZeroDelayInteractiveLogonTaskWithoutDuplicateRunEntry()
    {
        var service = Create();
        var enabled = service.SetEnabled(_executable, true);
        Assert.True(enabled.Succeeded, enabled.Error + "\n" + ReadTask());
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
        Assert.Null(_key.GetValue(_id));
        var xml = ReadTask();
        XNamespace ns = xml.Root!.Name.Namespace;
        var trigger = xml.Descendants(ns + "LogonTrigger").Single();
        Assert.Contains(trigger.Element(ns + "UserId")!.Value,
            new[] { WindowsIdentity.GetCurrent().User!.Value, WindowsIdentity.GetCurrent().Name });
        Assert.True(trigger.Element(ns + "Delay") is null || trigger.Element(ns + "Delay")!.Value == "PT0S");
        Assert.Equal(_executable, xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--autostart", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(_root, xml.Descendants(ns + "WorkingDirectory").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").SingleOrDefault()?.Value ?? "LeastPrivilege");
        Assert.Equal("6", xml.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "RunOnlyIfIdle").SingleOrDefault()?.Value ?? "false");
        Assert.Equal("false", xml.Descendants(ns + "RunOnlyIfNetworkAvailable").SingleOrDefault()?.Value ?? "false");
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Last().Value);
    }

    [Fact]
    public void MigrateLegacyRunEntryAndDeleteTaskIdempotentlyOnDisable()
    {
        _key.SetValue(_id, $"\"{_executable}\" --autostart");
        var service = Create();
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Null(_key.GetValue(_id));
        Assert.True(service.SetEnabled(_executable, false).Succeeded);
        Assert.True(service.SetEnabled(_executable, false).Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, service.Query(_executable).State);
        Assert.Throws<FileNotFoundException>(() => ReadTask());
    }

    [Fact]
    public void SameNameTaskPointingToOtherCopyIsNotOverwrittenOrDeleted()
    {
        var service = Create();
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        var other = Path.Combine(_root, "Other.exe");
        File.WriteAllText(other, "placeholder");
        Assert.Equal(StartupRegistrationState.OtherTarget, service.Query(other).State);
        Assert.False(service.SetEnabled(other, true).Succeeded);
        Assert.False(service.SetEnabled(other, false).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
    }

    [Fact]
    public void PreservesLegacyRunStartupWhenScheduledTaskIsNotWritable()
    {
        _key.SetValue(_id, $"\"{_executable}\" --autostart");
        var service = new StartupRegistration(_key, _id, _id + "?");
        var result = service.SetEnabled(_executable, true);
        Assert.False(result.Succeeded);
        Assert.Equal($"\"{_executable}\" --autostart", _key.GetValue(_id));
    }

    [Fact]
    public void ExternallyDisabledTaskRemainsOffUntilExplicitlyEnabled()
    {
        var service = Create();
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        WithFolder(folder => { dynamic task = folder.GetTask(_id); try { task.Enabled = false; } finally { Marshal.FinalReleaseComObject(task); } });
        Assert.Equal(StartupRegistrationState.Missing, service.Query(_executable).State);
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
    }

    private XDocument ReadTask()
    {
        XDocument? result = null;
        WithFolder(folder => { dynamic task = folder.GetTask(_id); try { result = XDocument.Parse((string)task.Xml); } finally { Marshal.FinalReleaseComObject(task); } });
        return result!;
    }

    private static void WithFolder(Action<dynamic> action)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try { scheduler.Connect(); folder = scheduler.GetFolder(@"\"); action(folder); }
        finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(scheduler); }
    }

    public void Dispose()
    {
        WithFolder(folder => { try { folder.DeleteTask(_id, 0); } catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { } });
        _key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Glassquid.Tests\" + _id, false);
        Directory.Delete(_root, true);
    }
}
