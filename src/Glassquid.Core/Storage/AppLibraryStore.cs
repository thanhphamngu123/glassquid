using Glassquid.Core.Models;
using Glassquid.Core.Serialization;

namespace Glassquid.Core.Storage;

/// <summary>Kết quả khi thêm một mục vào thư viện ứng dụng.</summary>
public enum AddEntryResult
{
    /// <summary>Đã tạo bản ghi mới.</summary>
    Added = 0,

    /// <summary>Đã tồn tại mục có cùng đích và tham số.</summary>
    Duplicate = 1,

    /// <summary>Mục thiếu các trường tối thiểu nên đã bị từ chối.</summary>
    Rejected = 2,
}

/// <summary>
/// Nắm giữ <c>apps.json</c> — kho thư viện ứng dụng duy nhất dùng chung cho Dock và Launchpad.
/// </summary>
/// <remarks>
/// Bên cạnh danh sách có duy trì một chỉ mục theo id để việc tra cứu mục ghim trên Dock và ô Launchpad
/// không bị chậm đi khi thư viện ứng dụng lớn dần. Chỉ mục được dựng lại mỗi lần tải và được các phương thức thay đổi
/// đồng bộ bảo trì; ngoài ra không nên truy cập trực tiếp <see cref="Items"/>.
/// </remarks>
public sealed class AppLibraryStore
{
    private readonly JsonFileStore<AppLibraryData> _store;
    private readonly Dictionary<string, AppEntry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AppEntry> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// "Đối tượng mục → khóa khử trùng lặp gần nhất của nó".
    /// </summary>
    /// <remarks>
    /// Phải tra cứu theo <strong>tham chiếu đối tượng</strong> chứ không theo nội dung, lý do xem <see cref="Update"/>:
    /// đối tượng mục sẽ bị bên gọi sửa tại chỗ trước rồi mới đưa vào phương thức cập nhật. Tra theo nội dung sẽ không tìm được khóa cũ,
    /// và nếu không tìm lại được khóa cũ thì nó sẽ nằm mãi trong chỉ mục.
    /// </remarks>
    private readonly Dictionary<AppEntry, string> _keyByEntry = new(ReferenceEqualityComparer.Instance);

    public AppLibraryStore(string path, TimeProvider? timeProvider = null)
    {
        _store = new JsonFileStore<AppLibraryData>(
            path,
            GlassquidJsonContext.Default.AppLibraryData,
            timeProvider);
    }

    public string FilePath => _store.Path;

    public long Version { get; private set; }

    /// <summary>Nội dung hiện tại, sắp xếp theo thứ tự chèn.</summary>
    /// <remarks>
    /// Có thể chứa phần tử null — thường có trong tệp bị chỉnh sửa thủ công — bên gọi cần tự kiểm tra null trước khi duyệt.
    /// Những mục như vậy vừa không thể sử dụng, vừa không được âm thầm bỏ đi khi lưu.
    /// </remarks>
    public IReadOnlyList<AppEntry> Items => Data.Items;

    /// <summary>
    /// Số lượng mục bất thường đã bị bỏ qua trong lần tải gần nhất: phần tử null, hoặc bản ghi thiếu trường bắt buộc nên không thể dùng.
    /// </summary>
    /// <remarks>
    /// Báo ra là để người dùng biết trong tệp có phần bị hỏng, thay vì để nó biến mất âm thầm.
    /// Các bản ghi này vẫn giữ nguyên trong tài liệu và sẽ được ghi lại nguyên trạng khi lưu, để giai đoạn 3 hiển thị lên cho việc sửa chữa.
    /// </remarks>
    public int AnomalousEntryCount { get; private set; }

    /// <summary>
    /// Số lượng tham chiếu biểu tượng được quản lý đã được sửa lại trỏ về thư mục dữ liệu hiện tại trong lần tải gần nhất.
    /// </summary>
    /// <remarks>
    /// Tự phục hồi là đang sửa dữ liệu người dùng nên phải hiển thị rõ ràng chứ không diễn ra âm thầm; được
    /// <see cref="GlassquidDataStore.LoadAll"/> chuyển thành một thông báo.
    /// </remarks>
    public int RepairedIconReferenceCount { get; private set; }

