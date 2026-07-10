namespace DragonCards.Desktop;

internal enum UiFocusOrigin
{
    Programmatic,
    Pointer,
    Keyboard,
    Controller
}

internal sealed class UiFocusState
{
    private int _index = -1;

    public int ItemCount { get; private set; }
    public int Index => _index;
    public bool HasFocus => _index >= 0 && _index < ItemCount;
    public UiFocusOrigin LastOrigin { get; private set; } = UiFocusOrigin.Programmatic;

    public void Configure(int itemCount)
    {
        ItemCount = Math.Max(0, itemCount);
        _index = ItemCount == 0 ? -1 : Math.Clamp(_index, 0, ItemCount - 1);
    }

    public bool Set(int index, UiFocusOrigin origin, ScrollState? scroll = null)
    {
        if (ItemCount == 0)
        {
            _index = -1;
            LastOrigin = origin;
            return false;
        }

        var next = Math.Clamp(index, 0, ItemCount - 1);
        if (next == _index)
        {
            LastOrigin = origin;
            return false;
        }

        _index = next;
        LastOrigin = origin;
        if (origin is UiFocusOrigin.Keyboard or UiFocusOrigin.Controller)
        {
            scroll?.EnsureVisible(_index);
        }

        return true;
    }

    public bool Move(int delta, UiFocusOrigin origin, ScrollState? scroll = null, bool wrap = false)
    {
        if (ItemCount == 0 || delta == 0)
        {
            LastOrigin = origin;
            return false;
        }

        var requested = (long)_index + delta;
        var next = wrap
            ? (int)((requested % ItemCount + ItemCount) % ItemCount)
            : (int)Math.Clamp(requested, 0L, ItemCount - 1L);
        return Set(next, origin, scroll);
    }

    public void Clear()
    {
        ItemCount = 0;
        _index = -1;
        LastOrigin = UiFocusOrigin.Programmatic;
    }
}
