using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace FullScreenStart;

/// <summary>「所有应用」列表中的一行：名称 + 启动目标 + 按需加载的小图标。</summary>
public sealed class AllAppItem : INotifyPropertyChanged
{
    public AllAppItem(string name, string target)
    {
        Name = name;
        Target = target;
    }

    public string Name { get; }
    public string Target { get; }

    private ImageSource? _icon;

    /// <summary>是否已经尝试过图标解析，避免滚动布局时重复排队。</summary>
    public bool IconResolved { get; set; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (value is Freezable freezable && freezable.CanFreeze)
            {
                freezable.Freeze();
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Name;
}

/// <summary>按首字母分好组的应用集合。</summary>
public sealed class AllAppsSection
{
    public AllAppsSection(string key, List<AllAppItem> apps)
    {
        Key = key;
        Apps = apps;
    }

    public string Key { get; }
    public List<AllAppItem> Apps { get; }
}

internal static class AllAppsBuilder
{
    public static List<AllAppsSection> Build()
    {
        var items = AppCatalog.Enumerate()
            .Select(e => new AllAppItem(e.Name, e.Target))
            .ToList();

        return items
            .GroupBy(a => PinyinInitial.Of(a.Name))
            .OrderBy(g => g.Key == "#" ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AllAppsSection(
                g.Key,
                g.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList()))
            .ToList();
    }
}

/// <summary>
/// 取名称首字符的字母分组。汉字按 GB2312 一级汉字拼音排序的区位码区间换算，
/// 多音字与二级汉字会落到 # 组。
/// </summary>
internal static class PinyinInitial
{
    private static readonly Encoding? Gbk = CreateGbk();

    // GB2312 一级汉字各拼音首字母的区位码起点
    private const string Letters = "abcdefghjklmnopqrstwxyz";

    private static readonly int[] Boundaries =
    {
        1601, 1637, 1833, 2078, 2274, 2302, 2433, 2594, 2787, 3106, 3212,
        3472, 3635, 3722, 3730, 3858, 4027, 4086, 4390, 4558, 4684, 4925, 5249
    };

    private static Encoding? CreateGbk()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936);
        }
        catch
        {
            return null;
        }
    }

    public static string Of(string name)
    {
        string trimmed = name.TrimStart();
        if (trimmed.Length == 0)
        {
            return "#";
        }

        char c = trimmed[0];
        if (c >= 'a' && c <= 'z')
        {
            return ((char)(c - 32)).ToString();
        }

        if (c >= 'A' && c <= 'Z')
        {
            return c.ToString();
        }

        if (c >= 0x4E00 && c <= 0x9FFF)
        {
            return HanInitial(c);
        }

        return "#";
    }

    /// <summary>把名称逐字转成首字母串，供拼音首字母搜索使用（如「记事本」→ JSB）。</summary>
    public static string InitialsOf(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            string one = Of(c.ToString());
            if (one.Length == 1 && one != "#")
            {
                builder.Append(one);
            }
        }

        return builder.ToString();
    }

    private static string HanInitial(char c)
    {
        if (Gbk == null)
        {
            return "#";
        }

        byte[] bytes;
        try
        {
            bytes = Gbk.GetBytes(c.ToString());
        }
        catch
        {
            return "#";
        }

        if (bytes.Length != 2)
        {
            return "#";
        }

        int code = (bytes[0] - 0xA0) * 100 + (bytes[1] - 0xA0);
        if (code is < 1601 or > 5590)
        {
            return "#";
        }

        for (int i = Boundaries.Length - 1; i >= 0; i--)
        {
            if (code >= Boundaries[i])
            {
                return char.ToUpperInvariant(Letters[i]).ToString();
            }
        }

        return "#";
    }
}

/// <summary>应用搜索匹配：名称包含、拼音首字母、常用应用别名与全拼。</summary>
internal static class AppSearch
{
    // 常用应用的全拼与别名，弥补没有完整拼音表时全拼搜索的缺口
    private static readonly (string Keyword, string Extra)[] Aliases =
    {
        ("记事本", "notepad jishiben jsb"),
        ("计算器", "calc calculator jisuanqi jsq"),
        ("画图", "mspaint paint huatu ht"),
        ("资源管理器", "explorer ziyuanguanliqi zyglq"),
        ("命令提示符", "cmd mingling tishifu mltsf"),
        ("终端", "terminal zhongduan zd wt"),
        ("任务管理器", "taskmgr renwu guanliqi rwglq"),
        ("控制面板", "control kongzhi mianban kzmb"),
        ("任务计划", "taskschd renwu jihua rwjh"),
        ("设置", "settings shezhi sz"),
        ("截图", "screenshot jietu jt snipping"),
        ("日历", "calendar rili rl"),
        ("时钟", "clock shizhong sz"),
        ("邮件", "mail youjian yj"),
        ("照片", "photos zhaopian zp"),
        ("相机", "camera xiangji xj"),
        ("音乐", "music yinyue yy"),
        ("商店", "store shangdian sd"),
        ("安全中心", "security anquan aq"),
        ("应用商店", "store shangdian sd"),
        ("远程桌面", "mstsc yuancheng zhuomian yczm"),
        ("磁盘管理", "diskmgmt cipan guanli cpgl"),
        ("设备管理器", "devmgmt shebei guanliqi sbglq"),
        ("服务", "services fuwu fw"),
        ("注册表", "regedit zhu ce biao zcb"),
    };

    public static bool Matches(string name, string query)
    {
        if (name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        // 拼音首字母：jsb / vs 这类短串直接匹配首字母串
        string initials = PinyinInitial.InitialsOf(name);
        if (initials.Length > 0 &&
            initials.Contains(query.ToUpperInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var (keyword, extra) in Aliases)
        {
            if (name.Contains(keyword, StringComparison.CurrentCulture) &&
                extra.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