    private AppLibraryData Data { get; set; } = new();

    public LoadOutcome<AppLibraryData> Load()
    {
        var outcome = _store.Load(static () => new AppLibraryData());

        Data = outcome.Value;
        Data.Items ??= [];
        RebuildIndexes();
        RepairManagedIconReferences();
        Version++;

        return outcome;
    }

    /// <summary>Lưu bền vững thư viện ứng dụng theo cách nguyên tử.</summary>
    /// <exception cref="StorageException">Không thể ghi tệp.</exception>
    public void Save() => _store.Save(Data);

    public AppEntry? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return _byId.GetValueOrDefault(id);
    }

        /// <summary>Tìm mục đã có với cùng đích và tham số.</summary>
    public AppEntry? FindByKey(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// Thêm mục, trừ khi đã tồn tại mục tương đương.
    /// </summary>
    /// <remarks>
    /// Không chịu trách nhiệm lưu; bên gọi quyết định thời điểm ghi xuống đĩa, nhờ vậy khi nhập hàng loạt
    /// chỉ ghi tệp một lần thay vì ghi mỗi mục một lần.
    /// </remarks>
    public AddEntryResult Add(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Chỉ cần bên gọi cung cấp đích. id do chúng ta sinh ra; nếu bắt buộc phải có ngay ở đây,
        // mọi mục mới nhập vào đều sẽ bị từ chối.
        if (!entry.HasLaunchTarget)
        {
            return AddEntryResult.Rejected;
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = NewId();
        }

        if (_byId.ContainsKey(entry.Id))
        {
            return AddEntryResult.Duplicate;
        }

        var key = AppEntryKey.Compute(entry);
        if (_byKey.ContainsKey(key))
        {
            return AddEntryResult.Duplicate;
        }

        Data.Items.Add(entry);
        Index(entry, key);
        Version++;
        return AddEntryResult.Added;
    }

    /// <summary>
    /// Xóa mục khỏi thư viện ứng dụng. Bên gọi cũng phải xóa các tham chiếu tới id này
    /// trong từng bố cục — xem <see cref="LayoutStore.PurgeItem"/>.
    /// </summary>
    public bool Remove(string? id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return false;
        }

        Data.Items.Remove(entry);
        _byId.Remove(entry.Id);
        Deindex(entry);
        Version++;
        return true;
    }

    /// <summary>
    /// Thay thế tại chỗ bản ghi đã có, giữ nguyên vị trí và id của nó.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bên gọi thường sửa mục trước rồi mới gọi hàm này</b> — việc định vị lại chính là đổi <c>Target</c>
    /// sang đường dẫn mới trước. Vì vậy không thể đợi vào trong phương thức này mới tính khóa cũ: lúc đó đối tượng đã bị sửa,
    /// khóa tính ra sẽ là khóa mới, khóa bị xóa cũng là khóa mới, còn khóa cũ sẽ nằm mãi trong chỉ mục,
    /// khiến vị trí cũ từ đó bị nhận nhầm là "đã có mục" và làm các lượt nhập hợp lệ bị từ chối.
    /// Khóa cũ được lấy lại từ <see cref="_keyByEntry"/> theo tham chiếu đối tượng.
    /// </para>
    /// <para>
    /// Khi đích bị đổi thành khóa đã được một bản ghi khác chiếm giữ, phương thức này trả về false và <strong>giữ nguyên chỉ mục</strong>:
    /// nếu cố tình sửa tiếp sẽ khiến hai bản ghi dùng chung một khóa, từ đó thư viện ứng dụng có một cặp trùng lặp không thể phân biệt,
    /// sau này xóa bất kỳ bản ghi nào cũng sẽ xóa luôn khóa dùng chung và bản ghi còn lại sẽ không còn tham gia khử trùng lặp nữa.
    /// Bên gọi cần giải thích xung đột cho người dùng (định vị lại) nên tự kiểm tra trước,
    /// xem <c>LibraryMaintenance.Relocate</c>.
    /// </para>
    /// <para>
    /// Khi trả về false, <b>đối tượng mà bên gọi truyền vào đã bị chính bên gọi sửa đổi rồi</b>,
    /// chỉ mục sẽ không sửa ngược lại giúp nó — bên gọi phải tự khôi phục mọi trường đã chạm vào.
    /// Đây chính là cái giá của mô hình "sửa tại chỗ", cũng là lý do <see cref="_keyByEntry"/>
    /// bắt buộc phải tồn tại.
    /// </para>
    /// </remarks>
    public bool Update(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var existing = Find(entry.Id);
        if (existing is null)
        {
            return false;
        }

        var index = Data.Items.IndexOf(existing);
        if (index < 0)
        {
            return false;
        }

        var newKey = AppEntryKey.Compute(entry);

        if (_byKey.TryGetValue(newKey, out var holder)
            && !ReferenceEquals(holder, existing)
            && !ReferenceEquals(holder, entry))
        {
            return false;
        }

        Deindex(existing);
        Data.Items[index] = entry;
        _byId[entry.Id] = entry;
        Index(entry, newKey);
        Version++;
        return true;
    }

    /// <summary>Sinh id mờ và vĩnh viễn cho bản ghi thư viện ứng dụng mới.</summary>
    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>Ghi danh một bản ghi cùng khóa của nó vào hai bảng chỉ mục.</summary>
    private void Index(AppEntry entry, string key)
    {
        _byId[entry.Id] = entry;
        _byKey[key] = entry;
        _keyByEntry[entry] = key;
    }

    /// <summary>
    /// Gỡ một bản ghi khỏi hai bảng chỉ mục, đồng thời xóa luôn khóa mà nó đang chiếm giữ.
    /// </summary>
    /// <remarks>
    /// Khóa được lấy lại theo tham chiếu, nên ngay cả khi nội dung mục đã bị bên gọi sửa đổi,
    /// khóa mà nó chiếm giữ <strong>ban đầu</strong> vẫn được dọn sạch.
    /// </remarks>
    private void Deindex(AppEntry entry)
    {
        _byId.Remove(entry.Id);

        if (_keyByEntry.Remove(entry, out var key))
        {
            _byKey.Remove(key);
        }
    }

    private void RebuildIndexes()
    {        _byId.Clear();
        _byKey.Clear();
        _keyByEntry.Clear();
        AnomalousEntryCount = 0;

        // Với các bản ghi không thể khởi chạy thì chỉ bỏ qua chứ không vứt đi: tệp chỉnh sửa thủ công nhờ vậy vẫn giữ nguyên nội dung,
        // giai đoạn 3 sẽ hiển thị các dòng hỏng lên để sửa chữa.
        foreach (var entry in Data.Items)
        {
            // System.Text.Json sẽ giải tuần tự hóa null trong mảng JSON thành phần tử null trong danh sách.
            // Trước đây chỗ này truy cập thẳng entry.IsUsable, nên {"items":[null]} liền ném ra
            // NullReferenceException khiến toàn bộ chương trình không khởi động được — một tệp cấu hình sửa tay bị hỏng không nên gây ra hậu quả đó.
            if (entry is null || !entry.IsUsable)
            {
                AnomalousEntryCount++;
                continue;
            }

            var key = AppEntryKey.Compute(entry);

            // Dùng TryAdd thay vì gán trực tiếp: trong tệp chỉnh sửa thủ công có thể thực sự có hai bản ghi nội dung giống hệt nhau.
            // Chỉ lập chỉ mục bản ghi đầu tiên, để bản ghi kia vẫn tìm được qua Find(id) nhưng không tham gia khử trùng lặp,
            // thay vì để bản ghi sau âm thầm đè mất bản ghi trước.
            if (_byKey.TryAdd(key, entry))
            {
                _keyByEntry[entry] = key;
            }

            _byId[entry.Id] = entry;
        }
    }

    /// <summary>
    /// Sửa các tham chiếu biểu tượng tùy chỉnh được quản lý đang trỏ đi nơi khác về thư mục dữ liệu hiện tại.
    /// </summary>
    /// <remarks>
    /// <see cref="AppEntry.CustomIconPath"/> lưu đường dẫn tuyệt đối, trỏ tới bản sao tệp nằm dưới <c>icons/</c>
    /// do chúng ta sao chép. Sau khi toàn bộ thư mục dữ liệu bị dời sang chỗ khác (đổi máy tính, di chuyển thủ công, hoặc khôi phục sang
    /// thư mục khác), các tham chiếu này vẫn trỏ về vị trí cũ — tệp rõ ràng đã chuyển sang rồi mà biểu tượng lại hỏng.
    ///
    /// <para>
    /// <b>Vị trí của biểu tượng được quản lý lấy thư mục dữ liệu hiện tại làm chuẩn.</b>
    /// Phiên bản đầu dùng cách "chỉ viết lại khi đường dẫn gốc không tồn tại", nhưng không có tác dụng khi khôi phục sang thư mục khác trên **cùng một máy**:
    /// thư mục cũ vẫn còn, đường dẫn cũ vẫn phân giải được bình thường, thế là tham chiếu cứ trỏ mãi về thư mục cũ,
    /// đến khi xóa thư mục cũ thì biểu tượng mất luôn. Quyền sở hữu tệp được quản lý không thể phụ thuộc vào việc thư mục cũ có tình cờ còn đó hay không.
    /// </para>
    ///
    /// <para>
    /// Tiêu chí nhận diện là tiền tố tên tệp (<see cref="AppPaths.ManagedCustomIconPrefix"/>):
    /// biểu tượng tùy chỉnh được quản lý luôn có dạng <c>custom-*</c>, và chỉ có thể nằm trong <c>icons/</c> của chính chúng ta.
    /// Chỉ khi thư mục hiện tại <strong>thực sự có tệp này</strong> thì mới viết lại — không có thì giữ nguyên,
    /// tuyệt đối không tự bịa ra một đường dẫn mới; đường dẫn trỏ ra ngoài icons/ không phải do chúng ta quản lý,
    /// đụng vào nó là phá hỏng dữ liệu người dùng.
    /// </para>
    ///
    /// <para>
    /// Ở đây chỉ sửa đối tượng trong bộ nhớ, việc ghi xuống đĩa vẫn do bên gọi quyết định (<see cref="Load"/> không bao giờ ghi tệp,
    /// đây là quy ước nhất quán của tầng dữ liệu). Quy trình khôi phục sẽ chủ ý lưu lại một lần vì việc này, xem <c>BackupService</c>.
    /// </para>
    /// </remarks>
    private void RepairManagedIconReferences()
    {
        RepairedIconReferenceCount = 0;

        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(_store.Path));

        if (string.IsNullOrEmpty(dataRoot))
        {
            return;
        }

        foreach (var entry in Data.Items)
        {
            // Trong tệp chỉnh sửa thủ công có thể có phần tử rỗng.
            if (entry is null || !AppPaths.IsManagedCustomIconPath(entry.CustomIconPath))
            {
                continue;
            }

            var current = entry.CustomIconPath!;
            var candidate = AppPaths.ManagedCustomIconPath(dataRoot, Path.GetFileName(current));

            // Đã trỏ về thư mục hiện tại rồi thì không cần đụng vào; trong thư mục hiện tại không có tệp này thì cũng không được trỏ bừa sang.
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                continue;
            }

            entry.CustomIconPath = candidate;
            RepairedIconReferenceCount++;
        }
    }
}
