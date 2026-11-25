using System.Globalization;
using System.IO;
using System.Text;
using Glassquid.Platform.Shell;

namespace Glassquid.App.Startup;

/// <summary>Một nguồn mẫu.</summary>
/// <param name="Key">Định danh tiếng Anh ổn định để các câu khẳng định (assertion) tìm thấy theo loại.</param>
/// <param name="Path">Đường dẫn nguồn hoặc URL.</param>
/// <param name="Description">Mô tả tiếng Việt, ghi vào báo cáo cho người dùng xem.</param>
internal sealed record SampleSource(string Key, string Path, string Description);

/// <summary>Danh sách mẫu được tạo ra.</summary>
/// <param name="Directory">Thư mục gốc chứa mẫu.</param>
/// <param name="ShouldImport">Các nguồn cần được nhập thành công.</param>
/// <param name="ShouldFail">Các nguồn cần bị từ chối từng mục một.</param>
/// <param name="BrokenTarget">Tệp có "đích bị xóa", dùng để kiểm chứng việc giữ lại mục hỏng.</param>
/// <param name="RelocationSource">Nguồn của mục hỏng, dùng để tìm lại nó khi định vị lại.</param>
internal sealed record SampleSet(
    string Directory,
    IReadOnlyList<SampleSource> ShouldImport,
    IReadOnlyList<SampleSource> ShouldFail,
    string BrokenTarget,
    string RelocationSource);

