using System.Runtime.CompilerServices;
using System.Text;
using Glassquid.Core.Contracts;
using Glassquid.Core.Models;

namespace Glassquid.AppServices;

/// <summary>Thực hiện tìm kiếm chỉ đọc và trả về kết quả ổn định trên thư viện ứng dụng dùng chung.</summary>
public sealed class SearchService : ISearchService
{
    private readonly Func<IEnumerable<AppEntry>> _entries;
    private readonly Func<LauncherLayout> _layout;
    private readonly ConditionalWeakTable<AppEntry, EntrySearchData> _indexCache = new();
    private readonly object _layoutLock = new();
    private LauncherLayout? _cachedLayout;
    private Dictionary<string, int>? _cachedPositions;

    private sealed class EntrySearchData
    {
        public required string DisplayName { get; init; }
        public required string NormalizedName { get; init; }
        public required string PinyinFull { get; init; }
        public required string PinyinInitials { get; init; }
        public required string[] NormalizedAliases { get; init; }
    }

    public SearchService(Func<IEnumerable<AppEntry>> entries, Func<LauncherLayout> layout)
    {
        _entries = entries;
        _layout = layout;
        Warmup();
    }

    /// <summary>
    /// Tiền xử lý index tìm kiếm cho danh sách ứng dụng hiện có.
    /// </summary>
    public void Warmup()
    {
        try
        {
            var items = _entries()?.ToArray();
            if (items is null) return;
            foreach (var item in items)
            {
                if (item is { IsUsable: true })
                {
                    GetSearchData(item);
                }
            }
        }
        catch
        {
            // Bỏ qua nếu dữ liệu chưa sẵn sàng ở thời điểm khởi tạo
        }
    }

    public IReadOnlyList<SearchHit> SearchApps(string query, int limit)
    {
        var needle = Normalize(query);
        if (needle.Length == 0 || limit <= 0) return [];

        var positions = GetPositions(_layout());
        var snapshot = _entries()?.ToArray() ?? [];

        return snapshot.Where(item => item is { IsUsable: true })
            .Select(item => (item, kind: Match(GetSearchData(item), needle)))
            .Where(pair => pair.kind is not null)
            .OrderBy(pair => pair.kind)
            .ThenBy(pair => positions.GetValueOrDefault(pair.item.Id, int.MaxValue))
            .ThenBy(pair => pair.item.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => new SearchHit { Item = pair.item, MatchKind = pair.kind!.Value, Score = 100 - (int)pair.kind.Value })
            .ToList();
    }

    private EntrySearchData GetSearchData(AppEntry item)
    {
        var currentDisplayName = item.DisplayName ?? string.Empty;
        var currentAliases = item.SearchAliases;

        if (_indexCache.TryGetValue(item, out var cached))
        {
            if (string.Equals(cached.DisplayName, currentDisplayName, StringComparison.Ordinal)
                && AliasesEqual(cached.NormalizedAliases, currentAliases))
            {
                return cached;
            }
        }

        var normalizedName = Normalize(currentDisplayName);
        var (full, initials) = OfflinePinyin.Index(currentDisplayName);
        var normalizedAliases = (currentAliases ?? [])
            .Select(Normalize)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var data = new EntrySearchData
        {
            DisplayName = currentDisplayName,
            NormalizedName = normalizedName,
            PinyinFull = full,
            PinyinInitials = initials,
            NormalizedAliases = normalizedAliases
        };

        _indexCache.AddOrUpdate(item, data);

        // Đồng bộ thuộc tính Pinyin của AppEntry nếu chưa có
        item.PinyinIndex ??= full;
        item.PinyinInitials ??= initials;

        return data;
    }

    private static bool AliasesEqual(string[] cachedAliases, List<string>? currentAliases)
    {
        if (currentAliases is null || currentAliases.Count == 0)
            return cachedAliases.Length == 0;
        if (cachedAliases.Length != currentAliases.Count)
            return false;
        for (var i = 0; i < cachedAliases.Length; i++)
        {
            if (!string.Equals(cachedAliases[i], Normalize(currentAliases[i]), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private Dictionary<string, int> GetPositions(LauncherLayout layout)
    {
        lock (_layoutLock)
        {
            if (_cachedPositions is not null && ReferenceEquals(layout, _cachedLayout))
                return _cachedPositions;

            var ordered = LauncherLayoutRules.AppIds(layout);
            var positions = new Dictionary<string, int>(ordered.Count, StringComparer.Ordinal);
            for (var i = 0; i < ordered.Count; i++)
            {
                positions.TryAdd(ordered[i], i);
            }

            _cachedLayout = layout;
            _cachedPositions = positions;
            return positions;
        }
    }

    private static SearchMatchKind? Match(EntrySearchData data, string needle)
    {
        var name = data.NormalizedName;
        if (name == needle) return SearchMatchKind.ExactName;
        if (name.StartsWith(needle, StringComparison.Ordinal)) return SearchMatchKind.NamePrefix;
        if (data.PinyinFull.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.Pinyin;
        if (data.PinyinInitials.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.PinyinInitials;
        if (name.Contains(needle, StringComparison.Ordinal)) return SearchMatchKind.NameContains;
        if (data.NormalizedAliases.Any(alias => alias.Contains(needle, StringComparison.Ordinal)))
            return SearchMatchKind.Alias;
        return null;
    }

    private static string Normalize(string? text)
        => (text ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
