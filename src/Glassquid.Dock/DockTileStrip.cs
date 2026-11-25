using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glassquid.Platform;
using Glassquid.Platform.Displays;
using Glassquid.UI.Theming;

namespace Glassquid.Dock;

/// <summary>Trạng thái vẽ <strong>thực tế</strong> của một ô trong khung hình vẽ cuối cùng.</summary>
/// <param name="EntryId">ID mục.</param>
/// <param name="Scale">Tỷ lệ thu phóng thực tế được áp dụng.</param>
/// <param name="ZIndex"><c>Panel.ZIndex</c> thực tế — giá trị càng lớn càng nằm ở trên, tức là thứ tự chồng lớp mà người dùng nhìn thấy.</param>
/// <param name="Left">Mép trái thực tế trên canvas.</param>
public readonly record struct PaintedTileState(string EntryId, double Scale, int ZIndex, double Left);

/// <summary>
/// Dải ô của Dock: bố cục khe cố định, hoạt ảnh phóng đại, kéo để sắp xếp lại và cuộn khi tràn đều nằm ở đây.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nguồn hình học duy nhất.</b> Mỗi khung hình tính toán vị trí và tỷ lệ của mọi ô bằng
/// <see cref="DockMagnification.Compute"/>; việc vẽ (định vị Canvas + biến đổi thu phóng) và kiểm tra trúng đích
/// (<see cref="DockMagnification.HitTest"/>) dùng <strong>cùng một</strong> kết quả;
/// mục tiêu khi nhấp không lấy từ event sender của WPF mà dùng phán đoán của HitTest —
/// điều này đảm bảo về mặt cấu trúc rằng "thấy A là nhấp trúng A", kể cả khi đang giữa chừng hoạt ảnh (D-05).
/// </para>
///
/// <para>
/// <b>Kéo để sắp xếp lại (D-02/R-08).</b> Việc kéo ô dùng cơ chế kéo-thả riêng của WPF (trong cùng cửa sổ),
/// cử chỉ chỉ được dịch thành ba hành động của <see cref="DockReorderModel"/>:
/// xem trước chỉ đổi thứ tự trong bộ nhớ (vạch chỉ báo chèn), khi thả tay để xác nhận thì máy trạng thái ghi xuống đĩa <strong>một lần</strong> duy nhất,
/// khi nhấn Esc hoặc kéo ra ngoài thanh để hủy thì tuyệt đối không ghi xuống đĩa. Tính chất có thể khẳng định được số lần ghi đĩa đến từ máy trạng thái,
/// không dựa vào ý thức của tầng giao diện.
/// </para>
///
/// <para>
/// <b>Tràn nội dung (mục 6 của kế hoạch sửa đổi).</b> Tổng chiều rộng các mục cố định bị giới hạn trần ở chiều rộng khả dụng
/// (vùng làm việc màn hình đích trừ đi phần dư phóng đại và lề trong), phần vượt quá sẽ cuộn theo hướng Dock
/// (hiện tại cả bốn vị trí neo đều xếp ngang nên cuộn ngang), hỗ trợ con lăn chuột, ẩn thanh cuộn,
/// hai đầu dùng dải mờ chuyển sắc để gợi ý vẫn còn nội dung. Kích thước ô không bao giờ bị thu nhỏ tổng thể.
/// </para>
///
/// <para>
/// Tọa độ trúng đích và tọa độ con trỏ luôn được quy đổi sang <strong>hệ tọa độ nội dung</strong> (độ lệch cuộn + vị trí trong khung nhìn),
/// nên sau khi cuộn vẫn nhấp chính xác.
/// </para>
/// </remarks>
public sealed class DockTileStrip : Border
{
    /// <summary>Định dạng dữ liệu kéo nội bộ: kéo-thả sắp xếp ô trong cùng tiến trình.</summary>
    private const string TileDragFormat = "Glassquid-TileDrag";

    /// <summary>Kéo ra ngoài khoảng cách này (DIP, theo trục) được coi là "kéo ra ngoài để bỏ ghim" (D-03).</summary>
    private const double UnpinDragDistance = 80;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _canvas;
    private readonly Border _leadingFade;
    private readonly Border _trailingFade;

    private readonly List<DockTileModel> _models = [];
    private readonly List<Border> _tiles = [];

    private readonly DockRenderLoop _animationLoop;
    private bool _pointerActive;
    private double _pointerPosition = double.NaN;
    private int _hoveredTileIndex = -1;

    private double _iconSize = 48;
    private double _spacing = 8;
    private double _peak = 1.15;
    private double _verticalPadding = 8;

    /// <summary>Giới hạn chiều rộng khả dụng, DIP. 0 nghĩa là không giới hạn (do lớp ngoài quyết định).</summary>
    private double _maxContentWidth;

    /// <summary>Ô được nhấp. Tham số là ID mục trong thư viện ứng dụng.</summary>
    public event EventHandler<string>? TileClicked;

    /// <summary>Ô được nhấp chuột phải (D-04). Tham số là ID mục trong thư viện ứng dụng; lối vào tích hợp sẵn không kích hoạt.</summary>
    public event EventHandler<string>? TileContextMenuRequested;

    /// <summary>Hoàn tất xác nhận kéo (thứ tự đã ghi xuống đĩa) hoặc hủy bỏ, host nên dựng lại hàng ô.</summary>
    public event EventHandler? ReorderFinished;

    /// <summary>Ô bị kéo ra ngoài thanh (D-03): yêu cầu bỏ ghim, không xóa mục khỏi thư viện.</summary>
    public event EventHandler<string>? UnpinRequested;

    /// <summary>Thay đổi trạng thái di chuột qua ô (EntryId là null nghĩa là rời khỏi ô).</summary>
    public event EventHandler<DockTileHoverEventArgs>? TileHoverChanged;

