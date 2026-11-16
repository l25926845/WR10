using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FullScreenStart;

internal sealed record AppEntry(string Name, string Target);

/// <summary>通过 shell:AppsFolder 枚举系统开始菜单中的全部应用（同时覆盖 Win32 与 UWP）。</summary>
internal static class AppCatalog
{
    // 枚举要走 Shell COM，成本较高；缓存结果供「所有应用」视图复用，F5 时强制刷新
    private static List<AppEntry>? _cache;

    public static List<AppEntry> Enumerate(bool refresh = false)
    {
        if (_cache != null && !refresh)
        {
            return _cache;
        }

        var result = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null)
        {
            return result;
        }

        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return result;
            }

            dynamic shellApp = shell;
            folder = shellApp.NameSpace("shell:AppsFolder");
            if (folder == null)
            {
                return result;
            }

            dynamic appsFolder = folder;
            items = appsFolder.Items();
            if (items == null)
            {
                return result;
            }

            dynamic appItems = items;
            int count = Convert.ToInt32(appItems.Count);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = appItems.Item(i);
                    string name = (Convert.ToString(item.Name) ?? "").Trim();
                    string target = (Convert.ToString(item.Path) ?? "").Trim();
                    if (name.Length == 0 || target.Length == 0)
                    {
                        continue;
                    }
                    result.Add(new AppEntry(name, target));
                }
                catch
                {
                    // 个别虚拟项无法读取，跳过
                }
            }
        }
        catch
        {
            // 枚举失败时由调用方回退到种子数据
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }

        _cache = result
            .GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCulture)
            .ToList();
        return _cache;
    }

    private static void Release(object? comObject)
    {
        if (comObject == null)
        {
            return;
        }
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 忽略释放异常
        }
    }
}

/// <summary>读取 Shell 项图标：文件类目标优先取系统图像列表的高清图标，其余回退 IShellItemImageFactory。</summary>
internal static class ShellIcon
{
    private const uint SIIGBF_BIGGERSIZEOK = 0x1;
    private const uint SIIGBF_ICONONLY = 0x4;

    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int SHIL_JUMBO = 0x4;
    private const int ILD_TRANSPARENT = 0x1;

    // 已知文件夹展开结果缓存，避免 376 个磁贴重复调用 Shell API
    private static readonly Dictionary<Guid, string?> _knownFolders = new();
    private static bool _defaultBrowserResolved;
    private static string? _defaultBrowserPath;

    public static ImageSource? Load(string target, int size)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        ImageSource? raw = LoadCore(target, size);

        // Shell 图像列表对部分 EXE 只给 32/48px 小图，但 EXE 内嵌了 128/256 资源
        if (raw is BitmapSource bs && Math.Max(bs.PixelWidth, bs.PixelHeight) <= 64 &&
            ResolveFilePath(target) is string filePath &&
            IsExeLike(filePath) &&
            ExtractLargestIconFromExe(filePath) is { } embedded)
        {
            raw = embedded;
        }

