using System.Windows;
using System.Windows.Controls;
using Glassquid.Dock;

namespace Glassquid.Dock.Tests;

/// <summary>
/// Khẳng định về chính sách bố cục và tập ứng viên sắp xếp — tương ứng lỗi 1 trong báo cáo rà soát:
/// việc sắp xếp đã ghi cả các mục tích hợp vào danh sách ứng dụng.
/// </summary>
public class DockLayoutPolicyTests
{
    private static readonly HashSet<string> Library = ["app-a", "app-b", "app-c", "app-d", "app-e"];

    [Fact]
    public void BuiltInEntriesAreNotWrittenToLayout()
    {
        // Tái hiện lỗi: việc sắp xếp ghi nguyên cả hàng ô (gồm 3 mục tích hợp) trở lại bố cục, khiến entry:* bị lẫn vào.
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-a", "entry:launcher", "app-b", "entry:showdesktop", "entry:recyclebin"],
            Library.Contains);

        Assert.Equal(["app-a", "app-b"], sanitized);
    }

    [Fact]
    public void PreservesOriginalAppOrderAndDeduplicates()
    {
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-c", "app-a", "app-c", "app-b", "app-a"],
            Library.Contains);

        Assert.Equal(["app-c", "app-a", "app-b"], sanitized);
    }

    [Fact]
    public void RemovesIdsMissingFromLibrary()
    {
        // Mục mà người dùng đã xóa khỏi thư viện ứng dụng hoặc id lạ được chỉnh sửa thủ công.
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-a", "id-muc-da-bi-xoa", "app-b"],
            Library.Contains);

        Assert.Equal(["app-a", "app-b"], sanitized);
    }

    [Fact]
    public void ReturnsEmptyListWhenOnlyEntriesExist()
    {
        var sanitized = DockLayoutPolicy.Sanitize(
            ["entry:launcher", "entry:showdesktop", "entry:recyclebin"],
            Library.Contains);

        Assert.Empty(sanitized);
    }

    [Fact]
    public void RecognizesEntryTilePrefix()
    {
        Assert.True(DockLayoutPolicy.IsEntryTile("entry:launcher"));
        Assert.True(DockLayoutPolicy.IsEntryTile("entry:recyclebin"));
        Assert.False(DockLayoutPolicy.IsEntryTile("entry"));
        Assert.False(DockLayoutPolicy.IsEntryTile("id-muc-thong-thuong"));
        Assert.False(DockLayoutPolicy.IsEntryTile(null));
    }

    [Fact]
    public void OrderComparisonIsOrderSensitive()
    {
        Assert.True(DockLayoutPolicy.SameOrder(["a", "b"], ["a", "b"]));
        Assert.False(DockLayoutPolicy.SameOrder(["a", "b"], ["b", "a"]));
        Assert.False(DockLayoutPolicy.SameOrder(["a"], ["a", "b"]));
        Assert.True(DockLayoutPolicy.SameOrder([], []));
    }
}

/// <summary>
/// Khẳng định về tập ứng viên sắp xếp: trong hàng ô chỉ có các ô ứng dụng tham gia sắp xếp.
/// </summary>
public class DockTileStripReorderScopeTests
{
    private static DockTileModel App(string id) => new() { EntryId = id, DisplayName = id };

    private static DockTileModel Entry(string id) => new() { EntryId = id, DisplayName = id };

    [Fact]
    public void ReorderCandidatesIncludeOnlyAppTiles()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([
                App("app-a"), App("app-b"), App("app-c"),
                Entry("entry:launcher"), Entry("entry:showdesktop"), Entry("entry:recyclebin"),
            ]);

            Assert.Equal(["app-a", "app-b", "app-c"], strip.ReorderCandidateIds);
        });
    }

    [Fact]
    public void ReorderCandidatesAreEmptyWithoutAppTiles()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([Entry("entry:launcher"), Entry("entry:showdesktop"), Entry("entry:recyclebin")]);

            Assert.Empty(strip.ReorderCandidateIds);
        });
    }

    [Fact]
    public void AllAppTilesParticipateInReorderWhenNoEntriesExist()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([App("app-a"), App("app-b")]);

            Assert.Equal(["app-a", "app-b"], strip.ReorderCandidateIds);
        });
    }
}

/// <summary>
/// Phân giải lượt nhấp trong danh sách nhiều cửa sổ — tương ứng lỗi 2 trong báo cáo rà soát:
/// chỉ đọc <c>OriginalSource.Tag</c> sẽ không lấy được gì.
/// </summary>
public class WindowChooserHitTests
{
    [Fact]
    public void ResolvesItemHandleWhenClickingOnText()
    {
        StaTest.Run(() =>
        {
            var list = new ListBox();
            var text = new TextBlock { Text = "Cửa sổ 1" };
            var handle = new nint(0x1234);

            var item = new ListBoxItem { Content = text, Tag = handle };
            list.Items.Add(item);

            // Đưa vào cửa sổ thật và chạy một lượt bố cục để container được tạo (ContainerFromElement phụ thuộc vào bước này).
            var host = new Window
            {
                Width = 200,
                Height = 120,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = list,
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                // Tiền đề kiểm thử: nguồn nhấp (chữ bên trong) tự nó không có Tag —
                // đây chính là lý do cách viết cũ “chỉ đọc OriginalSource.Tag” không lấy được handle.
                Assert.Null(text.Tag);

                Assert.Equal(handle, WindowChooserHit.Resolve(list, text));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void ResolvesHandleForClickedRow()
    {
        StaTest.Run(() =>
        {
            var list = new ListBox();
            var firstText = new TextBlock { Text = "Cửa sổ 1" };
            var secondText = new TextBlock { Text = "Cửa sổ 2" };
            var secondHandle = new nint(0x5678);

            list.Items.Add(new ListBoxItem { Content = firstText, Tag = new nint(0x1234) });
            list.Items.Add(new ListBoxItem { Content = secondText, Tag = secondHandle });

            var host = new Window
            {
                Width = 200,
                Height = 120,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = list,
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                Assert.Equal(secondHandle, WindowChooserHit.Resolve(list, secondText));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void ReturnsZeroInsteadOfThrowingWhenNotFound()
    {
        // Phần tử WPF phải được tạo trên luồng STA (xunit mặc định chạy kiểm thử trên thread pool).
        StaTest.Run(() =>
        {
            var list = new ListBox();

            Assert.Equal(0, WindowChooserHit.Resolve(list, null));
            Assert.Equal(0, WindowChooserHit.Resolve(list, "Không phải phần tử trực quan"));
            Assert.Equal(0, WindowChooserHit.Resolve(list, new TextBlock()));
        });
    }
}