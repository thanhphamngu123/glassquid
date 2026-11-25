using Glassquid.AppServices;
using Glassquid.Core.Contracts;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices.Tests;

public sealed class ImportRollbackTests
{
    [Fact]
    public void SaveFailure_DoesNotLeaveUnsavedDuplicateInSharedLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glassquid-Import-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new GlassquidDataStore(new AppPaths(root));
            data.LoadAll();
            data.Library.Save();
            var item = Path.Combine(root, "tệp thử nghiệm.txt");
            File.WriteAllText(item, "sample");
            using var host = new AppServiceHost(data);
            using (File.Open(data.Library.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<StorageException>(() => host.Import.ImportItems([new ImportRequest { Source = item }]));
            Assert.Empty(data.Library.Items);
            Assert.Single(host.Import.ImportItems([new ImportRequest { Source = item }]).Added);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
