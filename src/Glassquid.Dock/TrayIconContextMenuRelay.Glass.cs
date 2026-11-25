using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Globalization;

namespace Glassquid.Dock;

internal static partial class TrayIconContextMenuRelay
{
    private static NativeTrayGlassWindow? _nativeTrayGlass;
    private static readonly NativeTrayLayoutCache NativeTrayLayouts = new();
    private static byte? _nativeTrayInputAlpha;
    private static readonly object NativeTrayArtworkGate = new();
    private static IReadOnlyList<TrayIconItem>? _nativeTrayArtwork;
    private static long _nativeTrayArtworkLoadedAt;
    private static Task? _nativeTrayArtworkRefresh;

    internal static void WarmNativeTrayArtwork()
    {
        lock (NativeTrayArtworkGate)
        {
            if (_nativeTrayArtworkRefresh is { IsCompleted: false }
                || (_nativeTrayArtwork is not null
                    && System.Diagnostics.Stopwatch.GetElapsedTime(_nativeTrayArtworkLoadedAt).TotalSeconds < 15)) return;
            _nativeTrayArtworkRefresh = Task.Run(() =>
            {
                try { _ = LoadNativeTrayArtwork(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log("Tray artwork prefetch failed: " + ex.Message);
                }
            });
        }
    }

    private static IReadOnlyList<TrayIconItem> LoadNativeTrayArtwork()
    {
        // RegistryTrayReader freezes its image sources before returning; artwork
        // can be loaded off the dispatcher and reused safely by the visual window.
        var artwork = RegistryTrayReader.ReadRunningHiddenIcons(artworkOnly: true);
        lock (NativeTrayArtworkGate)
        {
            _nativeTrayArtwork = artwork;
            _nativeTrayArtworkLoadedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        return artwork;
    }

    private static IReadOnlyList<TrayIconItem> GetNativeTrayArtwork()
    {
        IReadOnlyList<TrayIconItem>? artwork;
        lock (NativeTrayArtworkGate) artwork = _nativeTrayArtwork;
        WarmNativeTrayArtwork();
        return artwork ?? LoadNativeTrayArtwork();
    }

    private static bool TryCreateNativeTrayGlass(SystemTrayBarWindow dock, nint hwnd, NativeRect bounds,
        out bool usedSettledLayout, out string layoutSignature, bool refresh = false)
    {
        usedSettledLayout = false;
        layoutSignature = string.Empty;
        if (refresh && _nativeTrayGlass is null) return false;
        if (!refresh) CloseNativeTrayGlass();
        try
        {
            var overflow = AutomationElement.FromHandle(hwnd);
            var elements = overflow.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, NotifyItemAutomationId));
            var slots = new List<(string Name, Rect Cell, Rect Glyph)>();
            // UIA rectangles are physical pixels. Convert them using the WPF
            // renderer's DPI, which can differ from Explorer's HWND awareness.
            var renderWindow = _nativeTrayGlass?.NativeHandle ?? new WindowInteropHelper(dock).Handle;
            var scale = Math.Max(1, GetDpiForWindow(renderWindow)) / 96d;
            foreach (AutomationElement element in elements)
            {
                var cell = element.Current.BoundingRectangle;
                if (cell.IsEmpty || cell.Width <= 0 || cell.Height <= 0
                    || slots.Any(slot => slot.Cell == cell)) continue;
                var name = (element.Current.Name + " " + element.Current.HelpText).Trim();
                var glyph = element.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image))?.Current.BoundingRectangle;
                var iconSize = 16 * scale;
                var glyphBounds = glyph is Rect imageBounds && !imageBounds.IsEmpty
                    && imageBounds.Width > 0 && imageBounds.Height > 0
                    && imageBounds.Width <= 24 * scale && imageBounds.Height <= 24 * scale
                    ? imageBounds
                    : new Rect(cell.X + (cell.Width - iconSize) / 2, cell.Y + (cell.Height - iconSize) / 2, iconSize, iconSize);
                slots.Add((name, cell, glyphBounds));
            }
            if (slots.Count == 0) return false;

            var windowBox = new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            layoutSignature = CreateNativeTrayLayoutSignature(slots, windowBox);
            if (refresh) NativeTrayLayouts.Remember(slots, windowBox, scale);
            else if (NativeTrayLayouts.TryGet(slots, windowBox, scale, out var settledLayout))
            {
                slots = settledLayout;
                usedSettledLayout = true;
            }

            var minCellX = slots.Min(s => s.Cell.Left);
            var maxCellX = slots.Max(s => s.Cell.Right);
            var minCellY = slots.Min(s => s.Cell.Top);
            var maxCellY = slots.Max(s => s.Cell.Bottom);
            var contentWidth = Math.Max(1, maxCellX - minCellX);
            var contentHeight = Math.Max(1, maxCellY - minCellY);

