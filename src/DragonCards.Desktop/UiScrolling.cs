using Microsoft.Xna.Framework;

namespace DragonCards.Desktop;

internal enum ScrollAlignment
{
    Nearest,
    Start,
    Center,
    End
}

internal readonly record struct VisibleRange(int Start, int Count)
{
    public int EndExclusive => Start + Count;
    public bool Contains(int index) => index >= Start && index < EndExclusive;

    public int Clamp(int index, int itemCount)
    {
        if (itemCount <= 0 || Count <= 0)
        {
            return -1;
        }

        var first = Math.Clamp(Start, 0, itemCount - 1);
        var last = Math.Clamp(EndExclusive - 1, first, itemCount - 1);
        return Math.Clamp(index, first, last);
    }
}

internal readonly record struct ScrollBarMetrics(
    int TrackStart,
    int TrackLength,
    int ThumbStart,
    int ThumbLength,
    bool CanScroll)
{
    public int TrackEnd => TrackStart + TrackLength;
    public int ThumbEnd => ThumbStart + ThumbLength;
    public int ThumbTravel => Math.Max(0, TrackLength - ThumbLength);
}

internal sealed class ScrollState
{
    private bool _draggingThumb;
    private int _thumbGrabOffset;

    public int LineCount { get; private set; }
    public int ViewportLineCount { get; private set; }
    public int Offset { get; private set; }
    public int MaxOffset => Math.Max(0, LineCount - ViewportLineCount);
    public bool CanScroll => MaxOffset > 0;
    public bool IsDraggingThumb => _draggingThumb;
    public VisibleRange VisibleRange =>
        new(Offset, Math.Max(0, Math.Min(ViewportLineCount, LineCount - Offset)));

    public void Configure(int lineCount, int viewportLineCount)
    {
        LineCount = Math.Max(0, lineCount);
        ViewportLineCount = Math.Max(0, viewportLineCount);
        Offset = Math.Clamp(Offset, 0, MaxOffset);
        if (!CanScroll)
        {
            EndThumbDrag();
        }
    }

    public bool SetOffset(int offset)
    {
        var next = Math.Clamp(offset, 0, MaxOffset);
        if (next == Offset)
        {
            return false;
        }

        Offset = next;
        return true;
    }

    public bool ScrollBy(int lineDelta)
    {
        var requested = (long)Offset + lineDelta;
        return SetOffset((int)Math.Clamp(requested, 0L, MaxOffset));
    }

    public bool PageBy(int pageDelta)
    {
        var pageSize = Math.Max(1, ViewportLineCount);
        var requested = (long)pageDelta * pageSize;
        return ScrollBy((int)Math.Clamp(requested, int.MinValue, int.MaxValue));
    }

    public bool MoveToStart() => SetOffset(0);
    public bool MoveToEnd() => SetOffset(MaxOffset);

    public bool EnsureVisible(int lineIndex, ScrollAlignment alignment = ScrollAlignment.Nearest)
    {
        if (LineCount == 0)
        {
            return false;
        }

        var index = Math.Clamp(lineIndex, 0, LineCount - 1);
        var next = alignment switch
        {
            ScrollAlignment.Start => index,
            ScrollAlignment.Center => index - Math.Max(0, ViewportLineCount - 1) / 2,
            ScrollAlignment.End => index - Math.Max(0, ViewportLineCount - 1),
            _ when index < Offset => index,
            _ when index >= Offset + ViewportLineCount => index - Math.Max(0, ViewportLineCount - 1),
            _ => Offset
        };
        return SetOffset(next);
    }

    public ScrollBarMetrics GetScrollBar(int trackStart, int trackLength, int minimumThumbLength = UiTheme.MinimumScrollThumbLength)
    {
        var safeLength = Math.Max(0, trackLength);
        if (safeLength == 0)
        {
            return new ScrollBarMetrics(trackStart, 0, trackStart, 0, false);
        }

        if (!CanScroll || LineCount == 0)
        {
            return new ScrollBarMetrics(trackStart, safeLength, trackStart, safeLength, false);
        }

        var proportionalLength = (int)MathF.Round(safeLength * (ViewportLineCount / (float)LineCount));
        var thumbLength = Math.Clamp(proportionalLength, Math.Min(minimumThumbLength, safeLength), safeLength);
        var travel = safeLength - thumbLength;
        var thumbStart = trackStart + (int)MathF.Round(Offset / (float)MaxOffset * travel);
        return new ScrollBarMetrics(trackStart, safeLength, thumbStart, thumbLength, travel > 0);
    }

