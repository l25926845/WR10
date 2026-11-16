using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FullScreenStart;

/// <summary>
/// 虚拟化网格磁贴面板：按 Win10 开始菜单的多列多尺寸网格排布磁贴，
/// 但只生成可视区域内的容器，滚动出视野的立即回收。
///
/// 使用方式：作为 ListBox.ItemsPanel 放在有有限高度的容器内，
/// 让 ListBox 自带的 ScrollViewer 驱动 IScrollInfo 滚动。
/// VirtualizingPanel 基类提供容器管理（InternalChildren / AddInternalChild /
/// ItemContainerGenerator），我们在 MeasureOverride 里自己决定生成哪些容器。
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>网格步长：90 像素磁贴 + 左右各 3 像素间隙后取整。</summary>
    private const double Pitch = 96;

    /// <summary>完整布局：按数据索引存 (dataIndex, row, col, cols, rows)。</summary>
    private readonly List<(int DataIndex, int Row, int Col, int Cols, int Rows)> _layout = new();

    /// <summary>
    /// 容器被生成时触发，宿主据此按需加载图标等重资源。
    /// 只在磁贴滚动进可视区域时才回调，避免一次性加载全部磁贴的图标。
    /// </summary>
    public event EventHandler<object>? ItemRealized;

    /// <summary>当前已生成容器的数据项，供宿主导入初始可视区域的加载任务。</summary>
    public IEnumerable<object> RealizedItems
    {
        get
        {
            foreach (var child in InternalChildren)
            {
                if (child is FrameworkElement { DataContext: { } item })
                {
                    yield return item;
                }
            }
        }
    }

    /// <summary>把当前已实现的数据项重新通知一遍，用于宿主晚于首次布局完成事件订阅。</summary>
    public void NotifyRealizedItems()
    {
        foreach (var item in RealizedItems)
        {
            ItemRealized?.Invoke(this, item);
        }
    }

    private int _columns;
    private double _extentHeight;
    private double _extentWidth;
    private double _viewportHeight;
    private double _viewportWidth;
    private double _offsetY;

    // ── IScrollInfo 完整实现 ────────────────────────────────────────────────

    public ScrollViewer ScrollOwner { get; set; } = null!;
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentHeight => _extentHeight;
    public double ExtentWidth => _extentWidth;
    public double ViewportHeight => _viewportHeight;
    public double ViewportWidth => _viewportWidth;
    public double HorizontalOffset { get; private set; }
    public double VerticalOffset => _offsetY;

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void MouseWheelUp() => SetVerticalOffset(_offsetY - 3 * Pitch);
    public void MouseWheelDown() => SetVerticalOffset(_offsetY + 3 * Pitch);
    public void LineUp() => SetVerticalOffset(_offsetY - Pitch);
    public void LineDown() => SetVerticalOffset(_offsetY + Pitch);
    public void PageUp() => SetVerticalOffset(_offsetY - _viewportHeight);
    public void PageDown() => SetVerticalOffset(_offsetY + _viewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        double old = _offsetY;
        _offsetY = Math.Max(0, Math.Min(offset, Math.Max(0, _extentHeight - _viewportHeight)));
        if (Math.Abs(old - _offsetY) > 0.5)
        {
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    // ── VirtualizingPanel 核心生命周期 ──────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        // 每次测量重算网格（列数随窗口宽度变化）
        double width = ResolveWidth(availableSize);
        _columns = Math.Max(1, (int)Math.Floor(width / Pitch));
        _viewportHeight = availableSize.Height;
        _viewportWidth = width;

        BuildLayout();

        _extentWidth = _columns * Pitch;
        _extentHeight = _layout.Count == 0 ? Pitch : _layout.Max(p => p.Row + p.Rows) * Pitch;

        // 夹紧 offset 到有效范围
        _offsetY = Math.Max(0, Math.Min(_offsetY, Math.Max(0, _extentHeight - _viewportHeight)));

        // 基类 MeasureOverride 处理 InternalChildren 的测量
        base.MeasureOverride(new Size(width, _extentHeight));

        // 在基类测量完后，决定需要哪些容器：生成可视的、回收不可视的
        UpdateContainers();

        // 期望宽度不能超过可用宽度，否则会把父 Grid（Auto 列）越撑越宽，导致列数反复变化
        double desiredWidth = double.IsInfinity(availableSize.Width) ? _extentWidth : availableSize.Width;
        return new Size(desiredWidth, Math.Max(_extentHeight, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 注意：使用 IScrollInfo 时 ScrollViewer 会以「完整 extent」作为排列尺寸，
        // 不能用 finalSize.Height 覆盖视口高度，否则会失去虚拟化效果。
        _viewportWidth = finalSize.Width;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child is FrameworkElement { DataContext: { } item })
            {
                int dataIndex = FindDataIndex(item);
                if (dataIndex >= 0 && FindLayout(dataIndex, out var p))
                {
                    child.Arrange(new Rect(
                        p.Col * Pitch,
                        p.Row * Pitch - _offsetY,
                        p.Cols * Pitch,
                        p.Rows * Pitch));
                }
            }
        }

        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }

    protected override void OnItemsChanged(object? sender, ItemsChangedEventArgs args)
    {
        InvalidateMeasure();
    }

    // ── 容器生成/回收 ────────────────────────────────────────────────────────

    private void UpdateContainers()
    {
        if (_layout.Count == 0) return;

        // 面板首次测量时可能还没接上 ItemsPresenter，此时退回宿主的容器生成器
        IItemContainerGenerator? generator = ItemContainerGenerator ?? FindItemsOwner()?.ItemContainerGenerator;
        if (generator == null)
        {
            return;
        }

        // 计算可视区域覆盖的行范围（多留 1 行缓冲，避免边缘闪烁）
        double topY = _offsetY;
        double bottomY = _offsetY + _viewportHeight;
        int firstRow = Math.Max(0, (int)Math.Floor(topY / Pitch));
        int lastRow = (int)Math.Ceiling(bottomY / Pitch) + 1;

        // 收集可视的 dataIndex
        var visibleIndices = new HashSet<int>();
        foreach (var (idx, row, _, _, rows) in _layout)
        {
            if (row + rows - 1 >= firstRow && row <= lastRow)
            {
                visibleIndices.Add(idx);
            }
        }

        // 回收不可视的现有容器（从后往前删避免索引错位）
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            if (InternalChildren[i] is not FrameworkElement fe)
            {
                continue;
            }

            int di = FindDataIndex(fe.DataContext);
            if (visibleIndices.Contains(di))
            {
                continue;
            }

            var position = generator.GeneratorPositionFromIndex(di);
            if (position.Offset == 0)
            {
                generator.Remove(position, 1);
            }

            RemoveInternalChildRange(i, 1);
        }

        // 收集当前已生成的 dataIndex，避免重复生成
        var existingDataIndices = new HashSet<int>();
        foreach (UIElement child in InternalChildren)
        {
            if (child is FrameworkElement fe)
            {
                existingDataIndices.Add(FindDataIndex(fe.DataContext));
            }
        }

        // 按 dataIndex 升序生成缺失的可视容器，保持 InternalChildren 顺序与数据顺序一致
        foreach (var (idx, _, _, cols, rows) in _layout)
        {
            if (!visibleIndices.Contains(idx) || existingDataIndices.Contains(idx))
            {
                continue;
            }

            var item = GetItemAt(idx);
            if (item == null)
            {
                continue;
            }

            // WPF 要求 GenerateNext 必须在 StartAt 的内容生成上下文内调用
            var startPos = generator.GeneratorPositionFromIndex(idx);
            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                if (generator.GenerateNext(out bool newlyRealized) is UIElement container)
                {
                    if (newlyRealized)
                    {
                        AddInternalChild(container);
                        generator.PrepareItemContainer(container);
                    }

                    container.Measure(new Size(cols * Pitch, rows * Pitch));
                }
            }

            // 容器刚进入可视区域，通知宿主按需加载图标
            ItemRealized?.Invoke(this, item);
        }
    }

    // ── 布局计算 ────────────────────────────────────────────────────────────

    /// <summary>VirtualizingPanel 基类没有 ItemsOwner 属性，自己从可视化树往上找宿主 ItemsControl。</summary>
    private ItemsControl? FindItemsOwner()
    {
        DependencyObject? node = this;
        while (node != null)
        {
            if (node is ItemsControl ic) return ic;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void BuildLayout()
    {
        _layout.Clear();

        int total = GetTotalCount();
        if (total == 0) return;

        var used = new HashSet<long>();
        int minRow = 0;   // 磁贴允许排布的最小行：分组标题之后要从下一行开始，避免下一组补进上一组的空位
        int bottom = 0;   // 已占用区域的下一行，分组标题就落在这一行

        for (int i = 0; i < total; i++)
        {
            var item = GetItemAt(i);

            if (item is GroupHeaderItem)
            {
                // 分组标题独占一整行，并且强制后续磁贴另起一行
                int row = Math.Max(bottom, minRow);
                Mark(used, row, 0, _columns, 1);
                _layout.Add((i, row, 0, _columns, 1));
                minRow = row + 1;
                bottom = Math.Max(bottom, row + 1);
                continue;
            }

            var (cols, rows) = SpanOf(item);
            cols = Math.Min(cols, _columns);
            var (r, c) = FindSlot(used, cols, rows, minRow);
            Mark(used, r, c, cols, rows);
            _layout.Add((i, r, c, cols, rows));
            bottom = Math.Max(bottom, r + rows);
        }
    }

    private int GetTotalCount()
    {
        if (FindItemsOwner() is ItemsControl ic)
        {
            if (ic.ItemsSource is IList list) return list.Count;
            return ic.Items.Count;
        }
        return 0;
    }

    private object? GetItemAt(int index)
    {
        if (FindItemsOwner() is not ItemsControl ic) return null;
        if (ic.ItemsSource is IList list && index >= 0 && index < list.Count)
            return list[index];
        if (index >= 0 && index < ic.Items.Count) return ic.Items[index];
        return null;
    }

    /// <summary>
    /// 用 ReferenceEquals 比对引用类型，TileItem/GroupHeaderItem 都是 class，
    /// 默认 Equals 就是引用比较，但显式用 ReferenceEquals 更安全。
    /// </summary>
    private int FindDataIndex(object? item)
    {
        if (item == null || FindItemsOwner() is not ItemsControl ic) return -1;

        if (ic.ItemsSource is IList list)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], item)) return i;
        }
        else
        {
            for (int i = 0; i < ic.Items.Count; i++)
                if (ReferenceEquals(ic.Items[i], item)) return i;
        }
        return -1;
    }

    private bool FindLayout(int dataIndex, out (int Row, int Col, int Cols, int Rows) p)
    {
        for (int i = 0; i < _layout.Count; i++)
        {
            if (_layout[i].DataIndex == dataIndex)
            {
                var (_, row, col, cols, rows) = _layout[i];
                p = (row, col, cols, rows);
                return true;
            }
        }
        p = (0, 0, 2, 2);
        return false;
    }

    /// <summary>按数据类型取占位：GroupHeaderItem 整行，TileItem 按 Size 档位。</summary>
    private (int Cols, int Rows) SpanOf(object? item)
    {
        return item switch
        {
            GroupHeaderItem => (_columns, 1),
            TileItem tile => tile.Size switch
            {
                "1x1" => (1, 1),
                "4x2" => (4, 2),
                "4x4" => (4, 4),
                "2x2" => (2, 2),
                _ => (2, 2)
            },
            _ => (2, 2)
        };
    }

    private double ResolveWidth(Size available)
    {
        if (!double.IsInfinity(available.Width) && available.Width >= Pitch * 3)
            return available.Width;

        // 向上找宿主 ListBox 的 ActualWidth
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node != null)
        {
            if (node is ListBox { ActualWidth: var aw } && aw >= Pitch * 3)
                return aw;
            node = VisualTreeHelper.GetParent(node);
        }

        return 1200;
    }

    private (int Row, int Col) FindSlot(HashSet<long> used, int cols, int rows, int minRow = 0)
    {
        // 安全上限：最多搜索 1000 行，正常布局不可能超过
        int maxRow = 1000;
        for (int row = minRow; row < maxRow; row++)
        {
            for (int col = 0; col + cols <= _columns; col++)
            {
                bool free = true;
                for (int r = 0; r < rows && free; r++)
                    for (int c = 0; c < cols; c++)
                        if (used.Contains(Key(row + r, col + c))) free = false;
                if (free) return (row, col);
            }
        }

        // 找不到就扔到最底部
        return (maxRow, 0);
    }

    private static void Mark(HashSet<long> used, int row, int col, int cols, int rows)
    {
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                used.Add(Key(row + r, col + c));
    }

    private static long Key(int row, int col) => ((long)row << 32) | (uint)col;
}