/// <summary>
/// Tạo các mẫu đại diện cần thiết cho nghiệm thu giai đoạn 3.
/// </summary>
/// <remarks>
/// Bài học từ giai đoạn 2 là "đầu vào biên mới phát sinh lỗi", chỉ kiểm thử đường dẫn sạch
/// thì coi như chưa kiểm thử. Vì vậy ở đây cố tình tạo đủ các trường hợp: tiếng Việt có dấu,
/// khoảng trắng, đường dẫn hỏng, lối tắt có tham số, lối tắt trỏ tới URL, cũng như những thứ
/// hoàn toàn không nên được chấp nhận, đồng thời để chính tên thư mục cũng chứa khoảng trắng
/// và tiếng Việt có dấu — đặt mẫu vào một thư mục tạm thuần ASCII sẽ vô tình né mất đúng đoạn
/// dễ phát sinh lỗi nhất.
///
/// <para>
/// Mỗi mẫu mang một <c>Key</c> tiếng Anh: câu khẳng định tìm theo Key, còn báo cáo hiển thị mô tả tiếng Việt.
/// Nhờ vậy logic nghiệm thu (trong C#) không cần khớp văn bản tiếng Việt, trong khi bản thân mẫu vẫn giữ tiếng Việt.
/// </para>
/// </remarks>
internal static class SampleDataBuilder
{
    /// <summary>Tạo trọn bộ mẫu trong thư mục làm việc.</summary>
    public static SampleSet Build(string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(Path.GetTempPath(), "Glassquid-Mẫu Thử");

        // Mỗi loại mẫu đặt trong một thư mục con riêng, nhờ vậy việc "xóa đích" chỉ ảnh hưởng đến đúng mục đó.
        var programs = Path.Combine(root, "Thư mục Chương trình");
        var documents = Path.Combine(root, "Thư mục Tài liệu");
        var shortcuts = Path.Combine(root, "Thư mục Lối tắt");
        var broken = Path.Combine(root, "Thư mục Hỏng");

        foreach (var directory in new[] { root, programs, documents, shortcuts, broken })
        {
            Directory.CreateDirectory(directory);
        }

        var importable = new List<SampleSource>();
        var failable = new List<SampleSource>();

        // ---- 1. Chương trình: tập lệnh thực thi, tên có tiếng Việt và khoảng trắng ----
        var program = Path.Combine(programs, "Trình mô phỏng Ghi chú.cmd");
        File.WriteAllText(
            program,
            "@echo off" + Environment.NewLine + "echo Đây là mẫu chương trình dùng để nghiệm thu." + Environment.NewLine,
            new UTF8Encoding(false));

        importable.Add(new SampleSource("program", program, "Chương trình (đường dẫn có tiếng Việt và khoảng trắng)"));

        // ---- 2. Thư mục: tên tiếng Việt ----
        var folder = Path.Combine(documents, "Thư mục Tài liệu của tôi");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Tệp bên trong.txt"), "Nội dung", new UTF8Encoding(false));
        importable.Add(new SampleSource("folder", folder, "Thư mục (đường dẫn có tiếng Việt và khoảng trắng)"));

        // ---- 3. Tệp: tài liệu tên tiếng Việt ----
        var document = Path.Combine(documents, "Báo cáo Quý 2026.txt");
        File.WriteAllText(document, "Nội dung mẫu", new UTF8Encoding(false));
        importable.Add(new SampleSource("file", document, "Tệp (đường dẫn có tiếng Việt và khoảng trắng)"));

        // ---- 4. Lối tắt: có tham số, tên tiếng Việt, biểu tượng trỏ tới chỉ mục của shell32 ----
        var withArguments = Path.Combine(shortcuts, "Lối tắt có Tham số.lnk");

        if (ShortcutWriter.Write(withArguments, new ShortcutDefinition(
                program,
                Arguments: "--flag \"giá trị có khoảng trắng\" --second",
                WorkingDirectory: programs,
                IconLocation: @"C:\Windows\System32\shell32.dll,3",
                Description: "Mẫu nghiệm thu giai đoạn 3: phải giữ lại cả tham số và vị trí biểu tượng tùy chỉnh")))
        {
            importable.Add(new SampleSource("shortcut-arguments", withArguments, "Lối tắt (có tham số, tiếng Việt, chỉ mục biểu tượng tùy chỉnh)"));
        }
        else
        {
            failable.Add(new SampleSource("shortcut-arguments", withArguments, "Máy này không thể tạo mẫu lối tắt"));
        }

        // ---- 5. Lối tắt: trỏ tới trang web ----
        var webShortcut = Path.Combine(shortcuts, "Lối tắt Trang web.lnk");

        if (ShortcutWriter.Write(webShortcut, new ShortcutDefinition("https://example.com/đường dẫn có khoảng trắng")))
        {
            importable.Add(new SampleSource("shortcut-url", webShortcut, "Lối tắt (trỏ tới trang web)"));
        }

        // ---- 6. Lối tắt Internet: tệp văn bản thuần .url ----
        var internetShortcut = Path.Combine(shortcuts, "Lối tắt Internet.url");
        File.WriteAllText(
            internetShortcut,
            "[InternetShortcut]" + Environment.NewLine + "URL=https://example.org/a b" + Environment.NewLine,
            new UTF8Encoding(false));

        importable.Add(new SampleSource("url-file", internetShortcut, "Trang web (tệp .url)"));

        // ---- 7. URL: truyền trực tiếp, chỉ mục này được đánh dấu là IsUrl ----
        // Bên gọi tự quyết định nguồn nào được xử lý như URL; ở đây chỉ ghi nhận dạng đường dẫn.

        // Hai địa chỉ chỉ khác nhau ở cổng: khóa khử trùng phải tính cả cổng, nếu không địa chỉ thứ hai sẽ bị từ chối vì coi là trùng lặp.
        // Khi rà soát lại phát hiện khóa chỉ gộp giao thức và máy chủ mà làm rơi mất cổng.
        importable.Add(new SampleSource("url-port-8001", "http://localhost:8001/app", "Trang web (cổng 8001)"));
        importable.Add(new SampleSource("url-port-8002", "http://localhost:8002/app", "Trang web (cổng 8002)"));

        // ---- 8. Đường dẫn hỏng: tạo đích trước, sau khi nhập thì bên gọi xóa đi ----
        var doomedTarget = Path.Combine(broken, "Chương trình sắp bị xóa.cmd");
        File.WriteAllText(doomedTarget, "@echo off" + Environment.NewLine, new UTF8Encoding(false));

        var doomedShortcut = Path.Combine(broken, "Lối tắt trỏ tới đích sắp hỏng.lnk");

        if (ShortcutWriter.Write(doomedShortcut, new ShortcutDefinition(doomedTarget)))
        {
            importable.Add(new SampleSource("broken-later", doomedShortcut, "Lối tắt (đích sẽ bị xóa sau đó, dùng để kiểm chứng giữ lại mục hỏng)"));
        }

        // ---- 9. Đuôi là .url nhưng nội dung không phải trang web — nên lùi về xử lý như tệp thường, chứ không thất bại ----
        var bogusUrl = Path.Combine(shortcuts, "Tệp nội dung không đầy đủ.url");
        File.WriteAllText(bogusUrl, "Đây không phải là một lối tắt Internet" + Environment.NewLine, new UTF8Encoding(false));
        importable.Add(new SampleSource("bogus-url-file", bogusUrl, "Tệp thông thường (đuôi .url nhưng nội dung không phải)"));

        // ---- 10. Các nguồn phải bị từ chối từng mục ----
        failable.Add(new SampleSource("missing-path", Path.Combine(root, "Đường dẫn hoàn toàn không tồn tại.txt"), "Đường dẫn không tồn tại"));

        // Một tệp .lnk 0 byte: cả trường đường dẫn lẫn danh sách ID đều không đọc được, phải báo lỗi từng mục.
        var emptyShortcut = Path.Combine(shortcuts, "Lối tắt rỗng.lnk");
        File.WriteAllBytes(emptyShortcut, []);
        failable.Add(new SampleSource("empty-shortcut", emptyShortcut, "Tệp rỗng giả làm lối tắt"));

        return new SampleSet(root, importable, failable, doomedTarget, doomedShortcut);
    }

    /// <summary>Ghi lại cấu trúc thư mục mẫu vào báo cáo để tiện đối chiếu thủ công.</summary>
    public static string Describe(SampleSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Thư mục gốc mẫu: {set.Directory}");
        report.AppendLine();

        report.AppendLine("Nên nhập thành công:");

        foreach (var source in set.ShouldImport)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{source.Key}] {source.Description}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {source.Path}");
        }

        report.AppendLine();
        report.AppendLine("Nên bị từ chối từng mục:");

        foreach (var source in set.ShouldFail)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{source.Key}] {source.Description}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {source.Path}");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Đích sẽ bị xóa sau đó: {set.BrokenTarget}");

        return report.ToString();
    }

    /// <summary>Xóa một đích mẫu để tạo tình huống "mục vẫn còn, đích đã mất hiệu lực".</summary>
    public static bool DeleteTarget(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không xóa được thì coi như chưa thành công, để bên gọi báo cáo đúng sự thật.
        }

        return false;
    }
}