    public bool BeginThumbDrag(int pointerCoordinate, ScrollBarMetrics metrics)
    {
        if (!metrics.CanScroll || pointerCoordinate < metrics.ThumbStart || pointerCoordinate >= metrics.ThumbEnd)
        {
            return false;
        }

        _draggingThumb = true;
        _thumbGrabOffset = pointerCoordinate - metrics.ThumbStart;
        return true;
    }

    public bool DragThumb(int pointerCoordinate, ScrollBarMetrics metrics)
    {
        if (!_draggingThumb || !metrics.CanScroll || metrics.ThumbTravel <= 0)
        {
            return false;
        }

        var desiredThumbStart = Math.Clamp(
            pointerCoordinate - _thumbGrabOffset,
            metrics.TrackStart,
            metrics.TrackEnd - metrics.ThumbLength);
        var ratio = (desiredThumbStart - metrics.TrackStart) / (float)metrics.ThumbTravel;
        return SetOffset((int)MathF.Round(ratio * MaxOffset));
    }

    public void EndThumbDrag()
    {
        _draggingThumb = false;
        _thumbGrabOffset = 0;
    }

    public bool ClickTrack(int pointerCoordinate, ScrollBarMetrics metrics)
    {
        if (!metrics.CanScroll || pointerCoordinate < metrics.TrackStart || pointerCoordinate >= metrics.TrackEnd)
        {
            return false;
        }

        if (pointerCoordinate < metrics.ThumbStart)
        {
            return PageBy(-1);
        }

        return pointerCoordinate >= metrics.ThumbEnd && PageBy(1);
    }
}

internal readonly record struct ListViewLayout(
    Rectangle Viewport,
    int ItemCount,
    int RowHeight,
    int RowGap,
    ScrollState Scroll)
{
    public int RowStride => RowHeight + RowGap;
    public VisibleRange VisibleItems => Scroll.VisibleRange;

    public static ListViewLayout Create(
        Rectangle viewport,
        int itemCount,
        int rowHeight,
        int rowGap,
        ScrollState scroll)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        if (rowHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowHeight));
        }

        if (rowGap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rowGap));
        }

        var visibleRows = viewport.Height <= 0
            ? 0
            : Math.Max(1, (viewport.Height + rowGap) / (rowHeight + rowGap));
        scroll.Configure(itemCount, visibleRows);
        return new ListViewLayout(viewport, Math.Max(0, itemCount), rowHeight, rowGap, scroll);
    }

    public Rectangle ItemBounds(int itemIndex)
    {
        var y = Viewport.Y + (itemIndex - Scroll.Offset) * RowStride;
        return new Rectangle(Viewport.X, y, Viewport.Width, RowHeight);
    }

    public bool TryGetItemAt(Point point, out int itemIndex)
    {
        itemIndex = -1;
        if (!Viewport.Contains(point) || RowStride <= 0)
        {
            return false;
        }

        var visibleRow = (point.Y - Viewport.Y) / RowStride;
        if ((point.Y - Viewport.Y) % RowStride >= RowHeight)
        {
            return false;
        }

        var candidate = Scroll.Offset + visibleRow;
        if (candidate < 0 || candidate >= ItemCount || !VisibleItems.Contains(candidate))
        {
            return false;
        }

        itemIndex = candidate;
        return true;
    }
}

