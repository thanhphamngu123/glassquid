using System.Text;
using Glassquid.App.Startup;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices.Tests;

public sealed class SelfTestReportTests
{
    [Fact]
    public void IsolatedSelfTestReportNoLongerClaimsToWriteLogFileInDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glassquid-SelfTestReport-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        using var output = new MemoryStream();

        try
        {
            SelfTest.Run(paths, output);

            var report = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("Báo cáo tự kiểm tra: nội dung được ghi vào luồng báo cáo do bên gọi chỉ định.", report);
            Assert.DoesNotContain(Path.Combine(root, "logs", "selftest.txt"), report);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