        return Fit(raw, size);
    }

    private static bool IsExeLike(string path)
    {
        string ext = Path.GetExtension(path);
        return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? ExtractLargestIconFromExe(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            // 0 = 默认图标索引，256 = 取最大资源图（Shell 内部会用 ICON_BIG 取最大内嵌图）
            if (ExtractIconEx(filePath, 0, out IntPtr hIconLarge, out IntPtr hIconSmall, 1) == 0 ||
                hIconLarge == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIconLarge, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(hIconLarge);
                if (hIconSmall != IntPtr.Zero) DestroyIcon(hIconSmall);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadCore(string target, int size)
    {
        // 网页快捷方式：Shell 只会给通用文档图标，改用默认浏览器图标
        if (IsWebUrl(target) && BrowserIcon(size) is { } urlIcon)
        {
            return urlIcon;
        }

        // .exe/.lnk/.msc 等文件目标：系统图像列表里有 256px 大图标，比按需缩放的 Shell 缩略图更清晰
        string? filePath = ResolveFilePath(target);
        if (filePath != null)
        {
            // 本地 .url 快捷方式同样优先用浏览器图标，避免显示成小圆点
            if (IsWebShortcut(filePath) && BrowserIcon(size) is { } shortcutIcon)
            {
                return shortcutIcon;
            }

            var fromList = LoadFromImageList(filePath, ListTypeFor(size)) ??
                           LoadFromImageList(filePath, ListTypeFor(size) == SHIL_JUMBO ? SHIL_EXTRALARGE : SHIL_JUMBO);
            if (fromList != null)
            {
                return fromList;
            }
        }

        return LoadFromShellItem(target, size);
    }

    private static bool IsWebUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static ImageSource? BrowserIcon(int size)
    {
        string? browser = DefaultBrowserPath();
        return browser == null ? null : LoadFromImageList(browser, ListTypeFor(size));
    }

    /// <summary>读取 .url 快捷方式里的 URL= 行（INI 格式）。</summary>
    private static bool IsWebShortcut(string path)
    {
        if (!path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                string text = line.Trim();
                if (text.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return IsWebUrl(text[4..].Trim());
                }
            }
        }
        catch
        {
            // 快捷方式损坏时按普通文件处理
        }

        return false;
    }

    /// <summary>
    /// 最终标准化：裁掉透明/纯色边距，再一次性缩放到 size。
    /// 无论源图比 size 大还是小，结果恒定为 size×size，
    /// 让 81 张 <=64px 的小图也放大到标准尺寸、内存稳定。
    /// </summary>
    private static ImageSource? Fit(ImageSource? source, int size)
    {
        if (source is not BitmapSource bitmap || size <= 0)
        {
            return source;
        }

        int bw = bitmap.PixelWidth;
        int bh = bitmap.PixelHeight;

        // 只裁明显的边距：内容占比不到 60% 才裁，否则保留原图结构
        int left = 0, top = 0, right = bw - 1, bottom = bh - 1;
        bitmap = CropIfNeeded(bitmap, ref left, ref top, ref right, ref bottom);

        int cw = right - left + 1;
        int ch = bottom - top + 1;

        // 源图本身就是标准尺寸、没边距、已经是 Pbgra32 → 直接 Freeze，零成本
        if (cw == size && ch == size && bitmap.Format == PixelFormats.Pbgra32 && left == 0 && top == 0)
        {
            bitmap.Freeze();
            return bitmap;
        }

        try
        {
            // RenderTargetBitmap 渲染 DrawImage 时自动做高质量缩放
            var visual = new DrawingVisual();
            using (DrawingContext ctx = visual.RenderOpen())
            {
                var rect = left == 0 && top == 0 && cw == bw && ch == bh
                    ? new Rect(0, 0, size, size)
                    : new Rect(-left * ((double)size / cw), -top * ((double)size / ch),
                               (double)bw * size / cw, (double)bh * size / ch);
                ctx.DrawImage(bitmap, rect);
            }

            var result = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }
        catch
        {
            bitmap.Freeze();
            return bitmap;
        }
    }

    /// <summary>检测边距：透明像素或与四角同色的不透明白底视为背景，算出内容包围盒。</summary>
    private static BitmapSource CropIfNeeded(BitmapSource bitmap, ref int left, ref int top, ref int right, ref int bottom)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        if (w < 16 || h < 16)
        {
            return bitmap;
        }

        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            bool hasTransparentCorner = A(pixels, 0, 0, stride) <= 12 ||
                                        A(pixels, w - 1, 0, stride) <= 12 ||
                                        A(pixels, 0, h - 1, stride) <= 12 ||
                                        A(pixels, w - 1, h - 1, stride) <= 12;
            byte bgB = pixels[0], bgG = pixels[1], bgR = pixels[2];
            bool solidBg = !hasTransparentCorner &&
                           N(pixels, 0, 0, stride, bgR, bgG, bgB) &&
                           N(pixels, w - 1, 0, stride, bgR, bgG, bgB) &&
                           N(pixels, 0, h - 1, stride, bgR, bgG, bgB) &&
                           N(pixels, w - 1, h - 1, stride, bgR, bgG, bgB);

            int l = w, t = h, r = -1, b = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    bool content = solidBg ? !N(pixels, i, bgR, bgG, bgB) : pixels[i + 3] > 12;
                    if (!content) continue;
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    if (y > b) b = y;
                }
            }

            if (r < l || b < t) return bitmap;

            double fill = (double)(r - l + 1) * (b - t + 1) / (w * h);
            if (fill >= 0.6)
            {
                // 内容够大，不裁
                return converted;
            }

            left = l; top = t; right = r; bottom = b;
            return converted;
        }
        catch
        {
            return bitmap;
        }
    }

    private static byte A(byte[] px, int x, int y, int stride) => px[y * stride + x * 4 + 3];
    private static bool N(byte[] px, int x, int y, int stride, byte r, byte g, byte b) =>
        N(px, y * stride + x * 4, r, g, b);
    private static bool N(byte[] px, int i, byte r, byte g, byte b)
    {
        if (px[i + 3] <= 12) return true;
        return Math.Abs(px[i] - b) <= 24 && Math.Abs(px[i + 1] - g) <= 24 && Math.Abs(px[i + 2] - r) <= 24;
    }

    /// <summary>小尺寸请求优先用 EXTRALARGE，避免为 24px 图标常驻一张 256px 位图。</summary>
    private static int ListTypeFor(int size) => size <= 64 ? SHIL_EXTRALARGE : SHIL_JUMBO;

    /// <summary>
    /// 把 AppsFolder 里的目标还原成真实文件路径。
    /// 部分项目形如 {FOLDERID}\相对路径，需先用 SHGetKnownFolderPath 展开。
    /// </summary>
    private static string? ResolveFilePath(string target)
    {
        if (Path.IsPathRooted(target))
        {
            return File.Exists(target) ? target : null;
        }

        if (target.Length <= 39 || target[0] != '{' || target[37] != '}' || target[38] != '\\')
        {
            return null;
        }

        if (!Guid.TryParse(target.Substring(0, 38), out Guid folderId))
        {
            return null;
        }

        string? folder = KnownFolderPath(folderId);
        if (folder == null)
        {
            return null;
        }

        string full = Path.Combine(folder, target.Substring(39));
        return File.Exists(full) ? full : null;
    }

    private static string? KnownFolderPath(Guid folderId)
    {
        if (_knownFolders.TryGetValue(folderId, out string? cached))
        {
            return cached;
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            string? path = null;
            if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out buffer) == 0 && buffer != IntPtr.Zero)
            {
                path = Marshal.PtrToStringUni(buffer);
            }

            _knownFolders[folderId] = path;
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }

    /// <summary>读取默认浏览器的可执行文件路径（http 协议的 ProgId -> DefaultIcon）。</summary>
    private static string? DefaultBrowserPath()
    {
        if (_defaultBrowserResolved)
        {
            return _defaultBrowserPath;
        }

        _defaultBrowserResolved = true;
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            string? progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId))
            {
                return null;
            }

            using var iconKey = Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon");
            string? icon = iconKey?.GetValue(null) as string;
            if (string.IsNullOrEmpty(icon))
            {
                return null;
            }

            // 形如 "C:\Program Files\...\brave.exe,0"
            int comma = icon.LastIndexOf(',');
            string path = (comma > 1 ? icon.Substring(0, comma) : icon).Trim('"');
            _defaultBrowserPath = File.Exists(path) ? path : null;
            return _defaultBrowserPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通过 SHGetFileInfo 拿到图标索引，再从系统图像列表取出对应尺寸的 HICON。</summary>
    private static ImageSource? LoadFromImageList(string path, int listType)
    {
        try
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero || info.iIcon < 0)
            {
                return null;
            }

            Guid iid = typeof(IImageList).GUID;
            if (SHGetImageList(listType, ref iid, out IImageList? list) != 0 || list == null)
            {
                return null;
            }

            IntPtr hIcon = IntPtr.Zero;
            try
            {
                if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                }

                Marshal.ReleaseComObject(list);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>AUMID（UWP）等无法按路径解析的目标，用 IShellItemImageFactory 取图。</summary>
    private static ImageSource? LoadFromShellItem(string target, int size)
    {
        string parsingName = File.Exists(target) ? target : "shell:AppsFolder\\" + target;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object item);
            var factory = (IShellItemImageFactory)item;
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(
        int iImageList,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList? list);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>从 .exe/.dll 资源里直接取最大尺寸的内嵌图标（绕开 Shell 图像列表的小图列表）。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        out IntPtr phiconLarge,
        out IntPtr phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    /// <summary>IImageList 只用到 GetIcon，声明前 8 个方法即可对齐虚表。</summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig]
        int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);

        [PreserveSig]
        int ReplaceIcon(int i, IntPtr hicon, ref int pi);

        [PreserveSig]
        int SetOverlayImage(int iImage, int iOverlay);

        [PreserveSig]
        int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

        [PreserveSig]
        int AddMasked(IntPtr hbmImage, int crMask, ref int pi);

        [PreserveSig]
        int Draw(IntPtr pimldp);

        [PreserveSig]
        int Remove(int i);

        [PreserveSig]
        int GetIcon(int i, int flags, out IntPtr picon);
    }
}
