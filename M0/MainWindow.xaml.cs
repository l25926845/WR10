using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FullScreenStart;

public partial class MainWindow : Window
{
    private readonly LayoutFile _layout;
    private readonly ObservableCollection<object> _flatItems = new();
    private readonly ObservableCollection<TileItem> _searchResults = new();
    private readonly ObservableCollection<object> _allAppRows = new();
    private readonly Queue<TileItem> _iconQueue = new();
    private readonly Queue<AllAppItem> _allIconQueue = new();
    private List<AllAppsSection> _allSections = new();
    private bool _allAppsVisible;
    private int _allAppsFocus = -1;
    private DispatcherTimer? _iconTimer;
    private VirtualizingTilePanel? _tilePanel;
    private VirtualizingTilePanel? _searchTilePanel;

    // 全局热键：注册成功才能收起后重新唤起，否则保持关闭即退出
    private const int HotkeyId = 0x5752;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkSpace = 0x20;
    private const uint VkS = 0x53;
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;

    // 拖拽状态：按下记录起点，移动超过阈值才进入拖拽，避免影响单击启动
    private Point _dragStart;
    private TileItem? _dragCandidate;
    private TileGroup? _dragSourceGroup;
    private TileItem? _selectedTile;
    private DateTime _suppressClickUntil;

    public MainWindow()
    {
        InitializeComponent();
        _layout = LayoutStore.Load() ?? BuildDefaultLayout();
        RebuildFlatItems();
        FlatTileList.ItemsSource = _flatItems;
        SearchResults.ItemsSource = _searchResults;
        AllAppsList.ItemsSource = _allAppRows;
        UserNameText.Text = Environment.UserName;
    }

    /// <summary>
    /// 把所有分组的 TileItem 扁平化到一个列表里，每组前插入一个 GroupHeaderItem。
    /// VirtualizingTilePanel 会把 GroupHeaderItem 整行渲染为分组标题。
    /// </summary>
    private void RebuildFlatItems()
    {
        // 记住当前选中的 TileItem，重建后恢复
        object? selectedBefore = FlatTileList.SelectedItem;
        TileItem? tileToReselect = selectedBefore as TileItem;

        _flatItems.Clear();
        foreach (var group in _layout.Groups)
        {
            if (group.Tiles.Count == 0) continue;
            _flatItems.Add(new GroupHeaderItem { Group = group });
            foreach (var tile in group.Tiles)
            {
                _flatItems.Add(tile);
            }
        }

        // 重建后尝试恢复选中
        if (tileToReselect != null)
        {
            FlatTileList.SelectedItem = tileToReselect;
        }
    }

    private static LayoutFile BuildDefaultLayout()
    {
        var entries = AppCatalog.Enumerate();
        if (entries.Count == 0)
        {
            entries = LoadSeedEntries();
        }

        var group = new TileGroup { Name = "所有应用" };
        foreach (var entry in entries)
        {
            group.Tiles.Add(TileItem.Create(entry));
        }

        var layout = new LayoutFile { Version = LayoutStore.CurrentVersion };
        layout.Groups.Add(group);
        LayoutStore.Save(layout);
        return layout;
    }

