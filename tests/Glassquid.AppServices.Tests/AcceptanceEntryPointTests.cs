using Glassquid.App.Startup;
using Glassquid.Core.Storage;
using Glassquid.Platform.Storage;

namespace Glassquid.AppServices.Tests;

public sealed class AcceptanceEntryPointTests
{
    [Theory]
    [InlineData("--phase3-acceptance", "--exit")]
    [InlineData("--phase4-acceptance", "--diagnose")]
    [InlineData("--phase5-acceptance", "--show-settings")]
    [InlineData("--phase6-acceptance", "--show-launcher")]
    [InlineData("--phase6-acceptance", "--sample-light")]
    [InlineData("--phase5-acceptance", "--help")]
    public void PhaseAcceptance_ConflictsWithOtherRecognizedMode_DoesNotRunSilently(string phase, string otherMode)
    {
        var options = CommandLineOptions.Parse([phase, "report", "data", otherMode]);
        Assert.NotEmpty(options.Unknown);
    }

    [Fact]
    public void CompleteStandaloneDiagnoseArgs_AllowsIsolatedDataAndReportEntryPoint()
    {
        var options = CommandLineOptions.Parse(["--diagnose", "--acceptance-data-root", "new-data", "--acceptance-report", "new-report"]);
        Assert.True(AcceptanceEntryPoint.IsValidIsolatedRun(options));
    }

    [Theory]
    [InlineData("--phase3-acceptance")]
    [InlineData("--phase4-acceptance")]
    [InlineData("--phase5-acceptance")]
    [InlineData("--phase6-acceptance")]
    public void PhaseEntryPoint_RejectsOverlappingReport_WithoutLoadingOrHealingProtectedRoot(string phase)
    {
        var parent = Path.Combine(Path.GetTempPath(), "Glassquid-EntryTests-" + Guid.NewGuid().ToString("N"));
        var realPaths = new AppPaths(Path.Combine(parent, "protected"));
        realPaths.EnsureCreated();
        File.WriteAllText(realPaths.SettingsFile, "{ corrupt sentinel");
        var before = Directory.GetFiles(realPaths.RootDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var newData = Path.Combine(parent, "data");
        var report = Path.Combine(parent, "safe-report.txt");
        var args = phase == "--phase3-acceptance"
            ? new[] { phase, realPaths.SettingsFile, report, newData }
            : [phase, realPaths.SettingsFile, newData];
        try
        {
            // Chỉ đi qua cổng kiểm tra đường dẫn bị từ chối, không đi vào phần thân nghiệm thu của bất kỳ giai đoạn nào.
            Assert.Equal(2, AppServiceRunner.Run(realPaths, CommandLineOptions.Parse(args)));
            Assert.Equal("{ corrupt sentinel", File.ReadAllText(realPaths.SettingsFile));
            Assert.Equal(before, Directory.GetFiles(realPaths.RootDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
            Assert.False(Directory.Exists(newData));
            Assert.False(File.Exists(report));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Theory]
    [InlineData("--phase5-acceptance")]
    [InlineData("--phase6-acceptance")]
    public void PhaseEntryPoint_RejectsOverlappingData_WithoutCreatingDataOrReport(string phase)
    {
        var parent = Path.Combine(Path.GetTempPath(), "Glassquid-EntryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var realPaths = new AppPaths(Path.Combine(parent, "protected"));
            var report = Path.Combine(parent, "report.txt");
            var options = CommandLineOptions.Parse([phase, report, Path.Combine(realPaths.RootDirectory, "test")]);
            Assert.Equal(2, AppServiceRunner.Run(realPaths, options));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void ActualReportStreamWriteFailure_ReturnsFailureFromEntryPoint()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Glassquid-EntryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var result = AcceptanceEntryPoint.Run(new AppPaths(Path.Combine(parent, "protected")), Path.Combine(parent, "data"),
                [new AcceptanceOutput("report", Path.Combine(parent, "report.txt"))], workspace =>
                {
                    var report = workspace.GetOutput("report");
                    report.Dispose();
                    AcceptanceEntryPoint.WriteText(report, "must fail");
                    return 0;
                });
            Assert.Equal(1, result);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void IsolatedSelftest_RejectsMixedPhaseAcceptanceCommand()
    {
        var options = CommandLineOptions.Parse([
            "--selftest", "--phase3-acceptance", "image.png", "report.txt", "data-root",
            "--acceptance-data-root", "selftest-root", "--acceptance-report", "selftest.txt",
        ]);

        Assert.False(AcceptanceEntryPoint.IsValidIsolatedRun(options));
    }

    [Fact]
    public void Phase3CrossDirectoryRestoreTarget_MustBeAllocatedByWorkspaceWithoutRecursivePathDelete()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Glassquid.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);

        var source = File.ReadAllText(Path.Combine(repository!.FullName, "src", "Glassquid.App", "Startup", "Phase3Acceptance.cs"));
        Assert.Contains("workspace.CreateChildDataRoot(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(newHome.RootDirectory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredReportWriteException_MapsToNonZeroResult()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Glassquid-EntryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var result = AcceptanceEntryPoint.Run(
                new AppPaths(Path.Combine(parent, "protected")),
                Path.Combine(parent, "data"),
                [new AcceptanceOutput("report", Path.Combine(parent, "report.txt"))],
                _ => throw new IOException("injected required report write failure"));

            Assert.Equal(1, result);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void OverlappingCandidate_RejectsBeforeAcceptanceCallback_WithoutCreatingProtectedRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Glassquid-EntryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var protectedRoot = Path.Combine(parent, "protected");
            var candidate = Path.Combine(protectedRoot, "acceptance");
            var invoked = false;

            var result = AcceptanceEntryPoint.Run(
                new AppPaths(protectedRoot),
                candidate,
                [new AcceptanceOutput("report", Path.Combine(parent, "report.txt"))],
                _ => { invoked = true; return 0; });

            Assert.Equal(2, result);
            Assert.False(invoked);
            Assert.False(Directory.Exists(protectedRoot));
            Assert.False(File.Exists(Path.Combine(parent, "report.txt")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