    /// <summary>Máy trạng thái sắp xếp được host truyền vào: delegate ghi đĩa nằm trong tay host (lưu một lần qua bộ lưu trữ bố cục).</summary>
    private DockReorderModel? _reorder;
    private string? _pressedEntryId;
    private Point _pressPosition;
    private bool _dragActive;
    internal bool IsDragging => _dragActive;
    private int _dragStartIndex = -1;

    /// <summary>Trong thanh có nhận được sự kiện Drop của lần kéo này hay chưa (thứ tự đã được xác nhận ngay tại thời điểm đó).</summary>
    private bool _droppedInStrip;

    /// <summary>Lý do hủy của lần kéo này; nếu chưa từng hủy là <see cref="DragCancelReason.None"/>.</summary>
    private DragCancelReason _dragCancelReason;

    /// <summary>Lý do hủy của lần kéo gần nhất. Dùng cho chẩn đoán và kiểm thử.</summary>
    public DragCancelReason LastDragCancelReason => _dragCancelReason;

    /// <summary>
    /// Hình học của khung hình gần nhất <strong>thực sự được vẽ lên màn hình</strong>.
    /// </summary>
    /// <remarks>
    /// Căn cứ duy nhất cho việc trúng đích (thấy gì được nấy): việc vẽ dùng vị trí con trỏ đã làm mượt, còn cú nhấp xảy ra tại thời điểm bất kỳ,
    /// nếu lúc nhấp lại dùng con trỏ tức thời tính lại một bản hình học khác, giữa chừng hoạt ảnh sẽ bị lệch với hình ảnh trên màn hình —
    /// thực tế từng gặp trường hợp "hình học đã vẽ trúng ô thứ nhất, nhưng xử lý nhấp lại trúng ô thứ hai".
    /// </remarks>
    private TileGeometry[] _paintedGeometry = [];
    private TileGeometry[] _targetGeometry = [];
    private int[] _paintOrder = [];
    private readonly IComparer<int> _paintOrderComparer;

    /// <summary>Truyền máy trạng thái sắp xếp vào và bật kéo-thả nội bộ.</summary>
    public void SetReorderModel(DockReorderModel? reorder)
    {
        _reorder = reorder;
        AllowDrop = reorder is not null;
    }

    /// <summary>
    /// ID các mục tham gia sắp xếp (<strong>chỉ gồm các mục thực trong thư viện ứng dụng</strong>).
    /// </summary>
    /// <remarks>
    /// Các lối vào tích hợp sẵn (Launcher, Hiện màn hình nền, Thùng rác) cố định ở vùng riêng: không tham gia sắp xếp,
    /// cũng không thể kéo ra để gỡ bỏ. Khi xác nhận sắp xếp, đây chính là danh sách được ghi vào bố cục.
    /// </remarks>
    public IReadOnlyList<string> ReorderCandidateIds =>
        _models.Where(model => model.IsPinned && !DockLayoutPolicy.IsEntryTile(model.EntryId))
            .Select(model => model.EntryId).ToList();

    /// <summary>Số lượng ô ứng dụng (phần tiền tố trong hàng ô; phần còn lại là lối vào tích hợp sẵn).</summary>
    private int AppTileCount { get; set; }

    /// <summary>Đường phân cách giữa các ô ứng dụng và các lối vào tích hợp sẵn (tách biệt hai nhóm về mặt thị giác).</summary>
    private readonly System.Windows.Shapes.Rectangle _groupDivider = new()
    {
        Width = 1,
        Fill = Brushes.Gray,
        Opacity = 0.5,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    public DockTileStrip()
    {
        _paintOrderComparer = Comparer<int>.Create((left, right) =>
            DockMagnification.CompareDrawOrder(_paintedGeometry, left, right));
        Background = Brushes.Transparent;
        ClipToBounds = true;

        _canvas = new Canvas
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.Transparent,
        };

        _scroller = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Focusable = false,
        };
        _scroller.ScrollChanged += (_, _) => UpdateFades();

        _leadingFade = CreateFade(0);
        _trailingFade = CreateFade(1);

