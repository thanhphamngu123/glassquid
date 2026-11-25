using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glassquid.Dock;

/// <summary>Describes one quick-action tile's position and size inside the 4-column control-center grid.</summary>
internal sealed class ControlTileLayout
{
    /// <summary>Unique key used to look up the tile (e.g. "wifi", "bluetooth", "nightlight").</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>0-based grid row.</summary>
    public int Row { get; set; }

    /// <summary>0-based grid column.</summary>
    public int Column { get; set; }

    /// <summary>How many columns this tile spans (1 = normal circle, 2 = wide tile, 4 = full-width).</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>How many rows this tile spans (1 = single, 2 = tall/media player).</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>Creates a deep clone of this layout entry.</summary>
    public ControlTileLayout Clone() => new()
    {
        Key = Key,
        Row = Row,
        Column = Column,
        ColumnSpan = ColumnSpan,
        RowSpan = RowSpan,
    };

    // ────────────────────────────────────────────────
    //  Persistence
    // ────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string GetLayoutFilePath()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Glassquid", "ControlCenter");
        Directory.CreateDirectory(appDataDir);
        return Path.Combine(appDataDir, "tile_layout.json");
    }

    /// <summary>Saves the tile layout list to a JSON file in LocalAppData.</summary>
    public static void SaveLayout(IReadOnlyList<ControlTileLayout> tiles)
    {
        try
        {
            var json = JsonSerializer.Serialize(tiles, _jsonOptions);
            File.WriteAllText(GetLayoutFilePath(), json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid ControlCenter] Failed to save layout: {ex.Message}");
        }
    }

    /// <summary>Loads the tile layout list from the JSON file, or null if it doesn't exist / is corrupt.</summary>
    public static List<ControlTileLayout>? LoadLayout()
    {
        try
        {
            var path = GetLayoutFilePath();
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ControlTileLayout>>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid ControlCenter] Failed to load layout: {ex.Message}");
            return null;
        }
    }

    // ────────────────────────────────────────────────
    //  Default layout (matches the original hardcoded XAML)
    // ────────────────────────────────────────────────

    /// <summary>Returns the factory-default tile layout matching the original XAML design.</summary>
    public static List<ControlTileLayout> CreateDefaultLayout() => new()
    {
        // Row 0: Wi-Fi (wide 2-col) + Media player (2x2 at col 2–3, row 0–1)
        new() { Key = "wifi",          Row = 0, Column = 0, ColumnSpan = 2, RowSpan = 1 },
        new() { Key = "media",         Row = 0, Column = 2, ColumnSpan = 2, RowSpan = 2 },

        // Row 1: Bluetooth (1x1), Airplane (1x1)  — media player occupies col 2-3
        new() { Key = "bluetooth",     Row = 1, Column = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "airplane",      Row = 1, Column = 1, ColumnSpan = 1, RowSpan = 1 },

        // Row 2: Display projection, (empty col 1), Night light, Energy saver
        new() { Key = "projection",    Row = 2, Column = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "nightlight",    Row = 2, Column = 2, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "energysaver",   Row = 2, Column = 3, ColumnSpan = 1, RowSpan = 1 },

        // Row 3: Brightness slider (full-width, draggable row position, non-resizable)
        new() { Key = "brightness",    Row = 3, Column = 0, ColumnSpan = 4, RowSpan = 1 },
        // Row 4: Volume slider (full-width, draggable row position, non-resizable)
        new() { Key = "volume",        Row = 4, Column = 0, ColumnSpan = 4, RowSpan = 1 },

        // Row 5: Bottom shortcut buttons
        new() { Key = "battery",       Row = 5, Column = 0, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "calculator",    Row = 5, Column = 1, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "clock",         Row = 5, Column = 2, ColumnSpan = 1, RowSpan = 1 },
        new() { Key = "camera",        Row = 5, Column = 3, ColumnSpan = 1, RowSpan = 1 },
    };
}