internal readonly record struct GridViewLayout(
    Rectangle Viewport,
    int ItemCount,
    int Columns,
    int ItemWidth,
    int ItemHeight,
    int ColumnGap,
    int RowGap,
    ScrollState RowScroll)
{
    public int RowCount => (ItemCount + Columns - 1) / Columns;
    public int RowStride => ItemHeight + RowGap;
    public int ColumnStride => ItemWidth + ColumnGap;
    public int FirstVisibleItem => RowScroll.Offset * Columns;
    public int VisibleItemCount => Math.Max(0, Math.Min(ItemCount - FirstVisibleItem, RowScroll.VisibleRange.Count * Columns));
    public VisibleRange VisibleItems => new(FirstVisibleItem, VisibleItemCount);

    public static GridViewLayout Create(
        Rectangle viewport,
        int itemCount,
        int columns,
        int itemWidth,
        int itemHeight,
        int columnGap,
        int rowGap,
        ScrollState rowScroll)
    {
        ArgumentNullException.ThrowIfNull(rowScroll);
        if (columns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (itemWidth <= 0 || itemHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(itemWidth <= 0 ? nameof(itemWidth) : nameof(itemHeight));
        }

        if (columnGap < 0 || rowGap < 0)
        {
            throw new ArgumentOutOfRangeException(columnGap < 0 ? nameof(columnGap) : nameof(rowGap));
        }

        var safeItemCount = Math.Max(0, itemCount);
        var rowCount = (safeItemCount + columns - 1) / columns;
        var visibleRows = viewport.Height <= 0
            ? 0
            : Math.Max(1, (viewport.Height + rowGap) / (itemHeight + rowGap));
        rowScroll.Configure(rowCount, visibleRows);
        return new GridViewLayout(viewport, safeItemCount, columns, itemWidth, itemHeight, columnGap, rowGap, rowScroll);
    }

    public Rectangle ItemBounds(int itemIndex)
    {
        var row = itemIndex / Columns;
        var column = itemIndex % Columns;
        return new Rectangle(
            Viewport.X + column * ColumnStride,
            Viewport.Y + (row - RowScroll.Offset) * RowStride,
            ItemWidth,
            ItemHeight);
    }

    public bool TryGetItemAt(Point point, out int itemIndex)
    {
        itemIndex = -1;
        if (!Viewport.Contains(point))
        {
            return false;
        }

        var relativeX = point.X - Viewport.X;
        var relativeY = point.Y - Viewport.Y;
        var column = relativeX / ColumnStride;
        var visibleRow = relativeY / RowStride;
        if (column >= Columns || relativeX % ColumnStride >= ItemWidth || relativeY % RowStride >= ItemHeight)
        {
            return false;
        }

        var candidate = (RowScroll.Offset + visibleRow) * Columns + column;
        if (candidate < 0 || candidate >= ItemCount || !VisibleItems.Contains(candidate))
        {
            return false;
        }

        itemIndex = candidate;
        return true;
    }

    public bool EnsureItemVisible(int itemIndex, ScrollAlignment alignment = ScrollAlignment.Nearest) =>
        ItemCount > 0 && RowScroll.EnsureVisible(Math.Clamp(itemIndex, 0, ItemCount - 1) / Columns, alignment);

    public int MoveFocus(int currentIndex, int columnDelta, int rowDelta)
    {
        if (ItemCount == 0)
        {
            return -1;
        }

        var current = Math.Clamp(currentIndex, 0, ItemCount - 1);
        var currentRow = current / Columns;
        var currentColumn = current % Columns;
        var targetRow = Math.Clamp(currentRow + rowDelta, 0, RowCount - 1);
        var targetColumn = Math.Clamp(currentColumn + columnDelta, 0, Columns - 1);
        return Math.Min(ItemCount - 1, targetRow * Columns + targetColumn);
    }
}

internal readonly record struct HorizontalStripLayout(
    Rectangle Viewport,
    int ItemCount,
    int ItemWidth,
    int ItemGap,
    ScrollState Scroll)
{
    public int ItemStride => ItemWidth + ItemGap;
    public VisibleRange VisibleItems => Scroll.VisibleRange;

    public static HorizontalStripLayout Create(
        Rectangle viewport,
        int itemCount,
        int itemWidth,
        int itemGap,
        ScrollState scroll,
        int? maximumVisibleItems = null)
    {
        ArgumentNullException.ThrowIfNull(scroll);
        if (itemWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemWidth));
        }

        if (itemGap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemGap));
        }

        var visibleItems = viewport.Width <= 0
            ? 0
            : Math.Max(1, (viewport.Width + itemGap) / (itemWidth + itemGap));
        if (maximumVisibleItems.HasValue)
        {
            visibleItems = Math.Min(visibleItems, Math.Max(0, maximumVisibleItems.Value));
        }

        scroll.Configure(itemCount, visibleItems);
        return new HorizontalStripLayout(viewport, Math.Max(0, itemCount), itemWidth, itemGap, scroll);
    }

    public Rectangle ItemBounds(int itemIndex)
    {
        var x = Viewport.X + (itemIndex - Scroll.Offset) * ItemStride;
        return new Rectangle(x, Viewport.Y, ItemWidth, Viewport.Height);
    }

    public bool TryGetItemAt(Point point, out int itemIndex)
    {
        itemIndex = -1;
        if (!Viewport.Contains(point))
        {
            return false;
        }

        var relativeX = point.X - Viewport.X;
        var visibleColumn = relativeX / ItemStride;
        if (relativeX % ItemStride >= ItemWidth)
        {
            return false;
        }

        var candidate = Scroll.Offset + visibleColumn;
        if (candidate < 0 || candidate >= ItemCount || !VisibleItems.Contains(candidate))
        {
            return false;
        }

        itemIndex = candidate;
        return true;
    }

    public bool EnsureItemVisible(int itemIndex, ScrollAlignment alignment = ScrollAlignment.Nearest) =>
        ItemCount > 0 && Scroll.EnsureVisible(Math.Clamp(itemIndex, 0, ItemCount - 1), alignment);
}
