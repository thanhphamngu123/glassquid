using Glassquid.AppServices;
using Glassquid.Core.Models;
using Glassquid.Core.Storage;

namespace Glassquid.AppServices.Tests;

public sealed class SearchServiceTests
{
    [Theory]
    [InlineData("启动台")]
    [InlineData("qidongtai")]
    [InlineData("qdt")]
    public void ChineseName_SearchesByNameFullPinyinAndInitials(string query)
    {
        var item = new AppEntry { Id = "a", DisplayName = "启动台", Target = "unused" };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Equal("a", Assert.Single(search.SearchApps(query, 500)).Item.Id);
    }

    [Fact]
    public void Search_UsesStableRankingAndAliases()
    {
        AppEntry[] entries =
        [
            new() { Id = "exact", DisplayName = "Code", Target = "a" },
            new() { Id = "prefix", DisplayName = "Code Editor", Target = "b" },
            new() { Id = "contains", DisplayName = "My Code", Target = "c" },
            new() { Id = "alias", DisplayName = "Công cụ Trùng Khánh", SearchAliases = ["code", "chongqing", "cq"], Target = "d" },
        ];
        var search = new SearchService(() => entries, () => new LauncherLayout());
        Assert.Equal(["exact", "prefix", "contains", "alias"], search.SearchApps("CODE", 500).Select(x => x.Item.Id));
        Assert.Equal("alias", Assert.Single(search.SearchApps("cq", 500)).Item.Id);
    }

    [Fact]
    public void Search_SeesRenamedEntryWithoutWritingTheLibrary()
    {
        var item = new AppEntry { Id = "a", DisplayName = "Tên cũ", Target = "unused" };
        var search = new SearchService(() => [item], () => new LauncherLayout());
        Assert.Single(search.SearchApps("Tên cũ", 500));
        item.DisplayName = "Tên mới";
        Assert.Empty(search.SearchApps("Tên cũ", 500));
        Assert.Single(search.SearchApps("Tên mới", 500));
    }

    [Fact]
    public void Warmup_PrepopulatesIndexWithoutErrors()
    {
        var entries = new[]
        {
            new AppEntry { Id = "1", DisplayName = "Photoshop", Target = "a" },
            new AppEntry { Id = "2", DisplayName = "微信", Target = "b" }
        };
        var search = new SearchService(() => entries, () => new LauncherLayout());
        search.Warmup();

        // Searching immediately hits precomputed indices
        var results = search.SearchApps("wx", 500);
        Assert.Single(results);
        Assert.Equal("2", results[0].Item.Id);
    }

    [Fact]
    public void Search_WithFormKCNormalization_MatchesEquivalents()
    {
        // Full-width latin characters (e.g. Ｃｏｄｅ) normalize to Code under FormKC
        var item = new AppEntry { Id = "fw", DisplayName = "Ｃｏｄｅ", Target = "path" };
        var search = new SearchService(() => [item], () => new LauncherLayout());

        var results = search.SearchApps("code", 500);
        Assert.Single(results);
        Assert.Equal("fw", results[0].Item.Id);
    }
}