        // Điểm ghi nhận hủy bằng Esc: WPF chỉ kích hoạt sự kiện này trên phần tử khởi phát kéo,
        // mà nguồn của DoDragDrop chính là control này.
        AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler(OnQueryContinueDrag));

        var overlay = new Grid { Background = Brushes.Transparent };
        _ = overlay.Children.Add(_scroller);
        _ = overlay.Children.Add(_leadingFade);
        _ = overlay.Children.Add(_trailingFade);

        Child = overlay;

        _animationLoop = new DockRenderLoop(OnFrame);
        Unloaded += (_, _) => ResetHover();
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) ResetHover(); };
    }

    /// <summary>
    /// Dựng lại toàn bộ hàng. Gọi khi tập hợp ô thay đổi; thiết lập kích thước đi qua <see cref="SetMetrics"/>.
    /// </summary>
    /// <remarks>
    /// Quy ước: <strong>phía trước đều là ô ứng dụng, một số ô cuối cùng là lối vào tích hợp sẵn</strong>
    /// (ID có tiền tố <see cref="DockLayoutPolicy.EntryTilePrefix"/>). Việc phân nhóm chỉ tính một lần ở đây,
    /// sau đó sắp xếp, kéo-thả và đường phân cách đều lấy nó làm chuẩn.
    /// </remarks>
    public void SetTiles(IReadOnlyList<DockTileModel> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        _models.Clear();
        _models.AddRange(tiles);

        // Lối vào tích hợp sẵn luôn nằm ở cuối (DockWindow xếp như vậy), nhưng ở đây không mặc định sẵn —
        // đếm số ứng dụng ở phần đầu theo tiền tố ID, gặp lối vào đầu tiên là dừng lại.
        AppTileCount = 0;

        foreach (var model in _models)
        {
            if (DockLayoutPolicy.IsEntryTile(model.EntryId))
            {
                break;
            }

            if (!model.IsPinned)
            {
                break;
            }

            AppTileCount++;
        }

        _canvas.Children.Clear();
        _tiles.Clear();

        for (var index = 0; index < _models.Count; index++)
        {
            // Chỉ gắn trình xử lý kéo-thả cho ô ứng dụng: lối vào tích hợp sẵn không tham gia sắp xếp và cũng không thể kéo ra để gỡ bỏ.
            var draggable = index < AppTileCount;
            var tile = CreateTile(_models[index], draggable);
            _tiles.Add(tile);
            _ = _canvas.Children.Add(tile);
        }

        _ = _canvas.Children.Add(_groupDivider);

        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        _hoveredTileIndex = -1;
        UpdateTileLayout();
        PaintFrame(null);
    }

    /// <summary>Áp dụng thiết lập kích thước và sắp xếp lại. Giới hạn trần cuộn cũng có hiệu lực tại đây.</summary>
    public void SetMetrics(double iconSize, double spacing, double peak, double verticalPadding, double maxContentWidth)
    {
        _iconSize = Math.Max(1, iconSize);
        _spacing = Math.Max(0, spacing);
        _peak = Math.Max(1, peak);
        _verticalPadding = Math.Max(0, verticalPadding);
        _maxContentWidth = Math.Max(0, maxContentWidth);

        // Giới hạn trần phải áp dụng lên **bố cục thực tế**, nếu không khung nhìn sẽ giãn rộng vô hạn theo nội dung:
        // các ô sẽ chỉ bị cửa sổ cắt mất, không cuộn được cũng không nhấp trúng được. Ở đây đặt giới hạn lên MaxWidth của chính control,
        // nhờ đó ScrollViewer bên trong nhận được khung nhìn hữu hạn và thanh cuộn ngang (kiểu ẩn) mới thực sự hoạt động.
        MaxWidth = _maxContentWidth > 0 ? _maxContentWidth : double.PositiveInfinity;

        UpdateTileLayout();
        PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>Con trỏ rời khỏi dải ô: tất cả các ô mượt mà trở về trạng thái chưa phóng đại.</summary>
    public void NotifyPointerLeft()
    {
        _pointerActive = false;
        var oldHovered = _hoveredTileIndex;
        _hoveredTileIndex = -1;
        if (oldHovered != -1)
        {
            DispatchTileHover(-1);
        }
        EnsureAnimating();
    }

    /// <summary>Tổng chiều rộng nội dung hiện tại (DIP, bao gồm phần phóng đại tràn ra hai đầu). Cung cấp cho lớp ngoài quyết định kích thước cửa sổ.</summary>
    public double RequiredLength =>
        DockMagnification.RequiredLength(_models.Count, _iconSize, _spacing, _peak);

    /// <summary>Độ dày cần thiết vuông góc với trục Dock (DIP, bao gồm không gian phóng đại).</summary>
    public double RequiredThickness =>
        DockMagnification.RequiredThickness(_iconSize, _peak, _verticalPadding);

    /// <summary>Nội dung có vượt quá chiều rộng trần hay không.</summary>
    public bool CanScroll => _maxContentWidth > 0 && RequiredLength > _maxContentWidth;

    /// <param name="draggable">
    /// Có tham gia kéo-thả sắp xếp hay không. Lối vào tích hợp sẵn truyền false: chúng cố định ở vùng riêng,
    /// vừa không tham gia sắp xếp ứng dụng, vừa không thể kéo ra để gỡ bỏ.
    /// </param>
    private Border CreateTile(DockTileModel model, bool draggable)
    {
        FrameworkElement body;

        if (model.Icon is { } iconSource)
        {
            var icon = new Image
            {
                Source = iconSource,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = model.IsMissing ? 0.4 : 1,
            };
            Bind(icon, Image.WidthProperty, model, nameof(DockTileModel.IconSize));
            Bind(icon, Image.HeightProperty, model, nameof(DockTileModel.IconSize));
            body = icon;
        }
        else
        {
            // Lối vào tích hợp sẵn (D-12/D-13/D-14) không có tệp biểu tượng trích xuất sẵn,
            // vẽ bằng ký tự Segoe Fluent Icons có sẵn của hệ thống, không phụ thuộc ngoài.
            body = new TextBlock
            {
                Text = model.Glyph ?? "?",
                Opacity = model.IsMissing ? 0.4 : 1,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(body, TextBlock.FontSizeProperty, model, nameof(DockTileModel.IconSize));
        }

        // Expose the visible body through UIA; a Border itself has no automation peer.
        System.Windows.Automation.AutomationProperties.SetName(body, model.DisplayName);
        System.Windows.Automation.AutomationProperties.SetAutomationId(body, "dock-tile:" + model.EntryId);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5,
            Height = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Bind(dot, System.Windows.Shapes.Shape.FillProperty, model, nameof(DockTileModel.DotBrush));
        Bind(dot, VisibilityProperty, model, nameof(DockTileModel.RunningVisibility));

        var content = new StackPanel();
        _ = content.Children.Add(body);
        _ = content.Children.Add(dot);

        var tile = new Border
        {
            Child = content,
            RenderTransform = new ScaleTransform(1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = model.ToolTipText,
            DataContext = model,
        };

        // Trình xử lý kéo-thả chỉ gắn lên ô ứng dụng. Lối vào tích hợp sẵn không có kéo-thả, nên cũng không có đường "kéo ra để gỡ bỏ".
        if (draggable)
        {
            tile.MouseLeftButtonDown += OnTileMouseDown;
            tile.MouseMove += OnTileMouseMove;
        }

        tile.MouseEnter += (_, _) =>
        {
            var idx = _tiles.IndexOf(tile);
            if (idx >= 0 && idx != _hoveredTileIndex)
            {
                _hoveredTileIndex = idx;
                DispatchTileHover(_hoveredTileIndex);
            }
        };

        tile.MouseLeftButtonUp += OnTileMouseUp;
        tile.MouseRightButtonUp += OnTileContextMenu;
        return tile;
    }

    /// <summary>Nhấn xuống ô: ghi nhận điểm bắt đầu và mục, chờ vượt ngưỡng di chuyển mới thực sự bắt đầu kéo.</summary>
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: DockTileModel model })
        {
            return;
        }

        _pressedEntryId = model.EntryId;
        _pressPosition = e.GetPosition(this);
    }

    /// <summary>Giữ và di chuyển vượt ngưỡng → vào vòng lặp hộp thoại kéo-thả (chặn đến khi thả tay/hủy).</summary>
    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedEntryId is null || _dragActive
            || e.LeftButton != MouseButtonState.Pressed
            || _reorder is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var distance = (position - _pressPosition).Length;

        if (distance < 4)
        {
            return;
        }

        _dragActive = true;
        _droppedInStrip = false;
        _dragCancelReason = DragCancelReason.None;

        var draggedEntryId = _pressedEntryId;
        // Ứng viên sắp xếp chỉ gồm ô ứng dụng, cố định khe xuất phát của lần kéo này; ranh giới chèn luôn
        // được tính dựa trên khe trước khi kéo, tránh chỉ số đích bị trôi sau nhiều lần xem trước.
        var candidateIds = ReorderCandidateIds.ToArray();
        _dragStartIndex = Array.IndexOf(candidateIds, draggedEntryId);
        _reorder.Begin(candidateIds);

        var data = new DataObject(TileDragFormat, draggedEntryId);
        data.SetData(DataFormats.Serializable, draggedEntryId);

        try
        {
            // Bên trong vòng lặp modal: DragOver/Drop/DragLeave/QueryContinueDrag đều quay về control này.
            // Việc hủy bằng Esc do OLE xử lý (lúc này DoDragDrop trả về None), lý do hủy được ghi lại qua
            // EscapePressed của QueryContinueDrag — nếu chỉ phán đoán theo vị trí sẽ nhận nhầm
            // "kéo ra ngoài thanh rồi bấm Esc" thành kéo ra để bỏ ghim.
            _ = DragDrop.DoDragDrop(this, data, DragDropEffects.Move);

            // Chỉ khi cử chỉ kết thúc mới phán đoán chính thức một lần: tuyệt đối không ghi đĩa trong lúc kéo (R-08).
            var endedOutside = DragEndedOutside();
            var cancelled = ResolveCancelReason();

            var action = DockDragGesture.DecideEnd(_droppedInStrip, endedOutside, cancelled);

            if (action is DragEndAction.CancelOnly or DragEndAction.CancelAndUnpin && _reorder.IsActive)
            {
                _ = _reorder.Cancel();
            }

            if (action == DragEndAction.CancelAndUnpin && !string.IsNullOrEmpty(draggedEntryId))
            {
                UnpinRequested?.Invoke(this, draggedEntryId);
            }

            if (action != DragEndAction.KeepCommitted || !_reorder.IsActive)
            {
                ReorderFinished?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _dragActive = false;
            _droppedInStrip = false;
            _pressedEntryId = null;
            _dragStartIndex = -1;
        }
    }

    /// <summary>
    /// Lần kéo này có bị hủy hay không, đồng thời ghi lại lý do.
    /// </summary>
    /// <remarks>
    /// Hai luồng tín hiệu, nhận được luồng nào tính luồng đó:
    /// <list type="number">
    /// <item><c>EscapePressed</c> do <c>QueryContinueDrag</c> báo — đường chính thức;</item>
    /// <item>Khi cử chỉ kết thúc mà nút chuột chính vẫn đang giữ — biểu hiện quan sát được của việc hủy bằng Esc (thả bình thường chắc chắn đã nhả chuột).
    /// Luồng này là phương án dự phòng: phòng trường hợp trên nền tảng luồng 1 không kích hoạt, cũng không xóa nhầm mục mà người dùng đã hủy.</item>
    /// </list>
    /// </remarks>
    private bool ResolveCancelReason()
    {
        if (_dragCancelReason != DragCancelReason.None)
        {
            return true;
        }

        if (InputState.IsPrimaryMouseButtonDown())
        {
            _dragCancelReason = DragCancelReason.PrimaryButtonStillDown;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Thông báo trạng thái của vòng lặp kéo-thả OLE: ghi nhận Esc tại đây.
    /// </summary>
    /// <remarks>
    /// Phải gắn trên phần tử nguồn kéo — WPF chỉ kích hoạt sự kiện này cho đúng phần tử đã khởi phát thao tác kéo.
    /// </remarks>
    private void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            _dragCancelReason = DragCancelReason.Escape;
        }
    }

    /// <summary>
    /// Khi cử chỉ kết thúc, con trỏ có nằm ngoài thanh hay không (bao gồm ngưỡng bỏ ghim).
    /// </summary>
    /// <remarks>
    /// Dùng <strong>vị trí con trỏ tại đúng thời điểm đó</strong> để phán đoán, chứ không dùng vị trí của một lần <c>DragLeave</c> nào đó trong lúc kéo:
    /// khi di chuyển chậm ra khỏi biên, vị trí lần đó chưa chắc đạt ngưỡng, khiến kết quả của cùng một cử chỉ thay đổi theo tốc độ chuột.
    /// Con trỏ là pixel vật lý, qua phép quy đổi màn hình của WPF thành tọa độ control (đã tính DPI).
    /// </remarks>
    private bool DragEndedOutside()
    {
        if (DisplayService.GetCursorPixelPoint() is not { } cursor)
        {
            // Không đọc được con trỏ (phiên điều khiển từ xa, v.v.): xử lý như "vẫn ở trong thanh", thà không động vào bố cục.
            return false;
        }

        Point local;

        try
        {
            local = PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }

        if (double.IsNaN(local.X) || double.IsNaN(local.Y))
        {
            return false;
        }

        return local.X < -UnpinDragDistance
            || local.X > ActualWidth + UnpinDragDistance
            || local.Y < -UnpinDragDistance
            || local.Y > ActualHeight + UnpinDragDistance;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        PreviewReorderAtEventPoint(e, entryId);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (!IsInternalTileDrag(e.Data, out var entryId) || _reorder is null)
        {
            return;
        }

        e.Handled = true;
        _droppedInStrip = true;

        // OLE có thể không phát thêm một lần DragOver ngay trước khi thả tay. Drop có sẵn tọa độ con trỏ cuối cùng,
        // dùng nó làm mới xem trước rồi mới xác nhận, tránh ghi xuống đĩa vị trí cũ của lần DragOver trước đó.
        PreviewReorderAtEventPoint(e, entryId);

        // Thả tay = hoàn tất thao tác: máy trạng thái ghi xuống đĩa thống nhất một lần tại đây (R-08).
        var outcome = _reorder.Commit();

        if (outcome == ReorderOutcome.SaveFailed)
        {
            // Thất bại đã được máy trạng thái khôi phục thứ tự gốc; đưa cho người dùng một lời giải thích dễ hiểu.
            System.Windows.MessageBox.Show(
                "Không thể lưu thứ tự; có thể tệp dữ liệu hiện không ghi được. Thứ tự ban đầu đã được khôi phục.",
                "Glassquid",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        ReorderFinished?.Invoke(this, EventArgs.Empty);
    }

    private void PreviewReorderAtEventPoint(DragEventArgs e, string entryId)
    {
        if (_dragStartIndex < 0 || _reorder?.IsActive != true)
        {
            return;
        }

        // Tham số OLE DragOver/Drop mang tọa độ đích của sự kiện lần này; độ lệch cuộn chỉ cộng một lần.
        var viewportX = e.GetPosition(_scroller).X;
        if (double.IsNaN(viewportX))
        {
            return;
        }

        var insertionIndex = IndexForInsert(ToContentX(viewportX));
        var targetIndex = insertionIndex > _dragStartIndex ? insertionIndex - 1 : insertionIndex;
        _reorder.PreviewMove(entryId, targetIndex);
    }

    private bool IsInternalTileDrag(IDataObject data, out string entryId)
    {
        if (_dragActive && data.GetDataPresent(TileDragFormat))
        {
            var value = data.GetData(TileDragFormat) as string;

            if (!string.IsNullOrEmpty(value))
            {
                entryId = value;
                return true;
            }
        }

        entryId = string.Empty;
        return false;
    }

    /// <summary>
    /// Tọa độ nội dung rơi vào ranh giới chèn nào: bên trái tâm mỗi ô thì chèn ra trước, bên phải thì chèn ra sau.
    /// </summary>
    /// <remarks>
    /// Chỉ lấy giá trị trong phạm vi <strong>ô ứng dụng</strong>: phía lối vào tích hợp sẵn không phải mục tiêu sắp xếp,
    /// kéo tới đó cũng chỉ là "chèn vào sau ứng dụng cuối cùng".
    /// </remarks>
    private int IndexForInsert(double contentPosition)
    {
        if (AppTileCount == 0)
        {
            return 0;
        }

        var geometry = DockMagnification.Compute(_models.Count, _iconSize, _spacing, _peak, null);

        for (var index = 0; index < AppTileCount; index++)
        {
            if (contentPosition < geometry[index].Center)
            {
                return index;
            }
        }

        return AppTileCount;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Trúng đích không lấy theo sender, cũng không tính lại hình học tại đây: dùng **khung hình vẽ cuối cùng** để phán đoán
        // (thấy gì được nấy). Tính lại sẽ bị lệch với hình ảnh trên màn hình khi đang giữa chừng hoạt ảnh.
        // Quy đổi đi theo một con đường duy nhất "tọa độ khung nhìn → tọa độ nội dung" (chỉ cộng độ lệch một lần).
        var viewportX = PointerViewportPosition();

        if (viewportX is not { } position)
        {
            return;
        }

        var hit = ResolveHitAtViewportX(position);

        if (hit >= 0 && hit < _models.Count)
        {
            TileClicked?.Invoke(this, _models[hit].EntryId);
        }
    }

    private void OnTileContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: DockTileModel { EntryId: var entryId } }
            && !entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            TileContextMenuRequested?.Invoke(this, entryId);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        _pointerActive = true;

        // Ghi nhận vị trí đích trước, phép nội suy giao cho vòng lặp khung hình — bão sự kiện chuột không còn kích hoạt bố trí lại ở từng sự kiện nữa.
        // WPF thỉnh thoảng trả về tọa độ NaN (đang bố cục), khi đó giữ nguyên mục tiêu trước.
        if (PointerContentPosition() is { } contentX)
        {
            _pointerPosition = contentX;
            var hit = ResolveHit(contentX);
            if (hit != _hoveredTileIndex)
            {
                _hoveredTileIndex = hit;
                DispatchTileHover(_hoveredTileIndex);
            }
        }

        EnsureAnimating();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        NotifyPointerLeft();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (CanScroll)
        {
            _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Quy đổi vị trí của con trỏ trong <strong>khung nhìn</strong> sang tọa độ nội dung; trả về null nếu không đọc được.
    /// </summary>
    /// <remarks>
    /// <strong>Phép quy đổi chỉ có một chỗ này, và cũng chỉ cộng độ lệch một lần.</strong>
    /// Tọa độ lấy từ <c>Mouse.GetPosition(_canvas)</c> vốn đã là tọa độ nội dung — canvas là phần tử con của nội dung cuộn,
    /// không gian tọa độ của nó tự thân đã bao gồm dịch chuyển cuộn; nếu đi qua <see cref="ToContentX"/> thêm lần nữa
    /// sẽ cộng độ lệch hai lần. Thực đo: khi cuộn 120 DIP, vị trí khung nhìn 50 bị tính thành 290 (đúng phải là 170),
    /// biểu hiện thành điểm nhấp, phóng đại và vị trí chèn khi kéo đều bị lệch tổng thể sau khi cuộn.
    /// </remarks>
    private double? PointerViewportPosition()
    {
        // Lấy tọa độ tương đối theo control này (= khung nhìn), không lấy tương đối theo canvas.
        var position = Mouse.GetPosition(this);

        return double.IsNaN(position.X) ? null : position.X;
    }

    /// <summary>Vị trí con trỏ trong hệ tọa độ nội dung của canvas cuộn, đã bao gồm biến đổi cuộn thực tế.</summary>
    private double? PointerContentPosition()
    {
        // Biến đổi tọa độ của chính canvas đã phản ánh độ lệch ScrollViewer; ở đây không được cộng thêm độ lệch nữa.
        var position = Mouse.GetPosition(_canvas);

        return double.IsNaN(position.X) ? null : position.X;
    }

    /// <summary>
    /// Quy đổi x <strong>trong khung nhìn</strong> sang tọa độ nội dung (cộng thêm độ lệch cuộn hiện tại).
    /// </summary>
    /// <remarks>
    /// Đầu vào bắt buộc phải là tọa độ khung nhìn. Vị trí đọc tương đối theo canvas vốn đã là tọa độ nội dung, đừng đi qua đây nữa —
    /// làm vậy sẽ cộng độ lệch hai lần (xem <see cref="PointerViewportPosition"/>).
    /// </remarks>
    public double ToContentX(double viewportX) => viewportX + _scroller.HorizontalOffset;

    /// <summary>
    /// Phân giải trúng đích bằng tọa độ <strong>trong khung nhìn</strong> (sau khi cuộn vẫn chính xác).
    /// </summary>
    /// <remarks>
    /// Nhấp chuột, di chuột và chèn khi kéo đều đi qua con đường này, do đó tính chất "chỉ cộng độ lệch một lần"
    /// được bao phủ trong kiểm thử chính là mã thực sự chạy lúc runtime, chứ không phải viết thêm một bản logic tương đương khác.
    /// </remarks>
    public int ResolveHitAtViewportX(double viewportX) => ResolveHit(ToContentX(viewportX));

    /// <summary>Cuộn tới độ lệch ngang chỉ định (DIP). Dùng cho chẩn đoán và kiểm thử.</summary>
    public void ScrollToHorizontalOffset(double offset) => _scroller.ScrollToHorizontalOffset(offset);

    /// <summary>Độ lệch cuộn ngang hiện tại (DIP). Dùng cho chẩn đoán và kiểm thử.</summary>
    public double HorizontalOffset => _scroller.HorizontalOffset;

    /// <summary>Chiều rộng khung nhìn hiện tại (DIP). Dùng cho chẩn đoán và kiểm thử.</summary>
    public double ViewportWidth => _scroller.ViewportWidth;

    /// <summary>Cuộn tới tận cùng bên phải. Dùng cho chẩn đoán và kiểm thử.</summary>
    public void ScrollToEnd() => _scroller.ScrollToRightEnd();

    private void EnsureAnimating()
    {
        if (IsVisible) _animationLoop.Start();
        else PaintFrame(CurrentPointerOrNull());
    }

    /// <summary>Khung hình hoạt ảnh: mọi ô đồng thời đuổi theo tỷ lệ đích tương ứng với con trỏ hiện tại, tất cả vào đúng vị trí thì dừng khung hình.</summary>
    private bool OnFrame(double deltaSeconds)
    {
        var targetPointer = CurrentPointerOrNull();
        // Resolve the latest pointer once per frame, rather than for every queued mouse event.
        var newHovered = targetPointer is { } position ? FindHoveredTile(position) : -1;
        if (newHovered != _hoveredTileIndex)
        {
            _hoveredTileIndex = newHovered;
            DispatchTileHover(_hoveredTileIndex);
        }
        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            PaintFrame(targetPointer);
            return true;
        }
        return AnimateFrame(targetPointer, deltaSeconds);
    }

    private void ResetHover()
    {
        _animationLoop.Stop();
        _pointerActive = false;
        _pointerPosition = double.NaN;
        var oldHovered = _hoveredTileIndex;
        _hoveredTileIndex = -1;
        if (oldHovered != -1)
        {
            DispatchTileHover(-1);
        }
        PaintFrame(null);
    }

    private void DispatchTileHover(int index)
    {
        if (index >= 0 && index < _models.Count)
        {
            var model = _models[index];
            var bounds = Rect.Empty;
            try
            {
                if (index < _tiles.Count && _tiles[index].IsVisible)
                {
                    var tile = _tiles[index];
                    if (Window.GetWindow(tile) is Window win)
                    {
                        var pt = tile.TranslatePoint(new Point(0, 0), win);
                        bounds = new Rect(win.Left + pt.X, win.Top + pt.Y, Math.Max(_iconSize, tile.ActualWidth), Math.Max(_iconSize, tile.ActualHeight));
                    }
                }
            }
            catch
            {
                // Visual may not be attached to presentation source
            }

            TileHoverChanged?.Invoke(this, new DockTileHoverEventArgs(model.EntryId, bounds));
        }
        else
        {
            TileHoverChanged?.Invoke(this, new DockTileHoverEventArgs(null, Rect.Empty));
        }
    }

    public Rect GetTileScreenBounds(string entryId)
    {
        for (var i = 0; i < _models.Count && i < _tiles.Count; i++)
        {
            if (string.Equals(_models[i].EntryId, entryId, StringComparison.Ordinal))
            {
                try
                {
                    if (_tiles[i].IsVisible)
                    {
                        var tile = _tiles[i];
                        if (Window.GetWindow(tile) is Window win)
                        {
                            var pt = tile.TranslatePoint(new Point(0, 0), win);
                            return new Rect(win.Left + pt.X, win.Top + pt.Y, Math.Max(_iconSize, tile.ActualWidth), Math.Max(_iconSize, tile.ActualHeight));
                        }
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        return Rect.Empty;
    }

    public FrameworkElement? GetTileElement(string entryId)
    {
        for (var i = 0; i < _models.Count && i < _tiles.Count; i++)
        {
            if (string.Equals(_models[i].EntryId, entryId, StringComparison.Ordinal))
            {
                return _tiles[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Vẽ một khung hình theo vị trí con trỏ đã cho, và lưu đệm hình học của khung hình này cho việc kiểm tra trúng đích sử dụng.
    /// </summary>
    /// <remarks>
    /// Phần làm mượt do vòng lặp khung hình phụ trách; phương thức này chỉ làm đúng bước "con trỏ đã cho → hình học → màn hình",
    /// nhờ đó trong kiểm thử có thể phát lại một khung hình một cách tất định để xác minh cú nhấp khớp với hình ảnh.
    /// </remarks>
    public void PaintFrame(double? pointerPosition)
    {
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(_paintedGeometry, pointerPosition);
        PaintGeometry(_paintedGeometry);
    }

    private bool AnimateFrame(double? pointerPosition, double deltaSeconds)
    {
        var targets = _targetGeometry;
        DockMagnification.ComputeInto(targets, _iconSize, _spacing, _peak, pointerPosition);
        ApplyHoveredPeak(targets, pointerPosition);
        var geometry = _paintedGeometry;
        var allSettled = true;

        for (var index = 0; index < targets.Length; index++)
        {
            var currentScale = _tiles[index].RenderTransform is ScaleTransform transform
                ? transform.ScaleX
                : 1.0;
            var targetScale = targets[index].Scale;
            var nextScale = DockHoverScale.Advance(currentScale, targetScale, deltaSeconds);

            if (nextScale != targetScale)
            {
                allSettled = false;
            }

            geometry[index] = new TileGeometry(targets[index].Center, nextScale);
        }

        PaintGeometry(geometry);
        return allSettled;
    }

    private void ApplyHoveredPeak(TileGeometry[] geometry, double? pointerPosition)
    {
        if (pointerPosition is null || _hoveredTileIndex < 0 || _hoveredTileIndex >= geometry.Length)
        {
            return;
        }

        geometry[_hoveredTileIndex] = new TileGeometry(geometry[_hoveredTileIndex].Center, _peak);
    }

    private int FindHoveredTile(double contentPosition)
    {
        var nearestIndex = -1;
        var nearestDistance = double.PositiveInfinity;
        var halfExpandedWidth = _iconSize * _peak / 2;

        for (var index = 0; index < _models.Count; index++)
        {
            var center = DockMagnification.SlotCenter(index, _iconSize, _spacing, _peak);
            var distance = Math.Abs(contentPosition - center);
            if (distance <= halfExpandedWidth && distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private void UpdateTileLayout()
    {
        var count = _models.Count;
        if (_paintedGeometry.Length != count)
        {
            _paintedGeometry = new TileGeometry[count];
            _targetGeometry = new TileGeometry[count];
            _paintOrder = Enumerable.Range(0, count).ToArray();
        }
        DockMagnification.ComputeInto(_paintedGeometry, _iconSize, _spacing, _peak, null);
        _canvas.Width = DockMagnification.RequiredLength(count, _iconSize, _spacing, _peak);
        _canvas.Height = RequiredThickness;
        var halfOverhangThickness = (_canvas.Height - _iconSize) / 2;
        for (var index = 0; index < count; index++)
        {
            Canvas.SetLeft(_tiles[index], _paintedGeometry[index].Center - _iconSize / 2);
            Canvas.SetTop(_tiles[index], halfOverhangThickness);
        }
        UpdateGroupDivider(_paintedGeometry);
        UpdateFades();
    }

    private void PaintGeometry(TileGeometry[] geometry)
    {
        // Thứ tự z dùng chung một bản cho cả việc vẽ và trúng đích: ô phóng to nằm trên, ở chỗ chồng lấn cái nào nhìn thấy được thì cái đó bị nhấp trúng.
        var order = _paintOrder;
        Array.Sort(order, _paintOrderComparer);

        for (var rank = 0; rank < order.Length; rank++)
        {
            var index = order[rank];
            var tile = _tiles[index];
            var scale = geometry[index].Scale;

            // Phần tử thứ 0 của order là "lớp trên cùng", còn ZIndex càng lớn càng nằm trên — vì vậy phải **gán đảo ngược**.
            // Trước đây khi viết Panel.SetZIndex(tile, rank), rank tăng dần đã đẩy ô nhỏ nhất lên lớp trên cùng,
            // khiến thứ tự lớp vẽ và thứ tự trúng đích ngược hẳn nhau (vùng chồng lấn "thấy A nhấp trúng B").
            var zIndex = order.Length - rank;
            if (Panel.GetZIndex(tile) != zIndex) Panel.SetZIndex(tile, zIndex);

            if (tile.RenderTransform is ScaleTransform transform)
            {
                if (transform.ScaleX != scale) transform.ScaleX = scale;
                if (transform.ScaleY != scale) transform.ScaleY = scale;
            }
        }

    }

    /// <summary>
    /// Đặt đường phân cách vào giữa hai nhóm "ô ứng dụng" và "lối vào tích hợp sẵn".
    /// </summary>
    /// <remarks>
    /// Chỉ hiển thị khi cả hai nhóm đều có mặt. Vị trí lấy trung điểm của tâm khe hai ô liền kề thuộc hai nhóm —
    /// nằm trong tọa độ canvas giống như các ô, nên khi cuộn sẽ đi theo nội dung.
    /// </remarks>
    private void UpdateGroupDivider(TileGeometry[] geometry)
    {
        if (AppTileCount == 0 || AppTileCount >= geometry.Length)
        {
            _groupDivider.Visibility = Visibility.Collapsed;
            return;
        }

        var lastAppCenter = geometry[AppTileCount - 1].Center;
        var firstEntryCenter = geometry[AppTileCount].Center;

        Canvas.SetLeft(_groupDivider, (lastAppCenter + firstEntryCenter) / 2);
        Canvas.SetTop(_groupDivider, _iconSize * 0.15);
        _groupDivider.Height = _iconSize * 0.7;
        _groupDivider.Visibility = Visibility.Visible;

        // Đường phân cách không tham gia phân định chồng lấn khi trúng đích (nó không thể nhấp), chỉ cần đặt lớp nằm dưới các ô là được.
        Panel.SetZIndex(_groupDivider, -1);
    }

    /// <summary>
    /// Phân giải ô bị con trỏ nhấp trúng dựa trên <strong>khung hình vẽ cuối cùng</strong>; không trúng trả về -1.
    /// </summary>
    /// <param name="pointerPosition">Vị trí con trỏ dọc theo trục Dock (tọa độ canvas, DIP).</param>
    public int ResolveHit(double pointerPosition)
        => _paintedGeometry.Length == 0
            ? -1
            : DockMagnification.HitTest(_paintedGeometry, _iconSize, pointerPosition);

    /// <summary>Ảnh chụp hình học của khung hình vẽ cuối cùng (dùng cho chẩn đoán và kiểm thử).</summary>
    public IReadOnlyList<TileGeometry> PaintedGeometry => _paintedGeometry.ToArray();

    /// <summary>
    /// Trạng thái vẽ <strong>thực tế</strong> của từng ô trong khung hình vẽ cuối cùng (đọc giá trị thật trên cây hiển thị).
    /// </summary>
    /// <remarks>
    /// Cố ý đọc <c>Panel.GetZIndex</c> và <c>Canvas.GetLeft</c>, thay vì báo lại mảng đã tính toán:
    /// lỗi "thứ tự lớp vẽ và thứ tự trúng đích ngược nhau" sở dĩ lọt qua kiểm thử hồi đó chính là vì các khẳng định lúc ấy đối chiếu
    /// mảng thứ tự hình học (giá trị dẫn xuất), chứ không đối chiếu thứ tự lớp thực sự có hiệu lực. Chẩn đoán và kiểm thử phải đọc giá trị có hiệu lực.
    /// </remarks>
    public IReadOnlyList<PaintedTileState> PaintedTiles
    {
        get
        {
            var states = new List<PaintedTileState>(_tiles.Count);

            for (var index = 0; index < _tiles.Count; index++)
            {
                var tile = _tiles[index];
                var scale = tile.RenderTransform is ScaleTransform transform ? transform.ScaleX : 1.0;
                var entryId = index < _models.Count ? _models[index].EntryId : string.Empty;

                states.Add(new PaintedTileState(entryId, scale, Panel.GetZIndex(tile), Canvas.GetLeft(tile)));
            }

            return states;
        }
    }


    private double? CurrentPointerOrNull()
        => _pointerActive && !double.IsNaN(_pointerPosition) ? _pointerPosition : null;

    /// <summary>Gợi ý làm mờ dần ở hai đầu: hiển thị ở phía có thể cuộn và chưa cuộn hết.</summary>
    private void UpdateFades()
    {
        var scrollable = CanScroll;

        _leadingFade.Visibility = scrollable && _scroller.HorizontalOffset > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        var maxOffset = Math.Max(0, _scroller.ExtentWidth - _scroller.ViewportWidth);
        _trailingFade.Visibility = scrollable && _scroller.HorizontalOffset < maxOffset - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static Border CreateFade(double direction)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(direction, 0.5),
            EndPoint = new Point(1 - direction, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x88, 0x00, 0x00, 0x00), 0),
                new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 1),
            },
        };

        return new Border
        {
            Background = gradient,
            Width = 28,
            HorizontalAlignment = direction < 0.5 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
    }

    private static void Bind(FrameworkElement element, DependencyProperty property, object source, string path)
    {
        _ = element.SetBinding(property, new System.Windows.Data.Binding
        {
            Source = source,
            Path = new PropertyPath(path),
        });
    }
}

public sealed class DockTileHoverEventArgs : EventArgs
{
    public string? EntryId { get; }
    public Rect ScreenBounds { get; }

    public DockTileHoverEventArgs(string? entryId, Rect screenBounds)
    {
        EntryId = entryId;
        ScreenBounds = screenBounds;
    }
}
