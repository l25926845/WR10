using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullScreenStart;

public sealed class LayoutFile
{
    /// <summary>布局格式版本，用于配色等一次性迁移。</summary>
    public int Version { get; set; }

    public ObservableCollection<TileGroup> Groups { get; set; } = new();

    /// <summary>上一版扁平格式，仅用于兼容读取，不再写出。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TileItem>? Tiles { get; set; }
}

public sealed class TileGroup : INotifyPropertyChanged
{
    private string _name = "所有应用";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public ObservableCollection<TileItem> Tiles { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 扁平磁贴列表中的分组标题项：与 TileItem 混排在 ObservableCollection 中，
/// VirtualizingTilePanel 遇到它时整行渲染为标题，不走网格占位。
/// </summary>
public sealed class GroupHeaderItem
{
    public TileGroup Group { get; init; } = null!;
    public string GroupName => Group.Name;
}

internal static class LayoutStore
{
    /// <summary>当前版本：2 起磁贴配色换成提亮后的 Palette。</summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullScreenStart",
        "layout.json");

    public static LayoutFile? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var layout = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(FilePath), Options);
            bool needsMigrate = layout != null && layout.Version < CurrentVersion;
            var normalized = Normalize(layout);

            // 迁移过配色后立刻落盘，避免每次启动重复换算
            if (needsMigrate && normalized != null)
            {
                Save(normalized);
            }

            return normalized;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>序列化为 JSON 文本，供导出布局使用。</summary>
    public static string Serialize(LayoutFile layout) => JsonSerializer.Serialize(layout, Options);

    /// <summary>解析导入的 JSON 文本，兼容旧版扁平格式；无效内容返回 null。</summary>
    public static LayoutFile? Parse(string json)
    {
        try
        {
            return Normalize(JsonSerializer.Deserialize<LayoutFile>(json, Options));
        }
        catch
        {
            return null;
        }
    }

    private static LayoutFile? Normalize(LayoutFile? layout)
    {
        if (layout == null)
        {
            return null;
        }

        // 兼容上一版扁平格式：按 Group 字段还原分组
        if (layout.Groups.Count == 0 && layout.Tiles is { Count: > 0 })
        {
            var groups = new Dictionary<string, TileGroup>(StringComparer.Ordinal);
            foreach (var tile in layout.Tiles)
            {
                string name = string.IsNullOrEmpty(tile.Group) ? "所有应用" : tile.Group;
                if (!groups.TryGetValue(name, out var group))
                {
                    group = new TileGroup { Name = name };
                    groups[name] = group;
                    layout.Groups.Add(group);
                }

                group.Tiles.Add(tile);
            }
        }

        layout.Tiles = null;

        // 旧版布局的配色在纯黑底上偏暗，统一映射到提亮后的新调色板
        if (layout.Version < CurrentVersion)
        {
            foreach (var tile in layout.Groups.SelectMany(g => g.Tiles))
            {
                tile.Color = TileItem.MigrateColor(tile.Color);
            }

            layout.Version = CurrentVersion;
        }

        int count = 0;
        foreach (var group in layout.Groups)
        {
            foreach (var tile in group.Tiles)
            {
                if (string.IsNullOrEmpty(tile.Id))
                {
                    tile.Id = tile.Target;
                }
                count++;
            }
        }

        return count > 0 ? layout : null;
    }

    public static void Save(LayoutFile layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, Options));
        }
        catch
        {
            // 布局保存失败不阻塞菜单使用
        }
    }
}
