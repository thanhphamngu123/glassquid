using System.Windows;

namespace Glassquid.Dock;

/// <summary>Matches visual artwork to Explorer's identified tray cells.</summary>
internal static class NativeTrayArtworkMatcher
{
    internal static int FindMatch(string? nativeName, Rect nativeCell,
        IReadOnlyList<(string Name, Rect? Bounds)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var exactName = CollapseWhitespace(nativeName);
        var normalizedName = NormalizeName(nativeName);
        if (HasIdentity(normalizedName))
        {
            for (var i = 0; i < candidates.Count; i++)
                if (string.Equals(exactName, CollapseWhitespace(candidates[i].Name), StringComparison.OrdinalIgnoreCase))
                    return i;

            for (var i = 0; i < candidates.Count; i++)
                if (string.Equals(normalizedName, NormalizeName(candidates[i].Name), StringComparison.OrdinalIgnoreCase))
                    return i;

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidateName = NormalizeName(candidates[i].Name);
                if (IsStrongPartialIdentity(normalizedName, candidateName)) return i;
            }
        }

        // Shell_NotifyIconGetRect can retain an older icon's slot. Geometry is
        // only a fallback for missing identity, never evidence against a name.
        var geometricMatch = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (HasIdentity(normalizedName) && HasIdentity(NormalizeName(candidates[i].Name))) continue;
            if (candidates[i].Bounds is not { IsEmpty: false } bounds
                || !nativeCell.Contains(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))) continue;
            if (geometricMatch >= 0) return -1;
            geometricMatch = i;
        }
        return geometricMatch;
    }

    internal static string NormalizeName(string? name)
    {
        var normalized = CollapseWhitespace(name);
        foreach (var separator in new[] { " - ", " — ", " – " })
        {
            var index = normalized.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0) normalized = normalized[..index];
        }
        foreach (var suffix in new[] { " notification icon", " tray icon" })
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^suffix.Length];
        return normalized.Trim();
    }

    private static string CollapseWhitespace(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool HasIdentity(string name) => name.Any(char.IsLetterOrDigit);

    private static bool IsStrongPartialIdentity(string left, string right)
    {
        if (left.Length < 5 || right.Length < 5) return false;
        return ContainsWholeIdentity(left, right) || ContainsWholeIdentity(right, left);
    }

    private static bool ContainsWholeIdentity(string text, string identity)
    {
        var offset = 0;
        while (offset <= text.Length - identity.Length)
        {
            var index = text.IndexOf(identity, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + identity.Length;
            if ((index == 0 || !char.IsLetterOrDigit(text[index - 1]))
                && (end == text.Length || !char.IsLetterOrDigit(text[end]))) return true;
            offset = index + 1;
        }
        return false;
    }
}