    private static List<AppEntry> LoadSeedEntries()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "tiles.json");
            var tiles = JsonSerializer.Deserialize<List<TileItem>>(File.ReadAllText(path)) ?? new List<TileItem>();
            return tiles.Select(t => new AppEntry(t.Name, t.Target)).ToList();
        }
        catch
        {
            return new List<AppEntry>();
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        FullScreenCover.Apply(handle);

        // 注册全局热键，Ctrl+Alt+Space 被占用时退回 Ctrl+Alt+S
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
        _hotkeyRegistered =
            RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, VkSpace) ||
            RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, VkS);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleWindow();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>热键切换：已显示则收起，否则重新拉起到鼠标所在屏幕。</summary>
    private void ToggleWindow()
    {
        if (IsVisible)
        {
            HideMenu();
            return;
        }

        FullScreenCover.Apply(new WindowInteropHelper(this).Handle);
        Show();
        Activate();
        Topmost = true;
    }

    /// <summary>收起窗口但不退出进程；热键未注册时退化为关闭，避免无法再唤起。</summary>
    private void HideMenu()
    {
        SearchBox.Text = string.Empty;

        // 收起时清掉没有磁贴的空分组，避免「新建分组」后未拖入磁贴却一直残留
        if (RemoveEmptyGroups())
        {
            RebuildFlatItems();
            LayoutStore.Save(_layout);
        }

        if (_hotkeyRegistered)
        {
            Hide();
        }
        else
        {
            Close();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FullScreenCover.Apply(new WindowInteropHelper(this).Handle);
        Activate();

        // 轻微淡入，避免全屏窗口瞬间弹出
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        // 默认选中第一个磁贴，但焦点留在搜索框：输入中文时输入法可直接在框内组合上屏
        if (FlatTileList.Items.Count > 0 && FlatTileList.Items[0] is TileItem)
        {
            FlatTileList.SelectedIndex = 0;
        }

        SearchBox.Focus();

        StartIconLoading();
    }

    /// <summary>选中项统一记录，FlatTileList 与 SearchResults 共用一套键盘操作。</summary>
    private void FlatTileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox current || current.SelectedItem is not TileItem tile)
        {
            return;
        }

        _selectedTile = tile;
        ClearSelectionExcept(current);
    }

    private void ClearSelectionExcept(ListBox current)
    {
        if (!ReferenceEquals(current, SearchResults) && SearchResults.SelectedItem != null)
        {
            SearchResults.SelectedItem = null;
        }

        if (!ReferenceEquals(current, FlatTileList) && FlatTileList.SelectedItem != null)
        {
            FlatTileList.SelectedItem = null;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>由磁贴容器反查它所属的分组：扁平化后从 TileItem DataContext 反查 _layout.Groups。</summary>
    private TileGroup? FindGroup(DependencyObject? node)
    {
        // 先拿到容器的 TileItem DataContext
        DependencyObject? current = node;
        while (current != null)
        {
            if (current is FrameworkElement { DataContext: TileItem tile })
            {
                foreach (var group in _layout.Groups)
                {
                    if (group.Tiles.Contains(tile))
                    {
                        return group;
                    }
                }

                return null;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 图标按需加载：只加载当前可视区域内的磁贴，滚动到新区域时由虚拟化面板回调补加载。
    /// 不再一次性排队全部磁贴，省掉看不见的图标位图内存（此前约 38MB）。
    /// </summary>
    private void StartIconLoading()
    {
        _iconQueue.Clear();

        // 面板可能还没生成（模板尚未应用），下一轮再关联
        if (_tilePanel == null)
        {
            Dispatcher.BeginInvoke(new Action(WireTilePanel), DispatcherPriority.Loaded);
            return;
        }

        _tilePanel.NotifyRealizedItems();
    }

    /// <summary>关联主磁贴面板的容器生成事件，实现图标按需加载。</summary>
    private void WireTilePanel()
    {
        _tilePanel = WireTilePanel(FlatTileList);
        _tilePanel?.NotifyRealizedItems();
    }

    /// <summary>搜索与主磁贴列表共用面板事件订阅；首次布局后的容器由调用方补通知。</summary>
    private VirtualizingTilePanel? WireTilePanel(ListBox list)
    {
        var panel = FindVisualChildren<VirtualizingTilePanel>(list).FirstOrDefault();
        if (panel == null)
        {
            return null;
        }

        panel.ItemRealized -= TilePanel_ItemRealized;
        panel.ItemRealized += TilePanel_ItemRealized;
        return panel;
    }

    private void TilePanel_ItemRealized(object? sender, object item)
    {
        if (item is not TileItem { Icon: null, IconResolved: false } tile)
        {
            return;
        }

        if (!_iconQueue.Contains(tile))
        {
            _iconQueue.Enqueue(tile);
            EnsureIconTimer();
        }
    }

    private void EnsureIconTimer()
    {
        if (_iconTimer == null)
        {
            _iconTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _iconTimer.Tick += (_, _) => DrainIconQueues();
        }

        if (!_iconTimer.IsEnabled)
        {
            _iconTimer.Start();
        }
    }

    private void DrainIconQueues()
    {
        for (int i = 0; i < 12 && _iconQueue.Count > 0; i++)
        {
            var tile = _iconQueue.Dequeue();
            // 最终标准化到 96px（磁贴实际显示 96 DIP），直接砍一半内存
            tile.Icon = ShellIcon.Load(tile.Target, 96);
            tile.IconResolved = true;
        }

        for (int i = 0; i < 12 && _allIconQueue.Count > 0; i++)
        {
            var app = _allIconQueue.Dequeue();
            app.Icon = ShellIcon.Load(app.Target, 32);
            app.IconResolved = true;
        }

        if (_iconQueue.Count == 0 && _allIconQueue.Count == 0)
        {
            _iconTimer?.Stop();
        }
    }

    /// <summary>F5 重新扫描应用，只追加新应用，不改动已有尺寸与顺序。</summary>
    private void ReloadCatalog()
    {
        var entries = AppCatalog.Enumerate(refresh: true);
        if (entries.Count == 0)
        {
            return;
        }

        var existing = new HashSet<string>(
            _layout.Groups.SelectMany(g => g.Tiles).Select(t => t.Id),
            StringComparer.OrdinalIgnoreCase);

        var target = _layout.Groups.FirstOrDefault() ?? new TileGroup();
        if (!_layout.Groups.Contains(target))
        {
            _layout.Groups.Add(target);
        }

        foreach (var entry in entries)
        {
            if (existing.Add(entry.Target))
            {
                target.Tiles.Add(TileItem.Create(entry));
            }
        }

        LayoutStore.Save(_layout);
        RebuildFlatItems();
        StartIconLoading();

        // 所有应用列表已构建过时同步刷新，避免出现新旧两份数据
        if (_allSections.Count > 0)
        {
            EnsureAllApps(refresh: true);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideMenu();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_selectedTile != null)
            {
                Launch(_selectedTile);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ReloadCatalog();
            e.Handled = true;
        }
    }

    /// <summary>所有应用视图下先处理方向键与 Enter，避免落到垂直滚动逻辑里。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_allAppsVisible && SearchPanel.Visibility != Visibility.Visible)
        {
            HandleAllAppsKey(e);
        }
    }

    /// <summary>
    /// 接管文本输入：普通按键与输入法上屏都走 TextInput，
    /// 因此中文组合输入完成后同样能直接进入搜索，无需先点搜索框。
    /// </summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        string text = e.Text;
        if (string.IsNullOrEmpty(text) || text.Any(char.IsControl))
        {
            return;
        }

        SearchBox.Focus();
        SearchBox.Text += text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowSearch(false);
            return;
        }

        _searchResults.Clear();
        foreach (var tile in _layout.Groups
                     .SelectMany(g => g.Tiles)
                     .Where(t => AppSearch.Matches(t.Name, query))
                     .OrderBy(t => t.Name, StringComparer.CurrentCulture))
        {
            _searchResults.Add(tile);
        }

        SearchHeader.Text = $"搜索结果 · {_searchResults.Count} 项";
        SearchEmpty.Visibility = _searchResults.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSearch(true);
    }

    private void ShowSearch(bool searching)
    {
        SearchPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

        if (searching)
        {
            // 搜索结果独占内容区，磁贴列表与所有应用列表都先隐藏
            FlatTileList.Visibility = Visibility.Collapsed;
            AllAppsPanel.Visibility = Visibility.Collapsed;
            LetterIndex.Visibility = Visibility.Collapsed;
            ClearSelectionExcept(SearchResults);
            _selectedTile = null;

            // 搜索面板首次显示时才应用模板，布局后关联面板并补加载首屏图标
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _searchTilePanel ??= WireTilePanel(SearchResults);
                _searchTilePanel?.NotifyRealizedItems();
            }), DispatcherPriority.Loaded);
            return;
        }

        SearchResults.SelectedItem = null;
        _selectedTile = null;

        // 退出搜索时恢复到进入搜索前的视图
        if (_allAppsVisible)
        {
            FlatTileList.Visibility = Visibility.Collapsed;
            AllAppsPanel.Visibility = Visibility.Visible;
            LetterIndex.Visibility = Visibility.Visible;
            return;
        }

        SearchPanel.Visibility = Visibility.Collapsed;
        FlatTileList.Visibility = Visibility.Visible;
        AllAppsPanel.Visibility = Visibility.Collapsed;
        LetterIndex.Visibility = Visibility.Collapsed;

        // 退出搜索后把选中恢复到第一个磁贴，保持键盘可继续操作
        if (FlatTileList.Items.OfType<TileItem>().FirstOrDefault() is { } firstTile)
        {
            int idx = FlatTileList.Items.IndexOf(firstTile);
            if (idx >= 0) FlatTileList.SelectedIndex = idx;
        }
    }

    private void AllApps_Click(object sender, RoutedEventArgs e) => ShowAllApps(!_allAppsVisible);

    /// <summary>在磁贴视图与「所有应用」列表之间切换。</summary>
    private void ShowAllApps(bool show)
    {
        if (show)
        {
            EnsureAllApps();
        }

        _allAppsVisible = show;
        _allAppsFocus = -1;
        MenuGlyph.Text = show ? "\uE72B" : "\uE700";
        ToolTipService.SetToolTip(AllAppsButton, show ? "返回" : "所有应用");

        // 清空搜索即回到当前视图，ShowSearch 会按 _allAppsVisible 决定显示哪一屏
        SearchBox.Text = string.Empty;
        ShowSearch(false);

        // 切换视图时把对应滚动区域复位到顶部
        if (show && _allAppRows.Count > 0)
        {
            AllAppsList.ScrollIntoView(_allAppRows[0]);
        }
        else
        {
            // FlatTileList 自带 ScrollViewer，用 ScrollIntoView 确保滚到第一个磁贴
            if (FlatTileList.Items.OfType<TileItem>().FirstOrDefault() is { } first)
            {
                FlatTileList.ScrollIntoView(first);
            }
        }
    }

    /// <summary>首次进入时构建字母分组；refresh 用于 F5 重新扫描应用后刷新列表。</summary>
    private void EnsureAllApps(bool refresh = false)
    {
        if (_allSections.Count == 0 || refresh)
        {
            _allIconQueue.Clear();
            _allSections = AllAppsBuilder.Build();
            _allAppRows.Clear();
            foreach (var section in _allSections)
            {
                _allAppRows.Add(section);
                foreach (var app in section.Apps)
                {
                    _allAppRows.Add(app);
                }
            }

            LetterIndex.ItemsSource = _allSections.Select(s => s.Key).ToList();
        }

        QueueVisibleAllAppIcons();
    }

    private void AllAppsList_LayoutUpdated(object? sender, EventArgs e) => QueueVisibleAllAppIcons();

    /// <summary>只排队已生成容器的小图标，滚动出新行时由 LayoutUpdated 继续补加载。</summary>
    private void QueueVisibleAllAppIcons()
    {
        foreach (var app in FindVisualChildren<Button>(AllAppsList)
                     .Select(b => b.DataContext)
                     .OfType<AllAppItem>())
        {
            if (app.Icon == null && !app.IconResolved && !_allIconQueue.Contains(app))
            {
                _allIconQueue.Enqueue(app);
            }
        }

        if (_allIconQueue.Count > 0)
        {
            EnsureIconTimer();
        }
    }

    /// <summary>点击右侧字母索引，滚动到对应分组。</summary>
    private void Letter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string key })
        {
            return;
        }

        var section = _allSections.FirstOrDefault(s => s.Key == key);
        if (section == null)
        {
            return;
        }

        int index = _allAppRows.IndexOf(section);
        if (index < 0)
        {
            return;
        }

        // CanContentScroll=true 时偏移单位是数据项，直接按扁平索引定位，无需生成离屏容器
        AllAppsList.UpdateLayout();
        if (FindVisualChildren<ScrollViewer>(AllAppsList).FirstOrDefault() is { } scroller)
        {
            scroller.ScrollToVerticalOffset(index);
        }
    }

    private void AllApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AllAppItem app })
        {
            LaunchTarget(app.Name, app.Target);
        }
    }

    /// <summary>所有应用列表的键盘操作：上下移动、Home/End 首尾、Enter 启动。</summary>
    private bool HandleAllAppsKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveAllAppsFocus(_allAppsFocus < 0 ? 0 : 1);
                break;
            case Key.Up:
                MoveAllAppsFocus(_allAppsFocus < 0 ? 0 : -1);
                break;
            case Key.Home:
                MoveAllAppsFocus(-9999);
                break;
            case Key.End:
                MoveAllAppsFocus(9999);
                break;
            case Key.Enter:
                LaunchFocusedApp();
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    private void MoveAllAppsFocus(int delta)
    {
        var apps = GetAllApps();
        if (apps.Count == 0)
        {
            return;
        }

        _allAppsFocus = Math.Clamp(_allAppsFocus + delta, 0, apps.Count - 1);
        var app = apps[_allAppsFocus];

        // 先让列表生成目标容器，再把焦点交给其中的按钮；离屏行不参与焦点遍历
        AllAppsList.ScrollIntoView(app);
        AllAppsList.UpdateLayout();
        var row = FindVisualChildren<Button>(AllAppsList)
            .FirstOrDefault(b => ReferenceEquals(b.DataContext, app));
        if (row != null)
        {
            row.Focus();
            row.BringIntoView();
        }
    }

    private void LaunchFocusedApp()
    {
        var apps = GetAllApps();
        if (apps.Count == 0)
        {
            return;
        }

        int index = Math.Clamp(_allAppsFocus, 0, apps.Count - 1);
        var app = apps[index];
        LaunchTarget(app.Name, app.Target);
    }

    private List<AllAppItem> GetAllApps() => _allSections.SelectMany(s => s.Apps).ToList();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 搜索框为空时，方向键交还给磁贴区域，保证键盘选磁贴仍然顺手
        if (SearchBox.Text.Length == 0 && !_allAppsVisible &&
            e.Key is Key.Down or Key.Up or Key.Left or Key.Right or Key.Home or Key.End)
        {
            FocusTileGrid();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var target = SearchResults.SelectedItem as TileItem ?? _searchResults.FirstOrDefault();
            if (target != null)
            {
                Launch(target);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _searchResults.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            if (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem item)
            {
                item.Focus();
            }
            e.Handled = true;
        }
    }

    /// <summary>把焦点从搜索框移到当前选中的磁贴，方向键随后由列表接管。</summary>
    private void FocusTileGrid()
    {
        if (FlatTileList.Items.Count == 0)
        {
            return;
        }

        // 跳过 GroupHeaderItem，选中第一个 TileItem
        if (FlatTileList.SelectedItem == null || FlatTileList.SelectedItem is GroupHeaderItem)
        {
            var firstTile = FlatTileList.Items.OfType<TileItem>().FirstOrDefault();
            if (firstTile != null)
            {
                FlatTileList.SelectedItem = firstTile;
            }
        }

        if (FlatTileList.ItemContainerGenerator.ContainerFromItem(FlatTileList.SelectedItem) is ListBoxItem item)
        {
            item.Focus();
        }
        else
        {
            FlatTileList.Focus();
        }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuEntry("锁定", LockWorkStation));
        menu.Items.Add(CreateMenuEntry("注销", () => RunShutdown("/l")));
        menu.Items.Add(new Separator());

        // 开机自启：写入 HKCU Run 项，普通权限即可生效
        var autoStart = new MenuItem
        {
            Header = "开机自启",
            IsCheckable = true,
            IsChecked = AutoStart.IsEnabled()
        };
        autoStart.Click += (_, _) =>
        {
            AutoStart.SetEnabled(autoStart.IsChecked);
            autoStart.IsChecked = AutoStart.IsEnabled();
        };
        menu.Items.Add(autoStart);

        menu.Items.Add(new MenuItem
        {
            Header = _hotkeyRegistered ? "热键 Ctrl+Alt+Space" : "热键注册失败",
            IsEnabled = false
        });

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuEntry("退出开始菜单", Close));

        ShowAbove(AccountButton, menu);
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuEntry("睡眠", Sleep));
        menu.Items.Add(CreateMenuEntry("关机", () => RunShutdown("/s /t 0")));
        menu.Items.Add(CreateMenuEntry("重启", () => RunShutdown("/r /t 0")));
        ShowAbove(PowerButton, menu);
    }

    /// <summary>在按钮上方弹出菜单，并让右边缘对齐，避免贴到屏幕右侧被裁切。</summary>
    private static void ShowAbove(FrameworkElement target, ContextMenu menu)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Top;
        menu.Opened += (_, _) => menu.HorizontalOffset = -(menu.ActualWidth - target.ActualWidth);
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void LockWorkStation()
    {
        Close();
        Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true });
    }

    private void RunShutdown(string arguments)
    {
        Close();
        Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
        {
            UseShellExecute = true,
            CreateNoWindow = true
        });
    }

    private void Sleep()
    {
        Close();
        SetSuspendState(false, false, false);
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        SetTilePressed(sender as ListBoxItem, false);

        // 拖拽松手后系统可能补发一次 MouseUp，这段时间内忽略，避免误启动
        if (DateTime.UtcNow < _suppressClickUntil)
        {
            return;
        }

        if (sender is ListBoxItem { DataContext: TileItem tile })
        {
            Launch(tile);
            e.Handled = true;
        }
    }

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: TileItem tile } container)
        {
            _dragCandidate = tile;
            _dragStart = e.GetPosition(this);
            SetTilePressed(container, true);
        }
    }

    private void Tile_MouseLeave(object sender, MouseEventArgs e) =>
        SetTilePressed(sender as ListBoxItem, false);

    /// <summary>切换磁贴模板里的压暗层，给出明确的按下反馈。</summary>
    private static void SetTilePressed(ListBoxItem? item, bool pressed)
    {
        if (item?.Template?.FindName("Pressed", item) is Border layer)
        {
            layer.Opacity = pressed ? 1 : 0;
        }
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // 搜索结果顺序是动态排序的，拖拽调整没有意义
        if (SearchPanel.Visibility == Visibility.Visible)
        {
            _dragCandidate = null;
            return;
        }

        Point position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < 8 && Math.Abs(position.Y - _dragStart.Y) < 8)
        {
            return;
        }

        var tile = _dragCandidate;
        _dragCandidate = null;

        if (sender is ListBoxItem container)
        {
            _dragSourceGroup = FindGroup(container);
            SetTilePressed(container, false);
            DragDrop.DoDragDrop(container, new DataObject(typeof(TileItem), tile), DragDropEffects.Move);
            _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(300);
        }
    }

    private void Tile_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TileItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>拖到目标磁贴上：插入目标之前，跨组时移动到目标所在分组。</summary>
    private void Tile_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TileItem)) ||
            sender is not ListBoxItem { DataContext: TileItem target } targetContainer ||
            e.Data.GetData(typeof(TileItem)) is not TileItem source ||
            ReferenceEquals(source, target))
        {
            return;
        }

        var sourceGroup = _dragSourceGroup ?? _layout.Groups.FirstOrDefault(g => g.Tiles.Contains(source));
        var targetGroup = FindGroup(targetContainer);
        if (sourceGroup == null || targetGroup == null || !sourceGroup.Tiles.Contains(source))
        {
            return;
        }

        sourceGroup.Tiles.Remove(source);
        int to = targetGroup.Tiles.IndexOf(target);
        targetGroup.Tiles.Insert(to < 0 ? targetGroup.Tiles.Count : to, source);

        _selectedTile = source;
        RemoveEmptyGroups();
        RebuildFlatItems();
        LayoutStore.Save(_layout);
        e.Handled = true;
    }

    /// <summary>拖拽或移动后清掉空分组，避免留下空标题；返回是否真的移除了分组。</summary>
    private bool RemoveEmptyGroups()
    {
        bool removed = false;
        for (int i = _layout.Groups.Count - 1; i >= 0; i--)
        {
            if (_layout.Groups[i].Tiles.Count == 0)
            {
                _layout.Groups.RemoveAt(i);
                removed = true;
            }
        }

        return removed;
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: TileItem tile } container)
        {
            return;
        }

        var menu = new ContextMenu();
        var sizes = new (string Label, string Value)[]
        {
            ("小", "1x1"),
            ("中", "2x2"),
            ("宽", "4x2"),
            ("大", "4x4")
        };

        foreach (var size in sizes)
        {
            var menuItem = new MenuItem
            {
                Header = size.Label,
                IsCheckable = true,
                IsChecked = tile.Size == size.Value
            };
            menuItem.Click += (_, _) =>
            {
                tile.Size = size.Value;
                LayoutStore.Save(_layout);
            };
            menu.Items.Add(menuItem);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildMoveToGroupMenu(tile));

        menu.PlacementTarget = container;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>“移到分组”子菜单：列出现有分组，并提供新建分组入口。</summary>
    private MenuItem BuildMoveToGroupMenu(TileItem tile)
    {
        var root = new MenuItem { Header = "移到分组" };
        var names = _layout.Groups.Select(g => g.Name).ToList();
        var current = FindGroupName(tile);

        foreach (var name in names)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = current == name,
                IsEnabled = current != name
            };
            item.Click += (_, _) => MoveToGroup(tile, name);
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());

        var create = new MenuItem { Header = "新建分组…" };
        create.Click += (_, _) =>
        {
            string? name = NamePrompt.Ask(this, "新建分组", $"分组 {names.Count + 1}");
            if (name != null)
            {
                MoveToGroup(tile, name);
            }
        };
        root.Items.Add(create);

        return root;
    }

    /// <summary>把磁贴移到指定分组末尾；分组不存在时自动创建。</summary>
    private void MoveToGroup(TileItem tile, string groupName)
    {
        var source = _layout.Groups.FirstOrDefault(g => g.Tiles.Contains(tile));
        if (source == null)
        {
            return;
        }

        var target = _layout.Groups.FirstOrDefault(g => g.Name == groupName);
        if (target == null)
        {
            target = new TileGroup { Name = groupName };
            _layout.Groups.Add(target);
        }

        if (ReferenceEquals(source, target))
        {
            return;
        }

        source.Tiles.Remove(tile);
        target.Tiles.Add(tile);
        tile.Group = groupName;

        _selectedTile = tile;
        RemoveEmptyGroups();
        RebuildFlatItems();
        LayoutStore.Save(_layout);
    }

    private string? FindGroupName(TileItem tile) =>
        _layout.Groups.FirstOrDefault(g => g.Tiles.Contains(tile))?.Name;

    /// <summary>空白处右键：新建分组与布局的导出、导入、重置。</summary>
    private void Content_RightClick(object sender, MouseButtonEventArgs e)
    {
        // 磁贴与按钮自己处理右键，这里只接管空白区域；搜索结果的空白处不提供布局操作
        if (SearchPanel.Visibility == Visibility.Visible ||
            FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null ||
            FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuEntry("新建分组…", CreateGroup));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuEntry("导出布局…", ExportLayout));
        menu.Items.Add(CreateMenuEntry("导入布局…", ImportLayout));
        menu.Items.Add(CreateMenuEntry("重置布局", ResetLayout));

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>分组标题右键：重命名、调整顺序、删除。</summary>
    private void Group_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TileGroup group })
        {
            return;
        }

        int index = _layout.Groups.IndexOf(group);
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuEntry("重命名…", () => RenameGroup(group)));

        var up = CreateMenuEntry("上移", () => MoveGroup(group, -1));
        up.IsEnabled = index > 0;
        menu.Items.Add(up);

        var down = CreateMenuEntry("下移", () => MoveGroup(group, 1));
        down.IsEnabled = index >= 0 && index < _layout.Groups.Count - 1;
        menu.Items.Add(down);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuEntry("删除分组", () => DeleteGroup(group)));

        menu.PlacementTarget = (FrameworkElement)sender;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CreateGroup()
    {
        string? name = NamePrompt.Ask(this, "新建分组", $"分组 {_layout.Groups.Count + 1}");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_layout.Groups.Any(g => g.Name == name))
        {
            MessageBox.Show(this, $"已存在名为「{name}」的分组", "新建分组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _layout.Groups.Add(new TileGroup { Name = name });
        LayoutStore.Save(_layout);
    }

    private void RenameGroup(TileGroup group)
    {
        string? name = NamePrompt.Ask(this, "重命名分组", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name)
        {
            return;
        }

        if (_layout.Groups.Any(g => !ReferenceEquals(g, group) && g.Name == name))
        {
            MessageBox.Show(this, $"已存在名为「{name}」的分组", "重命名分组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        group.Name = name;
        foreach (var tile in group.Tiles)
        {
            tile.Group = name;
        }

        RebuildFlatItems();
        LayoutStore.Save(_layout);
    }

    private void MoveGroup(TileGroup group, int delta)
    {
        int from = _layout.Groups.IndexOf(group);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _layout.Groups.Count)
        {
            return;
        }

        _layout.Groups.Move(from, to);
        RebuildFlatItems();
        LayoutStore.Save(_layout);
    }

    private void DeleteGroup(TileGroup group)
    {
        var result = MessageBox.Show(this,
            $"删除分组「{group.Name}」？其中的 {group.Tiles.Count} 个磁贴也会一并移除。",
            "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _layout.Groups.Remove(group);
        RebuildFlatItems();
        LayoutStore.Save(_layout);
    }

    private void ExportLayout()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出布局",
            FileName = "start-layout.json",
            DefaultExt = ".json",
            Filter = "布局文件 (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, LayoutStore.Serialize(_layout));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出布局", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportLayout()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入布局",
            Filter = "布局文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LayoutFile? layout;
        try
        {
            layout = LayoutStore.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取失败：{ex.Message}", "导入布局", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (layout == null)
        {
            MessageBox.Show(this, "文件内容不是有效的布局", "导入布局", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyLayout(layout);
    }

    private void ResetLayout()
    {
        var result = MessageBox.Show(this,
            "将把所有磁贴恢复为初始布局，当前分组与尺寸会被覆盖，确定继续？",
            "重置布局", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        ApplyLayout(BuildDefaultLayout());
    }

    /// <summary>整份替换布局：重建分组集合、重新加载图标并回到磁贴视图。</summary>
    private void ApplyLayout(LayoutFile layout)
    {
        _layout.Groups.Clear();
        foreach (var group in layout.Groups)
        {
            // 分组名同步到磁贴，保证「移到分组」的勾选状态正确
            foreach (var tile in group.Tiles)
            {
                tile.Group = group.Name;
            }

            _layout.Groups.Add(group);
        }

        _selectedTile = null;
        RebuildFlatItems();
        LayoutStore.Save(_layout);
        StartIconLoading();
        ShowAllApps(false);

        if (_allSections.Count > 0)
        {
            EnsureAllApps(refresh: true);
        }
    }

    private void Launch(TileItem tile) => LaunchTarget(tile.Name, tile.Target);

    private void LaunchTarget(string name, string target)
    {
        try
        {
            if (File.Exists(target))
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target) { UseShellExecute = true });
            }

            HideMenu();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法启动「{name}」\n{ex.Message}", "全屏开始菜单", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyRegistered && _hwndSource != null)
        {
            UnregisterHotKey(_hwndSource.Handle, HotkeyId);
        }

        _hwndSource?.RemoveHook(WndProc);
        LayoutStore.Save(_layout);
        base.OnClosed(e);
    }
}

/// <summary>磁贴数据模型：尺寸、颜色、图标均可持久化或动态更新。</summary>
public sealed class TileItem : INotifyPropertyChanged
{
    // 纯黑底上偏暗的颜色会与背景糊在一起，这里整体提高明度
    private static readonly string[] Palette =
    {
        "#FF3B7FC4", "#FF2A8FB0", "#FF5A5A5A", "#FFB08A2E", "#FF7F5BC4",
        "#FF1F5FA8", "#FF3E8368", "#FFC05060", "#FF467FBF", "#FF7A5AA0"
    };

    // 旧版调色板，仅用于把已保存的布局一次性迁移到上面的新配色
    private static readonly string[] LegacyPalette =
    {
        "#FF2B579A", "#FF1F6F8B", "#FF3A3A3A", "#FF8A6D1F", "#FF6B3FA0",
        "#FF012456", "#FF2E5E4E", "#FFB03A48", "#FF2F5D8A", "#FF5A3E7A"
    };

    /// <summary>把旧调色板中的颜色换成同下标的新颜色；非旧色保持原样。</summary>
    internal static string MigrateColor(string color)
    {
        int index = Array.FindIndex(LegacyPalette,
            c => string.Equals(c, color, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            return Palette[index];
        }

        // 早期默认色不在调色板里，同样并到第一个蓝色
        return string.Equals(color, "#FF1F4E79", StringComparison.OrdinalIgnoreCase)
            ? Palette[0]
            : color;
    }

    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Id { get; set; } = "";
    public string Group { get; set; } = "所有应用";
    public string Color { get; set; } = "#FF1F4E79";

    private string _size = "2x2";

    public string Size
    {
        get => _size;
        set
        {
            if (_size == value)
            {
                return;
            }
            _size = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(IconSize));
            OnPropertyChanged(nameof(IsSmall));
        }
    }

    // 96 的网格步长减去左右各 4 的间隙
    [JsonIgnore] public double TileWidth => _size switch { "1x1" => 88, "4x2" => 376, "4x4" => 376, _ => 184 };
    [JsonIgnore] public double TileHeight => _size switch { "1x1" => 88, "4x4" => 376, _ => 184 };
    [JsonIgnore] public double IconSize => _size switch { "1x1" => 32, "4x2" => 64, "4x4" => 96, _ => 64 };
    [JsonIgnore] public double LetterSize => _size switch
    {
        "1x1" => 22,
        "4x2" => 40,
        "4x4" => 56,
        _ => 34
    };
    [JsonIgnore] public bool IsSmall => _size == "1x1";
    [JsonIgnore] public string Letter => Name.Length > 0 ? Name[..1] : "?";

    // 图标还在队列里时先不显示首字占位，避免启动瞬间闪出一批假字母
    [JsonIgnore] public bool ShowPlaceholder => Icon == null && IconResolved;

    private bool _iconResolved;

    [JsonIgnore]
    public bool IconResolved
    {
        get => _iconResolved;
        set
        {
            if (_iconResolved == value)
            {
                return;
            }

            _iconResolved = value;
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    public override string ToString() => Name;

    /// <summary>按颜色字符串缓存已冻结的画刷，避免每次测量/渲染都新建 SolidColorBrush。</summary>
    private static readonly Dictionary<string, Brush> _brushCache = new();

    [JsonIgnore]
    public Brush ColorBrush
    {
        get
        {
            string key = Color ?? string.Empty;
            if (_brushCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Brush brush;
            try
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color));
                brush.Freeze();
            }
            catch
            {
                brush = Brushes.DimGray;
            }

            _brushCache[key] = brush;
            return brush;
        }
    }

    private ImageSource? _icon;

    [JsonIgnore]
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal static TileItem Create(AppEntry entry)
    {
        int hash = 0;
        foreach (char c in entry.Target)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }

        return new TileItem
        {
            Name = entry.Name,
            Target = entry.Target,
            Id = entry.Target,
            Color = Palette[hash % Palette.Length]
        };
    }
}

/// <summary>把窗口精确铺满鼠标所在显示器的物理像素矩形（含任务栏区域）。</summary>
internal static class FullScreenCover
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        GetCursorPos(out var point);
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var rect = info.rcMonitor;
        SetWindowPos(hwnd, HWND_TOPMOST,
            rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}

/// <summary>通过 HKCU 的 Run 项实现开机自启，不需要管理员权限。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FullScreenStart";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string path && path.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null)
        {
            return;
        }

        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (exe != null)
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