            var candidates = new List<(string Name, ImageSource Image, NativeRect? Bounds)>();
            foreach (var icon in Tray.ManagedShellNotificationArea.Instance.AllIcons)
            {
                if (icon.Icon is null) continue;
                NativeRect? iconBounds = null;
                if (icon.GUID != Guid.Empty && TryGetIconRectangle(0, 0, icon.GUID, out var guidBounds)) iconBounds = guidBounds;
                else if (icon.HWnd != 0 && TryGetIconRectangle(icon.HWnd, icon.UID, Guid.Empty, out var uidBounds)) iconBounds = uidBounds;
                candidates.Add((icon.Title ?? string.Empty, icon.Icon, iconBounds));
            }

            // Windows still owns the ordering, positions, tooltips and mouse callbacks.
            // Registry snapshots supply only artwork absent from ManagedShell's live catalog.
            var icons = MatchNativeTrayGlassIcons(slots, candidates, minCellX, minCellY, scale);
            if (icons is null)
            {
                void AddArtwork(IReadOnlyList<TrayIconItem> artwork, bool resolveBounds = false)
                {
                    var aliases = new List<(string Name, ImageSource Image, NativeRect? Bounds)>();
                    foreach (var icon in artwork)
                    {
                        if (icon.Icon is null) continue;
                        var iconBounds = resolveBounds ? TryGetIconRectangle(icon) : null;
                        candidates.Add((icon.ToolTip, icon.Icon, iconBounds));
                        foreach (var alias in BuildStrongExpectedNames(icon))
                            if (!alias.Equals(icon.ToolTip, StringComparison.OrdinalIgnoreCase))
                                aliases.Add((alias, icon.Icon, iconBounds));
                    }
                    // An app's real tooltip outranks another app's executable-name
                    // alias (for example Codex's ChatGPT.exe vs the ChatGPT app).
                    candidates.AddRange(aliases);
                }
                AddArtwork(GetNativeTrayArtwork());
                icons = MatchNativeTrayGlassIcons(slots, candidates, minCellX, minCellY, scale);
                if (icons is null)
                {
                    // A newly registered tray app invalidates the artwork cache.
                    AddArtwork(LoadNativeTrayArtwork(), resolveBounds: true);
                    icons = MatchNativeTrayGlassIcons(slots, candidates, minCellX, minCellY, scale);
                }
            }
            if (icons is null)
            {
                Log("Native tray glass: retained Explorer artwork because an icon could not be mapped safely.");
                return false;
            }

            const double padX = 16;
            const double padY = 14;
            var padXPhysical = (int)Math.Round(padX * scale);
            var padYPhysical = (int)Math.Round(padY * scale);

            var glassPhysicalLeft = (int)Math.Round(minCellX) - padXPhysical;
            var glassPhysicalTop = (int)Math.Round(minCellY) - padYPhysical;
            var glassPhysicalWidth = (int)Math.Round(contentWidth) + padXPhysical * 2;
            var glassPhysicalHeight = (int)Math.Round(contentHeight) + padYPhysical * 2;

            var offsetXFromHwnd = glassPhysicalLeft - bounds.Left;
            var offsetYFromHwnd = glassPhysicalTop - bounds.Top;

            var glass = _nativeTrayGlass;
            if (glass is null)
            {
                glass = new NativeTrayGlassWindow(dock,
                    glassPhysicalWidth / scale, glassPhysicalHeight / scale,
                    icons, padX, padY, offsetXFromHwnd, offsetYFromHwnd)
                {
                    Left = glassPhysicalLeft / scale,
                    Top = glassPhysicalTop / scale,
                };
                _nativeTrayGlass = glass;
                glass.Show();
            }
            else glass.UpdatePresentation(glassPhysicalWidth / scale, glassPhysicalHeight / scale,
                icons, padX, padY, offsetXFromHwnd, offsetYFromHwnd);
            glass.SetNativeInputReady(false);
            var glassHwnd = glass.NativeHandle;
            var hostHeight = glassPhysicalHeight + (int)Math.Ceiling(NativeTrayGlassWindow.MotionInset * scale);
            if (!GetWindowRect(glassHwnd, out var currentHost) || currentHost.Left != glassPhysicalLeft
                || currentHost.Top != glassPhysicalTop || currentHost.Width != glassPhysicalWidth || currentHost.Height != hostHeight)
                _ = SetWindowPos(glassHwnd, new nint(-1), glassPhysicalLeft, glassPhysicalTop,
                    glassPhysicalWidth, hostHeight, SWP_NOACTIVATE);

            // Only the visual glass is clipped to rounded corners. Cropping the
            // native HWND changes its hit region and can discard real icon cells.
            _ = SetWindowRgn(hwnd, 0, true);
            glass.UpdateLayout();

