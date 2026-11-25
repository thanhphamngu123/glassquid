using System.Windows;

namespace Glassquid.Dock;

/// <summary>Reuses settled native cells while Explorer animates its next entrance.</summary>
internal sealed class NativeTrayLayoutCache
{
    private (string Identity, Rect Cell, Rect Glyph)[]? _relativeLayout;
    private Size _windowSize;
    private double _scale;

    internal void Clear() => _relativeLayout = null;

    internal bool TryGet(IReadOnlyList<(string Name, Rect Cell, Rect Glyph)> observed,
        Rect nativeWindow, double scale, out List<(string Name, Rect Cell, Rect Glyph)> cachedLayout)
    {
        ArgumentNullException.ThrowIfNull(observed);
        cachedLayout = [];
        if (_relativeLayout is not { } saved || observed.Count != saved.Length
            || !IsValid(nativeWindow) || nativeWindow.Size != _windowSize || scale != _scale
            || !double.IsFinite(scale) || scale <= 0 || observed.Any(slot => !IsValid(slot.Cell) || !IsValid(slot.Glyph)))
            return false;

        var ordered = InLayoutOrder(observed);
        for (var i = 0; i < ordered.Length; i++)
            if (!string.Equals(NativeTrayArtworkMatcher.NormalizeName(ordered[i].Name),
                    saved[i].Identity, StringComparison.OrdinalIgnoreCase)) return false;

        cachedLayout = new List<(string Name, Rect Cell, Rect Glyph)>(saved.Length);
        for (var i = 0; i < saved.Length; i++)
            cachedLayout.Add((ordered[i].Name,
                Offset(saved[i].Cell, nativeWindow.X, nativeWindow.Y),
                Offset(saved[i].Glyph, nativeWindow.X, nativeWindow.Y)));
        return true;
    }

    internal void Remember(IReadOnlyList<(string Name, Rect Cell, Rect Glyph)> observed,
        Rect nativeWindow, double scale)
    {
        ArgumentNullException.ThrowIfNull(observed);
        _relativeLayout = null;
        if (observed.Count == 0 || !IsValid(nativeWindow) || !double.IsFinite(scale) || scale <= 0
            || observed.Any(slot => !IsValid(slot.Cell) || !IsValid(slot.Glyph))) return;

        var ordered = InLayoutOrder(observed);
        var identities = ordered.Select(slot => NativeTrayArtworkMatcher.NormalizeName(slot.Name)).ToArray();
        // Anonymous or duplicate labels cannot prove that a remembered cell still
        // represents the same icon after an app changes the notification area.
        if (identities.Any(string.IsNullOrWhiteSpace)
            || identities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length) return;

        _relativeLayout = ordered.Select((slot, index) => (identities[index],
            Offset(slot.Cell, -nativeWindow.X, -nativeWindow.Y),
            Offset(slot.Glyph, -nativeWindow.X, -nativeWindow.Y))).ToArray();
        _windowSize = nativeWindow.Size;
        _scale = scale;
    }

    private static (string Name, Rect Cell, Rect Glyph)[] InLayoutOrder(
        IReadOnlyList<(string Name, Rect Cell, Rect Glyph)> slots) =>
        slots.OrderBy(slot => slot.Cell.Y).ThenBy(slot => slot.Cell.X).ToArray();

    private static Rect Offset(Rect bounds, double x, double y) =>
        new(bounds.X + x, bounds.Y + y, bounds.Width, bounds.Height);

    private static bool IsValid(Rect bounds) => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
        && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
}