            // Keep Explorer above the visual-only glass. Alpha 1 preserves native hit
            // testing, including application context menus, without its opaque background.
            _ = SetWindowPos(hwnd, new nint(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            Log($"Native tray glass: {(refresh ? "aligned" : "prepared")} {icons.Count} Explorer cells; nativeDpi={GetDpiForWindow(hwnd)}, glassDpi={GetDpiForWindow(glassHwnd)}.");
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("Native tray glass could not be prepared: " + ex.Message);
            CloseNativeTrayGlass();
            return false;
        }
    }

    private static string CreateNativeTrayLayoutSignature(
        IReadOnlyList<(string Name, Rect Cell, Rect Glyph)> slots, Rect nativeWindow)
    {
        static string Number(double value) => Math.Round(value, 1).ToString("F1", CultureInfo.InvariantCulture);
        return string.Join(";", slots.OrderBy(slot => slot.Cell.Y).ThenBy(slot => slot.Cell.X).Select(slot =>
            string.Join(",", NativeTrayArtworkMatcher.NormalizeName(slot.Name),
                Number(slot.Cell.Left - nativeWindow.Left), Number(slot.Cell.Top - nativeWindow.Top),
                Number(slot.Cell.Width), Number(slot.Cell.Height),
                Number(slot.Glyph.Left - nativeWindow.Left), Number(slot.Glyph.Top - nativeWindow.Top),
                Number(slot.Glyph.Width), Number(slot.Glyph.Height))));
    }

    internal static void SetNativeGlassInputTransparent(nint hwnd, bool transparent)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | 0x08000000; // WS_EX_NOACTIVATE
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, transparent ? style | 0x20 : style & ~0x20);
    }

    private static bool EnableNativeTrayInput(nint hwnd)
    {
        if (_nativeTrayGlass is not { } glass) return false;
        // Activate the real input surface before letting the visual glass pass
        // events through. There must never be a frame with two transparent layers.
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~0x20);
        if (!SetLayeredWindowAttributes(hwnd, _stealthOverflowOriginalColorKey, 1,
                _stealthOverflowOriginalLayerFlags | LWA_ALPHA)) return false;
        _nativeTrayInputAlpha = 1;
        glass.SetNativeInputReady(true);
        return true;
    }

    private static List<NativeTrayGlassIcon>? MatchNativeTrayGlassIcons(
        List<(string Name, Rect Cell, Rect Glyph)> slots,
        List<(string Name, ImageSource Image, NativeRect? Bounds)> candidates,
        double contentOriginX, double contentOriginY, double scale)
    {
        var result = new List<NativeTrayGlassIcon>();
        var identities = candidates.Select(candidate =>
            (candidate.Name, candidate.Bounds is { } bounds
                ? (Rect?)new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)
                : null)).ToArray();
        foreach (var slot in slots)
        {
            var index = NativeTrayArtworkMatcher.FindMatch(slot.Name, slot.Cell, identities);
            if (index < 0)
            {
                Log($"Native tray glass: no artwork for Explorer slot '{slot.Name}' (identity '{NormalizeNativeTrayArtworkName(slot.Name)}'). Catalog: {string.Join(" | ", candidates.Select(candidate => candidate.Name))}");
                return null;
            }
            var glyph = slot.Glyph;
            result.Add(new NativeTrayGlassIcon(candidates[index].Image,
                new Rect((glyph.Left - contentOriginX) / scale, (glyph.Top - contentOriginY) / scale,
                    glyph.Width / scale, glyph.Height / scale)));
        }
        return result;
    }

    private static string NormalizeNativeTrayArtworkName(string name) => NativeTrayArtworkMatcher.NormalizeName(name);

    private static void UpdateNativeTrayGlassFrame(nint hwnd, double opacity, double offset)
    {
        if (_nativeTrayGlass is not { } glass) return;
        if (!IsWindowVisible(hwnd)) { CloseNativeTrayGlass(); return; }
        glass.SetMotionFrame(offset, opacity);
    }

    private static void CloseNativeTrayGlass()
    {
        var glass = _nativeTrayGlass;
        _nativeTrayGlass = null;
        _nativeTrayInputAlpha = null;
        if (_styledOverflowHwnd != 0 && IsWindow(_styledOverflowHwnd))
        {
            _ = SetWindowRgn(_styledOverflowHwnd, 0, true);
        }
        if (glass is not null && _stealthOverflowHwnd != 0 && IsWindow(_stealthOverflowHwnd))
            _ = SetWindowPos(_stealthOverflowHwnd,
                new nint((_stealthOverflowOriginalStyle & 0x00000008) != 0 ? -1 : -2),
                0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        glass?.Close();
        if (glass is not null)
        {
            // Refresh after a native tray action (for example UniKey changing
            // modes), while the popup is closed rather than on the next click.
            lock (NativeTrayArtworkGate) _nativeTrayArtworkLoadedAt = 0;
            WarmNativeTrayArtwork();
        }
    }
}
